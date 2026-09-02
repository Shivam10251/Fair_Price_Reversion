// =============================================================================
//  MNQ VOLUME PROFILE LIQUIDITY SWEEP  —  NinjaTrader 8
//  Part 3 of 3 — setup construction, entries, break-even and exits.
//
//  ORDER MECHANICS
//  Both bracket legs are absolute PRICES, not tick distances. The stop belongs
//  to the sweep candle's extreme and the target to an indicator level; anchoring
//  either to the fill would move it off the level it is defined by. Entries fill
//  at the next bar's open, so the realised risk differs from the risk measured
//  at the signal — the entry log prints both.
//
//  SAME-CANDLE EDGE CASES (specification section 13)
//    * Break-even is never evaluated on the entry bar itself.
//    * Stop versus target inside one candle is resolved by NinjaTrader's fill
//      engine, from real ticks when "Use tick precision" is on. With it off,
//      NinjaTrader assumes the STOP filled first — the conservative reading the
//      specification asks for.
//    * The break-even move is applied at the CLOSE of the bar that touched the
//      intermediate line. If the original stop was hit earlier inside that same
//      candle it has already filled, so a break-even move can never rescue a
//      trade that was stopped first. That is conservative by construction.
// =============================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Strategies.VPS;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class MnqVpLiquiditySweep : Strategy
	{
		// ── Active trade ──────────────────────────────────────────────────────────
		private bool     _gateWindow, _gateDayCap, _gateFlat;
		private string   _activeSignal = string.Empty;
		private int      _activeDirection;
		private VpsLevel _entryLine;
		private VpsLevel _targetLine;
		private double   _plannedStop;
		private double   _plannedTarget;
		private double   _signalPrice;
		private double   _entryFill;
		private int      _entryBar = -1;
		private bool     _breakEvenArmed;
		private bool     _inTrade;
		private List<VpsLevelValue> _intermediates = new List<VpsLevelValue>();

		// ── Setup evaluation ──────────────────────────────────────────────────────
		private void EvaluateSetups(DateTime barOpenInBarZone)
		{
			if (!_levels.AnyHighSide)
				return;

			_diagBarsWithLevels++;

			// A sweep is detected regardless of the trading window so the state
			// machine stays continuous; only the ENTRY is gated by the window.
			// The three gates are kept separate so a barren run can name which one bit.
			_gateWindow = InTradingWindow(barOpenInBarZone);
			_gateDayCap = MaxTradesPerDay == 0 || _tradesDay < MaxTradesPerDay;
			_gateFlat   = !OneTradeAtATime || (!_inTrade && Position.MarketPosition == MarketPosition.Flat);

			bool canEnter = _gateWindow && _gateDayCap && _gateFlat;

			if (canEnter)
				_diagBarsEntryAllowed++;

			// High side first, then low side. Within a side the value area is tested
			// before the range, so a bar that sweeps both resolves deterministically.
			if (EnableShorts)
			{
				if (SweepValueArea && TryLevel(VpsLevel.Vah, -1, canEnter)) return;
				if (SweepRange     && TryLevel(VpsLevel.Rh,  -1, canEnter)) return;
			}

			if (EnableLongs)
			{
				if (SweepValueArea && TryLevel(VpsLevel.Val, +1, canEnter)) return;
				if (SweepRange     && TryLevel(VpsLevel.Rl,  +1, canEnter)) return;
			}
		}

		/// <summary>Detects a fresh sweep of one level and tests both confirmations.</summary>
		private bool TryLevel(VpsLevel level, int direction, bool canEnter)
		{
			if (!_levels.IsValid(level))
				return false;

			double levelPrice = _levels.PriceOf(level);

			VpsArmedSweep fresh = _sweeps.DetectSweep(level, direction, levelPrice,
				High[0], Low[0], High[1], Low[1], CurrentBar, Time[0]);

			if (fresh != null)
				_diagSweeps++;

			if (fresh != null && VerboseLogging)
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  SWEEP {1} at {2} | candle H {3} L {4} C {5}",
					Time[0], level, levelPrice.ToString(_tickFormat, CultureInfo.InvariantCulture),
					High[0].ToString(_tickFormat, CultureInfo.InvariantCulture),
					Low[0].ToString(_tickFormat, CultureInfo.InvariantCulture),
					Close[0].ToString(_tickFormat, CultureInfo.InvariantCulture)));

			if (fresh != null && ShowVisuals)
				Draw.Text(this, "SW" + CurrentBar + level, level.ToString(), 0,
					direction < 0 ? High[0] + 4 * _tickSize : Low[0] - 4 * _tickSize,
					direction < 0 ? Brushes.OrangeRed : Brushes.MediumSeaGreen);

			VpsArmedSweep armed = _sweeps.ArmedFor(level);
			if (armed == null)
				return false;

			// ---- Method A: the sweep candle rejected the level -----------------
			bool rejectionAllowed = ConfirmMode != VpsConfirmMode.EngulfingOnly;

			if (rejectionAllowed && armed.SweepBar == CurrentBar
			    && VpsSweepTracker.IsRejection(direction, Close[0], levelPrice))
			{
				return Confirm(armed, VpsConfirmation.Rejection, canEnter);
			}

			// ---- Method B: an engulfing candle after the sweep -----------------
			bool engulfAllowed = ConfirmMode != VpsConfirmMode.RejectionOnly;

			if (engulfAllowed && CurrentBar > armed.SweepBar
			    && _sweeps.IsEngulfingConfirmation(direction, armed.LevelPrice,
			           Open[0], High[0], Low[0], Close[0], Open[1], Close[1]))
			{
				return Confirm(armed, VpsConfirmation.Engulfing, canEnter);
			}

			return false;
		}

		/// <summary>Turns a confirmed sweep into an order, or explains why it was skipped.</summary>
		private bool Confirm(VpsArmedSweep armed, VpsConfirmation how, bool canEnter)
		{
			// The sweep is consumed either way: one liquidity event, one decision.
			_sweeps.Clear(armed.Level);
			_diagConfirmations++;

			if (!canEnter)
			{
				if      (!_gateWindow) _diagSkipWindow++;
				else if (!_gateDayCap) _diagSkipDayCap++;
				else                   _diagSkipInTrade++;

				if (VerboseLogging)
					Print(string.Format(CultureInfo.InvariantCulture,
						"{0}  SKIP {1} {2} - {3}.",
						Time[0], armed.Level, how,
						!_gateWindow ? "outside the entry window"
						: !_gateDayCap ? "the daily trade cap is reached"
						: "a trade is already open"));
				return false;
			}

			double entry = Close[0];
			int    dir   = armed.Direction;

			// ---- initial stop: the SWEEP candle's extreme, never the engulfing
			//      candle's, unless they are the same candle ---------------------
			double buffer = Math.Max(0.0, StopBufferTicks) * _tickSize;
			double stop   = Instrument.MasterInstrument.RoundToTickSize(
				dir < 0 ? armed.SweepExtreme + buffer : armed.SweepExtreme - buffer);

			double riskPoints = dir < 0 ? stop - entry : entry - stop;
			if (riskPoints < _tickSize)
			{
				_diagSkipRisk++;

				if (VerboseLogging)
					Print(string.Format(CultureInfo.InvariantCulture,
						"{0}  SKIP {1} {2} — the sweep candle's extreme is less than a tick from the entry.",
						Time[0], armed.Level, how));
				return false;
			}

			// ---- dynamic maximum-distance target ------------------------------
			VpsTargetChoice target = _levels.SelectTarget(dir, entry,
				MinTargetDistanceTicks * _tickSize, TargetTieBreak);

			if (!target.Found)
			{
				_diagSkipNoTarget++;

				// Section 13: no valid opposite-side target means no trade.
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  SKIP {1} {2} — {3}. No entry taken.",
					Time[0], armed.Level, how, target.Rejection));
				return false;
			}

			// ---- sizing -------------------------------------------------------
			int quantity;
			string sizeReason;
			if (!TrySize(riskPoints, out quantity, out sizeReason))
			{
				_diagSkipSize++;

				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  SKIP {1} {2} — {3}", Time[0], armed.Level, how, sizeReason));
				return false;
			}

			_diagEntries++;
			SubmitEntry(armed, how, dir, entry, stop, target, quantity, riskPoints);
			return true;
		}

		private void SubmitEntry(VpsArmedSweep armed, VpsConfirmation how, int dir, double entry,
		                         double stop, VpsTargetChoice target, int quantity, double riskPoints)
		{
			_tradeSeq++;
			string signal = "VPS" + _tradeSeq.ToString(CultureInfo.InvariantCulture);

			_activeSignal    = signal;
			_activeDirection = dir;
			_entryLine       = armed.Level;
			_targetLine      = target.Id;
			_plannedStop     = stop;
			_plannedTarget   = target.Price;
			_signalPrice     = entry;
			_entryFill       = double.NaN;
			_entryBar        = -1;
			_breakEvenArmed  = false;
			_inTrade         = true;

			// Intermediate lines are fixed at signal time from the levels in force.
			_intermediates = _levels.IntermediateLines(_entryLine, _targetLine, entry, target.Price);

			// Brackets must be registered BEFORE the entry order they belong to.
			SetStopLoss(signal, CalculationMode.Price, stop, false);
			SetProfitTarget(signal, CalculationMode.Price, target.Price);

			if (dir < 0)
				EnterShort(quantity, signal);
			else
				EnterLong(quantity, signal);

			_tradesDay++;

			string names = string.Empty;
			for (int i = 0; i < _intermediates.Count; i++)
				names += (i > 0 ? ", " : string.Empty) + _intermediates[i].Id;

			Print(string.Format(CultureInfo.InvariantCulture,
				"{0}  ENTRY #{1} {2} {3} x{4} @ {5} | from {6} ({7}) | SL {8} (sweep candle {9:HH:mm}) | "
			  + "TP {10} at {11}, {12} pts — the farther of the two | BE lines: {13} | risk {14:0.##} pts",
				Time[0], _tradeSeq, dir < 0 ? "SHORT" : "LONG", Instrument.MasterInstrument.Name, quantity,
				entry.ToString(_tickFormat, CultureInfo.InvariantCulture),
				armed.Level, how,
				stop.ToString(_tickFormat, CultureInfo.InvariantCulture), armed.SweepTime,
				target.Id, target.Price.ToString(_tickFormat, CultureInfo.InvariantCulture), target.Distance,
				_intermediates.Count == 0 ? "none" : names,
				riskPoints));

			if (ShowVisuals)
			{
				Draw.ArrowDown(this, "EN" + _tradeSeq, false, 0, entry + 6 * _tickSize, Brushes.OrangeRed);
				Draw.Line(this, "SL" + _tradeSeq, false, 0, stop, -10, stop, Brushes.Firebrick, DashStyleHelper.Dash, 2);
				Draw.Line(this, "TP" + _tradeSeq, false, 0, target.Price, -10, target.Price, Brushes.SeaGreen, DashStyleHelper.Dash, 2);
			}
		}

		// ── Sizing ────────────────────────────────────────────────────────────────
		private bool TrySize(double riskPoints, out int quantity, out string reason)
		{
			quantity = 0;
			reason   = string.Empty;

			if (SizingMode == VpsSizingMode.Fixed)
			{
				quantity = Math.Max(1, Contracts);
				return true;
			}

			double riskPerContract = riskPoints * _pointValue;
			if (riskPerContract <= 0)
			{
				reason = "the per-contract risk could not be computed";
				return false;
			}

			int qty = (int)Math.Round(RiskPerTradeUSD / riskPerContract, MidpointRounding.AwayFromZero);
			qty = Math.Max(1, Math.Min(MaxContracts, qty));

			while (RiskHardCapUSD > 0 && qty * riskPerContract > RiskHardCapUSD && qty > 1)
				qty--;

			if (RiskHardCapUSD > 0 && qty * riskPerContract > RiskHardCapUSD)
			{
				reason = string.Format(CultureInfo.InvariantCulture,
					"one contract risks {0:C} at a {1:0.##} point stop, above the {2:C} hard cap",
					riskPerContract, riskPoints, RiskHardCapUSD);
				return false;
			}

			quantity = qty;
			return true;
		}

		// ── Break-even ────────────────────────────────────────────────────────────
		private void ManageOpenTrade()
		{
			if (!_inTrade || !UseBreakEven || _breakEvenArmed)
				return;

			// Not established yet, or this is the entry bar itself (section 13).
			if (double.IsNaN(_entryFill) || _entryBar < 0 || CurrentBar <= _entryBar)
				return;

			if (Position.MarketPosition == MarketPosition.Flat)
				return;

			for (int i = 0; i < _intermediates.Count; i++)
			{
				VpsLevelValue v = _intermediates[i];

				// Touched: the bar's range reached the line.
				if (Low[0] > v.Price || High[0] < v.Price)
					continue;

				double offset = Math.Max(0.0, BreakEvenOffsetTicks) * _tickSize;
				double be = Instrument.MasterInstrument.RoundToTickSize(
					_activeDirection < 0 ? _entryFill - offset : _entryFill + offset);

				SetStopLoss(_activeSignal, CalculationMode.Price, be, false);
				_breakEvenArmed = true;
				_plannedStop    = be;

				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  BREAK-EVEN #{1} — price touched {2} at {3} between {4} and {5}. Stop moved to {6}.",
					Time[0], _tradeSeq, v.Id, v.Price.ToString(_tickFormat, CultureInfo.InvariantCulture),
					_entryLine, _targetLine, be.ToString(_tickFormat, CultureInfo.InvariantCulture)));

				if (ShowVisuals)
					Draw.Diamond(this, "BE" + _tradeSeq, false, 0, v.Price, Brushes.Gold);

				return;
			}
		}

		// ── Order lifecycle ───────────────────────────────────────────────────────
		protected override void OnExecutionUpdate(Execution execution, string executionId, double price, int quantity,
		                                          MarketPosition marketPosition, string orderId, DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;

			Order order = execution.Order;
			if (order.OrderState != OrderState.Filled && order.OrderState != OrderState.PartFilled)
				return;

			// Entry fill: capture the ACTUAL price, because break-even is defined as
			// the exact entry and the specification is explicit about that.
			if (_inTrade && order.Name == _activeSignal)
			{
				if (double.IsNaN(_entryFill))
				{
					_entryFill = execution.Price;
					_entryBar  = CurrentBars[0];

					if (VerboseLogging)
						Print(string.Format(CultureInfo.InvariantCulture,
							"{0}  FILL #{1} at {2} (signalled {3}, slip {4:0.##} pts) | actual risk {5:0.##} pts",
							execution.Time, _tradeSeq,
							_entryFill.ToString(_tickFormat, CultureInfo.InvariantCulture),
							_signalPrice.ToString(_tickFormat, CultureInfo.InvariantCulture),
							Math.Abs(_entryFill - _signalPrice),
							Math.Abs(_entryFill - _plannedStop)));
				}
				return;
			}

			// Exit fill.
			string from = order.FromEntrySignal;
			if (!_inTrade || string.IsNullOrEmpty(from) || from != _activeSignal)
				return;

			if (Position.MarketPosition != MarketPosition.Flat)
				return;

			double pnlPoints = (execution.Price - _entryFill) * _activeDirection;

			Print(string.Format(CultureInfo.InvariantCulture,
				"{0}  EXIT #{1} {2} at {3} | {4:0.##} pts | {5}",
				execution.Time, _tradeSeq, ClassifyExit(order.Name),
				execution.Price.ToString(_tickFormat, CultureInfo.InvariantCulture),
				pnlPoints,
				_breakEvenArmed ? "break-even was armed" : "break-even never armed"));

			ResetTrade();
		}

		private string ClassifyExit(string orderName)
		{
			string n = (orderName ?? string.Empty).ToLowerInvariant();

			if (n.Contains("profit") || n.Contains("target"))
				return "TARGET";

			if (n.Contains("stop"))
				return _breakEvenArmed ? "BREAK-EVEN" : "STOP";

			return "EXIT";
		}

		private void ResetTrade()
		{
			_inTrade         = false;
			_activeSignal    = string.Empty;
			_activeDirection = 0;
			_entryFill       = double.NaN;
			_entryBar        = -1;
			_breakEvenArmed  = false;
			_intermediates.Clear();
		}

		// ── Trading window ────────────────────────────────────────────────────────
		/// <summary>
		/// Entries are permitted from the moment the range session has closed — which
		/// is when RH/RL exist and the level set is complete — until the cutoff.
		///
		/// There is deliberately no separate "entries from" time. Tying the floor to
		/// the range close means moving the range window moves the floor with it, so
		/// the two can never drift out of sync, and it moves with DST for free.
		/// </summary>
		private bool InTradingWindow(DateTime barOpenInBarZone)
		{
			// Floor: the range must have produced RH/RL, and we must be past it.
			if (!_levels.HasRange || _inRange)
				return false;

			if (_entryCutoff == null || !_entryCutoff.IsValid)
				return true;

			// The cutoff window runs 0000-HHMM, so being inside it means "not yet past".
			return _entryCutoff.Contains(VpsTimeZone.Convert(barOpenInBarZone, _barTz, _entryCutoff.Zone));
		}

		// ── Visuals ───────────────────────────────────────────────────────────────
		private void DrawLevels()
		{
			if (_levels.HasProfile)
			{
				DrawLevel("VAH", _levels.Vah, Brushes.Silver);
				DrawLevel("POC", _levels.Poc, Brushes.DarkKhaki);
				DrawLevel("VAL", _levels.Val, Brushes.Silver);
			}

			if (_levels.HasRange)
			{
				DrawLevel("RH", _levels.Rh, Brushes.Teal);
				DrawLevel("RL", _levels.Rl, Brushes.Teal);
			}
		}

		private void DrawLevel(string tag, double price, Brush brush)
		{
			Draw.Line(this, tag, false, 30, price, -5, price, brush, DashStyleHelper.Solid, 1);
		}
	}
}
