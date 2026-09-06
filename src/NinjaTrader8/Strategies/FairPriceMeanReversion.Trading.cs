// =============================================================================
//  FAIR PRICE MEAN REVERSION  —  NinjaTrader 8
//  Part 3 of 3 — entry gating, order lifecycle, exits and diagnostics.
//
//  ORDER APPROACH (chosen deliberately, see docs/DESIGN_DECISIONS.md)
//  Managed orders with SetStopLoss / SetProfitTarget in CalculationMode.Price,
//  scoped to a unique per-trade entry signal name and submitted BEFORE the entry
//  order, with StopTargetHandling.PerEntryExecution. NinjaTrader then owns the
//  OCO pair for that entry, which is what makes concurrent trades safe and makes
//  the bracket survive a disconnect. Explicit ExitLongStopMarket/ExitLongLimit
//  would have required hand-rolling the OCO and the restart reconciliation.
// =============================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Strategies.FPMR;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class FairPriceMeanReversion : Strategy
	{
		private readonly Dictionary<string, TradeRecord> _trades     = new Dictionary<string, TradeRecord>();
		private readonly List<TradeRecord>               _openTrades = new List<TradeRecord>();
		private readonly List<TradeRecord>               _justClosed = new List<TradeRecord>();

		private int CountOpenTrades() { return _openTrades.Count; }

		private void DropAllOpenTrades(string reason)
		{
			foreach (TradeRecord t in _openTrades)
			{
				t.IsClosed   = true;
				t.ExitReason = reason;
			}
			_openTrades.Clear();
		}

		// ── Entry evaluation ──────────────────────────────────────────────────────
		private void EvaluateEntry(StructureBreak brk)
		{
			int    dir   = brk.Direction;
			double entry = Close[0];

			// Stop: a fixed number of points from the entry, or the displacement
			// candle's extreme. Fixed TP/SL, when on, overrides the structure stop.
			double stop = UseFixedTpSl
				? (dir < 0
					? Instrument.MasterInstrument.RoundToTickSize(entry + FixedStopLossPoints)
					: Instrument.MasterInstrument.RoundToTickSize(entry - FixedStopLossPoints))
				: (dir < 0
					? Instrument.MasterInstrument.RoundToTickSize(High[0] + StopBufferTicks * _tickSize)
					: Instrument.MasterInstrument.RoundToTickSize(Low[0]  - StopBufferTicks * _tickSize));

			double risk = dir < 0 ? stop - entry : entry - stop;

			// Which distance band the entry sits in. Fixed TP/SL bypasses the band
			// system for both gating (no "too far" limit) and target selection.
			FpSetupBand band = UseFixedTpSl
				? FpSetupBand.Near
				: SetupBands.Classify(entry, _fairPrice, _hasFair, ZonePercent, Band1Percent, Band2Percent);

			SizingResult sizing = RiskSizer.Size(entry, stop, _pointValue,
				RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD, MaxContracts);

			GateState g = new GateState
			{
				Reconciled   = !_unreconciled,
				InSession    = _inTradingWindow,
				HasFairPrice = _hasFair,
				WarmupDone   = _warmupDone,
				InZone       = _insideZone,
				DistanceOk   = band != FpSetupBand.Beyond,
				NewsReady    = !_newsAwaiting,
				SideOk       = SideAllowed(dir),
				DailyLossOk  = !_dayLossHit && LossHeadroomFor(sizing.ResultingRisk),
				DailyProfitOk= !_dayProfitHit,
				DayCapOk     = MaxTradesPerDay     == 0 || _tradesDay     < MaxTradesPerDay,
				SessionCapOk = MaxTradesPerSession == 0 || _tradesSession < MaxTradesPerSession,
				FlatOk       = !OnlyOneOpenTrade || (_openTrades.Count == 0 && Position.MarketPosition == MarketPosition.Flat),
				EventOk      = EventAllowed(brk.Event),
				EmaOk        = dir < 0 ? EmaOkShort() : EmaOkLong(),
				VwapOk       = dir < 0 ? VwapOkShort() : VwapOkLong(),
				RiskOk       = risk > 0 && risk >= MinStopTicks * _tickSize,
				RiskCapOk    = sizing.Accepted
			};

			FpReject reason = RejectionReporter.FirstFailure(g);

			if (reason != FpReject.None)
			{
				RecordRejection(dir, reason, sizing);
				return;
			}

			SubmitEntry(dir, brk.Event, entry, stop, risk, sizing, band);
		}

		/// <summary>
		/// Which side the current regime allows.
		///
		/// REVERSION: price above the Fair Price zone only permits shorts, below it
		/// only longs — the trade is always back toward Fair Price.
		///
		/// CONTINUATION: the rule inverts and the trade goes WITH the displacement,
		/// away from Fair Price. Two independent things ask for it:
		///   • the BOS-continuation ENTRY MODEL, the user choosing it for every session;
		///   • a large news surprise, where the initial move is treated as legitimate
		///     repricing rather than something to fade.
		/// Either is enough, so under the BOS-continuation model the news bias can no
		/// longer flip the direction back.
		/// </summary>
		private bool SideAllowed(int dir)
		{
			bool continuation = EntryModel == FpEntryModel.BosContinuation
			                    || _newsBias == FpNewsBias.Continuation;

			if (continuation)
				return dir < 0 ? _posState == -1 : _posState == 1;

			return dir < 0 ? _posState == 1 : _posState == -1;
		}

		/// <summary>
		/// Which break events may become a trade. The BOS-continuation model is BOS-only
		/// by definition, so a CHoCH is refused there whatever the CHoCH switch says.
		/// </summary>
		private bool EventAllowed(FpBreakEvent evt)
		{
			if (evt == FpBreakEvent.CHoCH)
				return EntryModel != FpEntryModel.BosContinuation && TakeChochEntries;

			return TakeBosEntries;
		}

		// ── Daily realised P&L budget ─────────────────────────────────────────────
		//
		// The limits are a bound on the day's NET result, not a switch that trips after
		// the fact. Two things are therefore needed:
		//
		//   1. A PREVENTIVE gate. Blocking new entries only once the limit is already
		//      breached lets the day overshoot by the whole risk of the trade that broke
		//      it: sitting at -$350 against a $400 limit, one more trade risking $100
		//      ends the day at -$450. The gate below refuses any trade whose worst case,
		//      added to what is already realised AND to what the open trades still have
		//      at risk, would push the day past the limit.
		//
		//   2. A REACTIVE latch, kept as a backstop. A stop can fill worse than its level
		//      on a gap, so the projection is a bound on intent, not a guarantee.

		/// <summary>Dollar loss still at risk across every open trade if each one stops out.</summary>
		private double OpenRiskUsd()
		{
			double risk = 0.0;

			for (int i = 0; i < _openTrades.Count; i++)
				risk += _openTrades[i].OpenRiskUsd(_pointValue);

			return risk;
		}

		/// <summary>
		/// True when taking a trade risking <paramref name="candidateRiskUsd"/> still leaves
		/// the day inside the loss limit even if it, and every open trade, hits its stop.
		/// </summary>
		private bool LossHeadroomFor(double candidateRiskUsd)
		{
			if (!UseDailyPnlLimits || DailyLossLimitUSD <= 0)
				return true;

			double worstCase = _realisedPnlDay - OpenRiskUsd() - Math.Max(0.0, candidateRiskUsd);
			return worstCase >= -DailyLossLimitUSD;
		}

		/// <summary>
		/// Moves the accumulator onto a new trading day. Keyed on the day value itself so
		/// that the bar loop and an exit fill arriving on the first bar of a new day cannot
		/// disagree — previously the exit was booked to the old day and then wiped by the
		/// reset, losing it from both.
		/// </summary>
		private void RollPnlDay(DateTime tradingDay)
		{
			if (tradingDay == _pnlDay)
				return;

			_pnlDay           = tradingDay;
			_realisedPnlDay   = 0.0;
			_dayLossHit       = false;
			_dayProfitHit     = false;
			_flattenRequested = false;
		}

		/// <summary>Adds money to the day and latches the limits if it crossed one.</summary>
		private void BookMoney(double amount, DateTime whenInBarZone)
		{
			RollPnlDay(TimeZoneRegistry.Convert(whenInBarZone, _barTz, _sessionTz).Date);

			_realisedPnlDay += amount;

			if (!UseDailyPnlLimits)
				return;

			bool wasStopped = _dayLossHit || _dayProfitHit;

			if (DailyLossLimitUSD > 0 && _realisedPnlDay <= -DailyLossLimitUSD)
				_dayLossHit = true;

			if (DailyProfitLimitUSD > 0 && _realisedPnlDay >= DailyProfitLimitUSD)
				_dayProfitHit = true;

			if (!wasStopped && (_dayLossHit || _dayProfitHit))
			{
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  DAILY {1} LIMIT HIT — net {2:0.##} realised for {3:yyyy-MM-dd}. No further entries today.{4}",
					Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
					_dayLossHit ? "LOSS" : "PROFIT",
					_realisedPnlDay,
					_pnlDay,
					FlattenOnDailyLimit ? " Flattening the open position." : " The open trade is left to reach its own stop or target."));

				if (FlattenOnDailyLimit)
					_flattenRequested = true;
			}
		}

		/// <summary>
		/// Books ONE exit execution. Called per execution rather than per trade, because a
		/// bracket can fill in pieces: booking the full entry quantity at the first partial
		/// exit's price over-counted the result and then ignored the remaining fills.
		/// </summary>
		private void BookExitFill(TradeRecord rec, Execution execution)
		{
			int entryQty  = rec.FilledQuantity > 0 ? rec.FilledQuantity : rec.Quantity;
			int remaining = entryQty - rec.ExitedQuantity;
			if (remaining <= 0)
				return;

			int qty = Math.Min(execution.Quantity, remaining);

			double gross = (execution.Price - rec.FillPrice) * rec.Direction * qty * _pointValue;
			double comm  = execution.Commission;
			double net   = gross - comm;

			rec.ExitedQuantity += qty;
			rec.RealisedPnl    += net;
			rec.Commission     += comm;

			BookMoney(net, execution.Time);
		}

		/// <summary>
		/// Closes anything still open once a limit has been breached and the user asked
		/// for that. Runs on the bar AFTER the booking so the exit order is submitted
		/// from OnBarUpdate rather than from inside an execution callback.
		/// </summary>
		private void ApplyDailyLimitFlatten()
		{
			if (!_flattenRequested)
				return;

			bool anyLeft = false;

			foreach (TradeRecord t in _openTrades)
			{
				if (!t.IsFilled || t.IsClosed)
					continue;

				anyLeft = true;

				if (t.Direction > 0)
					ExitLong(t.SignalName + "_LIMIT", t.SignalName);
				else
					ExitShort(t.SignalName + "_LIMIT", t.SignalName);
			}

			if (!anyLeft)
				_flattenRequested = false;
		}

		/// <summary>
		/// EMA gate. Each leg is independent:
		///   both legs on  -> crossover rule, long needs EMA1 above EMA2;
		///   one leg on    -> price-vs-EMA rule, long needs the close above that EMA;
		///   no leg on     -> no constraint, same as the master switch being off.
		/// A disabled leg is null, so the shape of the test follows what was built.
		/// </summary>
		private bool EmaOkLong()  { return EmaOk(true); }
		private bool EmaOkShort() { return EmaOk(false); }

		private bool EmaOk(bool isLong)
		{
			if (!UseEmaFilter)
				return true;

			bool one = _emaFast != null;
			bool two = _emaSlow != null;

			if (one && two)
				return isLong ? _emaFast[0] > _emaSlow[0] : _emaFast[0] < _emaSlow[0];

			if (one)
				return isLong ? Close[0] > _emaFast[0] : Close[0] < _emaFast[0];

			if (two)
				return isLong ? Close[0] > _emaSlow[0] : Close[0] < _emaSlow[0];

			return true;
		}

		private string DescribeEmaFilter()
		{
			if (!UseEmaFilter)
				return "OFF";

			bool one = _emaFast != null;
			bool two = _emaSlow != null;

			if (one && two)
				return "EMA" + EmaFastLength + " vs EMA" + EmaSlowLength + " · "
				     + (_emaFast[0] > _emaSlow[0] ? "bull (longs ok)" : "bear (shorts ok)");

			if (one)
				return "close vs EMA" + EmaFastLength + " · "
				     + (Close[0] > _emaFast[0] ? "above (longs ok)" : "below (shorts ok)");

			if (two)
				return "close vs EMA" + EmaSlowLength + " · "
				     + (Close[0] > _emaSlow[0] ? "above (longs ok)" : "below (shorts ok)");

			return "ON but no EMA enabled — no effect";
		}
		private bool VwapOkLong()  { return !UseVwapFilter || !_vwap.HasValue || Close[0] > _vwap.Value; }
		private bool VwapOkShort() { return !UseVwapFilter || !_vwap.HasValue || Close[0] < _vwap.Value; }

		private void RecordRejection(int dir, FpReject reason, SizingResult sizing)
		{
			string label = RejectionReporter.Label(reason);
			_lastRejectText = label;

			if (!VerboseLogging)
				return;

			string extra = reason == FpReject.RiskCap || reason == FpReject.Risk
				? "  " + sizing.Describe(RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD)
				: string.Empty;

			Print(string.Format(CultureInfo.InvariantCulture, "{0}  REJECT {1} ({2} displacement){3}",
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"), label, dir < 0 ? "bearish" : "bullish", extra));
		}

		private void SubmitEntry(int dir, FpBreakEvent evt, double entry, double stop, double risk,
		                         SizingResult sizing, FpSetupBand band)
		{
			// Take-profit selection:
			//   Fixed TP/SL on -> a fixed number of points from the entry.
			//   FAR band       -> Fair Price itself (target the full reversion).
			//   NEAR band      -> the Band 1 risk/reward multiple.
			//
			// The FAR band's Fair Price target only makes sense while the trade is
			// heading back toward Fair Price. Under the BOS-continuation model it runs
			// away from it, so Fair Price would sit behind the entry and never fill —
			// a FAR setup there takes the R:R multiple like a NEAR one.
			double rawTarget;
			bool   targetIsFair;

			if (UseFixedTpSl)
			{
				rawTarget    = dir < 0 ? entry - FixedTakeProfitPoints : entry + FixedTakeProfitPoints;
				targetIsFair = false;
			}
			else if (band == FpSetupBand.Far && EntryModel != FpEntryModel.BosContinuation)
			{
				rawTarget    = _fairPrice;
				targetIsFair = true;
			}
			else
			{
				rawTarget    = dir < 0 ? entry - risk * RewardRatio : entry + risk * RewardRatio;
				targetIsFair = false;
			}

			double target = Instrument.MasterInstrument.RoundToTickSize(rawTarget);

			// After rounding the target must still sit at least one tick beyond the
			// signal price, otherwise the bracket is nonsense.
			bool targetViable = dir < 0 ? target <= entry - _tickSize : target >= entry + _tickSize;
			if (!targetViable)
			{
				RecordRejection(dir, FpReject.Risk, sizing);
				return;
			}

			_tradeSeq++;
			string signal = "FPMR" + _tradeSeq.ToString(CultureInfo.InvariantCulture);

			TradeRecord rec = new TradeRecord
			{
				SignalName     = signal,
				Sequence       = _tradeSeq,
				Direction      = dir,
				Event          = evt,
				SignalPrice    = entry,
				StopPrice        = stop,
				InitialStopPrice = stop,
				TargetPrice       = target,
				Quantity          = sizing.Quantity,
				TargetIsFairPrice = targetIsFair,
				Trail             = UseFixedTpSl ? FpTrailMode.Off : TrailMode,
				MaxFavorablePoints = 0.0,
				EntryBarIndex     = CurrentBar,
				EntryBarTime   = Time[0],
				SessionIndex   = _effSession,
				Sizing         = sizing
			};

			// Brackets must be registered BEFORE the entry order they belong to.
			SetStopLoss(signal, CalculationMode.Price, stop, false);
			SetProfitTarget(signal, CalculationMode.Price, target);

			if (dir < 0)
				EnterShort(sizing.Quantity, signal);
			else
				EnterLong(sizing.Quantity, signal);

			_trades[signal] = rec;
			_openTrades.Add(rec);
			_tradesDay++;
			_tradesSession++;

			Print(string.Format(CultureInfo.InvariantCulture, "{0}  ENTRY {1}  |  {2}",
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
				rec.Describe(),
				sizing.Describe(RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD)));
		}

		// ── Trailing stops ────────────────────────────────────────────────────────
		//
		// Runs once per bar for every filled, open band trade whose trail mode is not
		// Off. The stop is only ever moved in the tightening direction — a level that
		// would loosen it is ignored — so the trail can never increase risk. Fixed
		// TP/SL trades carry Trail = Off and are never touched here.
		private void UpdateTrailingStops()
		{
			for (int i = 0; i < _openTrades.Count; i++)
			{
				TradeRecord t = _openTrades[i];

				if (t.Trail == FpTrailMode.Off || !t.IsFilled || t.IsClosed || double.IsNaN(t.FillPrice))
					continue;

				double newStop = t.Trail == FpTrailMode.RStep
					? RStepTrailStop(t)
					: StructureTrailStop(t);

				if (double.IsNaN(newStop))
					continue;

				// Tighten only: a long's stop may only rise, a short's may only fall.
				bool tighter = t.Direction > 0 ? newStop > t.StopPrice : newStop < t.StopPrice;
				if (!tighter)
					continue;

				t.StopPrice = newStop;
				SetStopLoss(t.SignalName, CalculationMode.Price, newStop, false);

				if (VerboseLogging)
					Print(string.Format(CultureInfo.InvariantCulture, "{0}  TRAIL {1} SL -> {2:0.#####} ({3})",
						Time[0].ToString("yyyy-MM-dd HH:mm:ss"), t.SignalName, newStop, t.Trail));
			}
		}

		/// <summary>
		/// Whole-R ratchet. R is the entry-to-initial-stop distance. Once price has
		/// reached +nR the stop sits at +(n-1)R from the entry — breakeven at n = 1.
		/// Uses the best favourable excursion so far, so a pullback never loosens it.
		/// </summary>
		private double RStepTrailStop(TradeRecord t)
		{
			double r = Math.Abs(t.FillPrice - t.InitialStopPrice);
			if (r <= 0.0)
				return double.NaN;

			double favor = t.Direction > 0 ? High[0] - t.FillPrice : t.FillPrice - Low[0];
			if (favor > t.MaxFavorablePoints)
				t.MaxFavorablePoints = favor;

			int steps = (int)Math.Floor(t.MaxFavorablePoints / r);
			if (steps < 1)
				return double.NaN;

			double stop = t.FillPrice + t.Direction * (steps - 1) * r;
			return Instrument.MasterInstrument.RoundToTickSize(stop);
		}

		/// <summary>
		/// Trails the latest CONFIRMED swing plus the SL buffer: the swing high for a
		/// short, the swing low for a long. The tighten-only rule in the caller is what
		/// turns "the latest swing" into "each lower high / higher low". A level that
		/// would sit on the wrong side of the current close is refused.
		/// </summary>
		private double StructureTrailStop(TradeRecord t)
		{
			double buffer = StopBufferTicks * _tickSize;

			if (t.Direction < 0)
			{
				if (double.IsNaN(_lastSwingHigh))
					return double.NaN;

				double stop = Instrument.MasterInstrument.RoundToTickSize(_lastSwingHigh + buffer);
				return stop > Close[0] ? stop : double.NaN;
			}

			if (double.IsNaN(_lastSwingLow))
				return double.NaN;

			double stopLong = Instrument.MasterInstrument.RoundToTickSize(_lastSwingLow - buffer);
			return stopLong < Close[0] ? stopLong : double.NaN;
		}

		// ── Open trade maintenance ────────────────────────────────────────────────
		private void MaintainOpenTrades(bool sessionEnd)
		{
			for (int i = _openTrades.Count - 1; i >= 0; i--)
			{
				TradeRecord t = _openTrades[i];

				// Same-candle TP/SL detection — reporting only; the fill itself comes
				// from the order fill resolution.
				bool ambiguous = t.Direction < 0
					? (High[0] >= t.StopPrice && Low[0] <= t.TargetPrice)
					: (Low[0] <= t.StopPrice && High[0] >= t.TargetPrice);

				if (ambiguous && !t.AmbiguousBarSeen)
				{
					t.AmbiguousBarSeen = true;
					_ambiguousCount++;
				}

				if (t.IsClosed)
				{
					_justClosed.Add(t);
					_openTrades.RemoveAt(i);
					continue;
				}

				// The zone rule never closes an open trade — only session end can.
				if (sessionEnd && CloseAtSessionEnd && t.IsFilled)
				{
					if (t.Direction > 0)
						ExitLong(t.SignalName + "_EOS", t.SignalName);
					else
						ExitShort(t.SignalName + "_EOS", t.SignalName);
				}
			}

			_justClosed.Clear();

			ApplyDailyLimitFlatten();
		}

		// ── Order lifecycle ───────────────────────────────────────────────────────
		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
		                                      int filled, double averageFillPrice, OrderState orderState,
		                                      DateTime time, ErrorCode error, string comment)
		{
			if (order == null)
				return;

			if (orderState != OrderState.Rejected && orderState != OrderState.Cancelled)
				return;

			TradeRecord rec;
			if (!_trades.TryGetValue(order.Name, out rec))
				return;

			if (orderState == OrderState.Rejected)
			{
				Log("FPMR: entry order " + order.Name + " was REJECTED (" + error + " " + comment + "). "
				  + "The trade record is being discarded.", LogLevel.Error);
			}

			if (!rec.IsFilled)
			{
				rec.IsClosed   = true;
				rec.ExitReason = orderState == OrderState.Rejected ? "REJECTED" : "CANCELLED";
				rec.ExitBarIndex = CurrentBars[0];

				// Never counted as a trade that happened.
				_tradesDay     = Math.Max(0, _tradesDay - 1);
				_tradesSession = Math.Max(0, _tradesSession - 1);
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
		                                          MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;

			Order order = execution.Order;
			if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled)
				return;

			TradeRecord rec;

			// Entry fill. An entry can fill in pieces, so the record keeps a volume-weighted
			// average price and a running quantity; taking only the first partial understated
			// both the position and every dollar figure derived from it.
			if (_trades.TryGetValue(order.Name, out rec))
			{
				bool firstFill = !rec.IsFilled;

				int    prevQty = rec.FilledQuantity;
				int    newQty  = prevQty + execution.Quantity;

				rec.FillPrice = firstFill || newQty <= 0
					? execution.Price
					: (rec.FillPrice * prevQty + execution.Price * execution.Quantity) / newQty;

				rec.FilledQuantity = newQty;
				rec.IsFilled       = true;
				rec.Commission    += execution.Commission;

				// Entry commission is part of the day's net result the moment it is charged.
				BookMoney(-execution.Commission, execution.Time);

				if (firstFill && VerboseLogging)
				{
					double realisedRiskPoints = Math.Abs(rec.FillPrice - rec.StopPrice);
					Print(string.Format(CultureInfo.InvariantCulture,
						"{0}  FILL {1} at {2:0.#####} (signalled {3:0.#####}, slip {4:0.#####} pts) | "
					  + "actual risk ${5:0.##} | actual R:R {6:0.00}",
						execution.Time.ToString("yyyy-MM-dd HH:mm:ss"), rec.SignalName, rec.FillPrice, rec.SignalPrice,
						Math.Abs(rec.FillPrice - rec.SignalPrice),
						realisedRiskPoints * _pointValue * rec.FilledQuantity,
						realisedRiskPoints > 0 ? Math.Abs(rec.TargetPrice - rec.FillPrice) / realisedRiskPoints : 0.0));
				}
				return;
			}

			// Exit fill for a tracked entry.
			string from = order.FromEntrySignal;
			if (string.IsNullOrEmpty(from) || !_trades.TryGetValue(from, out rec) || rec.IsClosed)
				return;

			BookExitFill(rec, execution);

			rec.ExitReason   = ClassifyExit(order.Name, rec);
			rec.ExitPrice    = execution.Price;
			rec.ExitTime     = execution.Time;
			rec.ExitBarIndex = CurrentBars[0];

			// Only closed once every filled contract has been exited; a partial exit leaves
			// the record open so the remainder is still counted as risk on the books.
			rec.IsClosed = rec.ExitedQuantity >= (rec.FilledQuantity > 0 ? rec.FilledQuantity : rec.Quantity);

			_lastExitText = rec.ExitReason + " @ " + rec.ExitPrice.ToString("0.#####", CultureInfo.InvariantCulture);

			if (!rec.IsClosed)
				return;

			if (VerboseLogging)
				Print(string.Format(CultureInfo.InvariantCulture, "{0}  EXIT {1} {2} at {3:0.#####}{4}",
					execution.Time.ToString("yyyy-MM-dd HH:mm:ss"), rec.SignalName, rec.ExitReason, rec.ExitPrice,
					rec.AmbiguousBarSeen ? "  (same-candle TP/SL — reported as " + SameBarPriority + ")" : string.Empty));
		}

		/// <summary>
		/// NinjaTrader names the orders generated by SetStopLoss / SetProfitTarget
		/// "Stop loss" and "Profit target". The same-candle preference only ever
		/// changes the REPORTED reason, never the fill.
		/// </summary>
		private string ClassifyExit(string orderName, TradeRecord rec)
		{
			string n = (orderName ?? string.Empty).ToLowerInvariant();

			string reason = n.Contains("stop")   ? "SL"
			              : n.Contains("profit") || n.Contains("target") ? "TP"
			              : n.Contains("eos")    ? "EOS"
			              : "EXIT";

			if (!rec.AmbiguousBarSeen || reason == "EOS" || reason == "EXIT")
				return reason;

			switch (SameBarPriority)
			{
				case FpSameBarPriority.SlFirst:     return "SL";
				case FpSameBarPriority.TpFirst:     return "TP";
				case FpSameBarPriority.BarDirection:
					return Close[0] >= Open[0]
						? (rec.Direction < 0 ? "TP" : "SL")
						: (rec.Direction < 0 ? "SL" : "TP");
				default:
					// TickSequence: trust the fill engine, which used real ticks when
					// UseTickPrecision is on.
					return reason;
			}
		}

	}
}
