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
using System.Text;
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
			double stop  = dir < 0
				? Instrument.MasterInstrument.RoundToTickSize(High[0] + StopBufferTicks * _tickSize)
				: Instrument.MasterInstrument.RoundToTickSize(Low[0]  - StopBufferTicks * _tickSize);

			double risk = dir < 0 ? stop - entry : entry - stop;

			SizingResult sizing = RiskSizer.Size(entry, stop, _pointValue,
				RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD, MaxContracts);

			GateState g = new GateState
			{
				Reconciled   = !_unreconciled,
				InSession    = _inTradingWindow,
				HasFairPrice = _hasFair,
				WarmupDone   = _warmupDone,
				InZone       = _insideZone,
				SideOk       = dir < 0 ? _posState == 1 : _posState == -1,
				DayCapOk     = MaxTradesPerDay     == 0 || _tradesDay     < MaxTradesPerDay,
				SessionCapOk = MaxTradesPerSession == 0 || _tradesSession < MaxTradesPerSession,
				FlatOk       = !OnlyOneOpenTrade || (_openTrades.Count == 0 && Position.MarketPosition == MarketPosition.Flat),
				EventOk      = brk.Event == FpBreakEvent.CHoCH ? TakeChochEntries : TakeBosEntries,
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

			SubmitEntry(dir, brk.Event, entry, stop, risk, sizing);
		}

		private bool EmaOkLong()   { return !UseEmaFilter || _emaFast[0] > _emaSlow[0]; }
		private bool EmaOkShort()  { return !UseEmaFilter || _emaFast[0] < _emaSlow[0]; }
		private bool VwapOkLong()  { return !UseVwapFilter || !_vwap.HasValue || Close[0] > _vwap.Value; }
		private bool VwapOkShort() { return !UseVwapFilter || !_vwap.HasValue || Close[0] < _vwap.Value; }

		private void RecordRejection(int dir, FpReject reason, SizingResult sizing)
		{
			string label = RejectionReporter.Label(reason);
			_lastRejectText = label;

			_viz.DrawRejection(dir, label, High[0], Low[0], ++_uid);

			if (!VerboseLogging)
				return;

			string extra = reason == FpReject.RiskCap || reason == FpReject.Risk
				? "  " + sizing.Describe(RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD)
				: string.Empty;

			Print(string.Format(CultureInfo.InvariantCulture, "{0}  REJECT {1} ({2} displacement){3}",
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"), label, dir < 0 ? "bearish" : "bullish", extra));
		}

		private void SubmitEntry(int dir, FpBreakEvent evt, double entry, double stop, double risk, SizingResult sizing)
		{
			ExtendedTpResult tpResult = _xtp.ComputeTakeProfit(dir, entry, risk, RewardRatio,
				_fairPrice, _hasFair, _xtpOffset, _tickSize);

			double target = Instrument.MasterInstrument.RoundToTickSize(tpResult.TakeProfit);

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
				StopPrice      = stop,
				TargetPrice    = target,
				Quantity       = sizing.Quantity,
				ExtendedTpUsed = tpResult.OverrideUsed,
				EntryBarIndex  = CurrentBar,
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

			if (tpResult.OverrideUsed)
				_xtp.Consume();

			_trades[signal] = rec;
			_openTrades.Add(rec);
			_tradesDay++;
			_tradesSession++;

			_viz.OpenTrade(rec);

			Print(string.Format(CultureInfo.InvariantCulture, "{0}  ENTRY {1}  |  {2}",
				Time[0].ToString("yyyy-MM-dd HH:mm:ss"),
				rec.Describe(),
				sizing.Describe(RiskTargetUSD, RiskToleranceUSD, RiskHardCapUSD)));
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

				_viz.UpdateTrade(t, 0);

				// The zone rule never closes an open trade — only session end can.
				if (sessionEnd && CloseAtSessionEnd && t.IsFilled)
				{
					if (t.Direction > 0)
						ExitLong(t.SignalName + "_EOS", t.SignalName);
					else
						ExitShort(t.SignalName + "_EOS", t.SignalName);
				}
			}

			for (int i = 0; i < _justClosed.Count; i++)
			{
				TradeRecord t = _justClosed[i];
				int barsAgo = t.ExitBarIndex < 0 ? 0 : Math.Max(0, CurrentBar - t.ExitBarIndex);
				_viz.CloseTrade(t, barsAgo);
			}
			_justClosed.Clear();
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
		                                          MarketPosition marketPosition, string orderId, bool isExit)
		{
			if (execution == null || execution.Order == null)
				return;

			Order order = execution.Order;
			if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled)
				return;

			TradeRecord rec;

			// Entry fill.
			if (_trades.TryGetValue(order.Name, out rec) && !rec.IsFilled)
			{
				rec.IsFilled       = true;
				rec.FillPrice      = execution.Price;
				rec.FilledQuantity = execution.Quantity;

				if (VerboseLogging)
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

			rec.ExitReason   = ClassifyExit(order.Name, rec);
			rec.ExitPrice    = execution.Price;
			rec.ExitTime     = execution.Time;
			rec.ExitBarIndex = CurrentBars[0];
			rec.IsClosed     = true;

			_lastExitText = rec.ExitReason + " @ " + rec.ExitPrice.ToString("0.#####", CultureInfo.InvariantCulture);

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

		// ── State dump ────────────────────────────────────────────────────────────
		private void DrawStatePanel(SessionEvaluation ev)
		{
			if (!ShowStatePanel)
				return;

			StringBuilder sb = new StringBuilder();
			sb.AppendLine("FAIR PRICE MEAN REVERSION");
			sb.AppendLine("Session       : " + (_effSession != 0
				? "S" + _effSession + (ev.InSession ? " active" : " (news pre-open)")
				: "closed"));
			sb.AppendLine("Fair Price    : " + (_hasFair
				? _fairPrice.ToString("0.#####", CultureInfo.InvariantCulture) + (_fair.IsNewsFairPrice ? "  [news]" : string.Empty)
				: "-"));
			sb.AppendLine("Zone          : " + (_hasFair
				? _zoneLower.ToString("0.#####", CultureInfo.InvariantCulture) + " / " + _zoneUpper.ToString("0.#####", CultureInfo.InvariantCulture)
				: "-"));
			sb.AppendLine("Distance      : " + (_hasFair
				? (Close[0] - _fairPrice).ToString("0.##", CultureInfo.InvariantCulture) + " pts"
				: "-"));
			sb.AppendLine("Regime        : " + (_posState == 1 ? "ABOVE zone (shorts)"
				: _posState == -1 ? "BELOW zone (longs)"
				: _hasFair ? "INSIDE zone (no new trades)" : "no Fair Price"));
			sb.AppendLine("Structure     : " + _structure.State);
			sb.AppendLine("Active high   : " + (_structure.HasActiveHigh
				? _structure.ActiveHighRole + " " + _structure.ActiveHigh.ToString("0.#####", CultureInfo.InvariantCulture) : "-"));
			sb.AppendLine("Active low    : " + (_structure.HasActiveLow
				? _structure.ActiveLowRole + " " + _structure.ActiveLow.ToString("0.#####", CultureInfo.InvariantCulture) : "-"));
			sb.AppendLine("Level age     : " + _structure.ActiveLevelAge(CurrentBar) + " bars");
			sb.AppendLine("Last CHoCH    : " + _structure.LastChoch);
			sb.AppendLine("Last BOS      : " + _structure.LastBos);
			sb.AppendLine("Warm-up       : " + (_warmupDone ? "done" : "blocking (" + Math.Max(0, MinBarsBeforeFirstTrade - (CurrentBar - Math.Max(0, _tradingStartBar))) + " bars left)"));
			sb.AppendLine("Open trades   : " + _openTrades.Count + (Position.MarketPosition != MarketPosition.Flat
				? "  (" + Position.MarketPosition + " " + Position.Quantity + ")" : string.Empty));
			sb.AppendLine("Trades today  : " + _tradesDay + (MaxTradesPerDay > 0 ? " / " + MaxTradesPerDay : string.Empty));
			sb.AppendLine("This session  : " + _tradesSession + (MaxTradesPerSession > 0 ? " / " + MaxTradesPerSession : string.Empty));
			sb.AppendLine("Last exit     : " + _lastExitText);
			sb.AppendLine("Last reject   : " + _lastRejectText);
			sb.AppendLine("Same-bar hits : " + _ambiguousCount + "  (" + SameBarPriority + ")");
			sb.AppendLine("EMA filter    : " + (!UseEmaFilter ? "OFF"
				: _emaFast[0] > _emaSlow[0] ? "bull (longs ok)" : "bear (shorts ok)"));
			sb.AppendLine("VWAP filter   : " + (!UseVwapFilter ? "OFF"
				: !_vwap.HasValue ? "no volume data" : Close[0] > _vwap.Value ? "above (longs ok)" : "below (shorts ok)"));
			sb.AppendLine("FP-TP override: " + (!UseExtendedTp ? "OFF"
				: (double.IsNaN(_xtp.DistancePercent) ? "-" : _xtp.DistancePercent.ToString("0.##", CultureInfo.InvariantCulture) + "% from FP · " + _xtp.Remaining + " armed")));
			sb.Append    ("News          : " + (!UseNewsFairPrice ? "OFF"
				: _news == null ? "FILE ERROR — normal Fair Price in use"
				: (_newsLoad != null ? _newsLoad.Events.Count + " events loaded" : "loaded")));

			_viz.DrawPanel(sb.ToString());
		}
	}
}
