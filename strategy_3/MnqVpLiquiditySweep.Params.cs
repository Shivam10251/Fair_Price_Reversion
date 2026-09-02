// =============================================================================
//  MNQ VOLUME PROFILE LIQUIDITY SWEEP  —  NinjaTrader 8
//  Part 1 of 3 — user inputs.
//
//  Group 2 mirrors the supplied Pine indicator's own inputs one for one, so the
//  levels this strategy trades can be reconciled against the chart it came from.
// =============================================================================
#region Using declarations
using System;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.NinjaScript.Strategies.VPS;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class MnqVpLiquiditySweep : Strategy
	{
		private const string G_GEN  = "1 · General";
		private const string G_IND  = "2 · Indicator (matches the Pine inputs)";
		private const string G_SWP  = "3 · Sweep & Confirmation";
		private const string G_TGT  = "4 · Target";
		private const string G_BE   = "5 · Break-even";
		private const string G_SIZE = "6 · Sizing";
		private const string G_BT   = "7 · Backtest";
		private const string G_DBG  = "8 · Debug & Visuals";

		// ── 1 · GENERAL ───────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Enable longs", Description = "Low-side sweeps of VAL / RL.", GroupName = G_GEN, Order = 0)]
		public bool EnableLongs { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable shorts", Description = "High-side sweeps of VAH / RH.", GroupName = G_GEN, Order = 1)]
		public bool EnableShorts { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "No entries after (HHMM)", Description = "Hard cutoff for NEW entries, in the session timezone. Entries begin as soon as the range session has closed and RH/RL exist, so there is no separate start time. An open trade is still managed after the cutoff.", GroupName = G_GEN, Order = 2)]
		public string EntryCutoff { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max trades per day", Description = "0 = unlimited.", GroupName = G_GEN, Order = 4)]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "One trade at a time", GroupName = G_GEN, Order = 5)]
		public bool OneTradeAtATime { get; set; }

		// ── 2 · INDICATOR ─────────────────────────────────────────────────────────
		// These are the supplied indicator's own inputs. Changing them here changes
		// which levels the strategy sees, so they must match the chart's settings.
		[NinjaScriptProperty]
		[Display(Name = "Session timezone", Description = "The zone EVERY window below is typed in. Defaults to Asia/Kolkata.", GroupName = G_IND, Order = 0)]
		public string SessionTimeZone { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Times are SUMMER (auto-adjust for winter)", Description = "ON: type every window as its US-DST (summer) IST time and the strategy shifts it an hour later in winter automatically, so it stays pinned to the same US market hours all year. OFF: the times are taken literally and never move.", GroupName = G_IND, Order = 1)]
		public bool AutoAdjustForUsDst { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Profile session (VAH/POC/VAL)", Description = "HHMM-HHMM in the session timezone. 1900-0130 IST summer = 0930-1600 New York.", GroupName = G_IND, Order = 2)]
		public string ProfileSession { get; set; }

		[NinjaScriptProperty]
		[Range(5, 200)]
		[Display(Name = "Rows (price bins)", Description = "The indicator's default is 60.", GroupName = G_IND, Order = 4)]
		public int ProfileBins { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, 100.0)]
		[Display(Name = "Value area %", Description = "The indicator's default is 70.", GroupName = G_IND, Order = 5)]
		public double ValueAreaPercent { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Range session (RH/RL)", Description = "HHMM-HHMM in the session timezone. 0330-0530 IST summer = 1800-2000 New York, the first two hours of the Globex reopen. Entries begin when this closes.", GroupName = G_IND, Order = 3)]
		public string RangeSession { get; set; }

		// ── 3 · SWEEP & CONFIRMATION ──────────────────────────────────────────────
		// The confirmation is a single rule: after a level is swept, a candle must
		// close in the rejecting COLOUR *and* close back THROUGH the swept level —
		// red and below it for a short, green and above it for a long. The sweep
		// candle itself counts when it does both.
		[NinjaScriptProperty]
		[Range(0, 50)]
		[Display(Name = "Confirmation window (bars after the sweep)", Description = "How long a swept level stays armed waiting for its red/green rejection candle. 0 = the sweep candle only. 1 = the sweep candle or the one after it. Higher values keep the sweep armed longer.", GroupName = G_SWP, Order = 1)]
		public int SweepConfirmWindowBars { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Level proximity (ticks)", Description = "Applies only to a confirmation candle AFTER the sweep candle: it must still have traded within this band of the swept level, so an unrelated candle later in the window cannot confirm the setup. The sweep candle traded through the level by definition and is never proximity-tested.", GroupName = G_SWP, Order = 2)]
		public double LevelProximityTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Stop buffer (ticks beyond the sweep extreme)", Description = "Pushes the initial stop past the sweep candle's high/low so a one-tick poke does not take it out. 0 = exactly the sweep candle's extreme, as specified.", GroupName = G_SWP, Order = 3)]
		public double StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sweep VAH / VAL", Description = "Allow value-area levels to originate trades.", GroupName = G_SWP, Order = 4)]
		public bool SweepValueArea { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Sweep RH / RL", Description = "Allow range levels to originate trades.", GroupName = G_SWP, Order = 5)]
		public bool SweepRange { get; set; }

		// ── 4 · TARGET ────────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Tie-break when two targets are equidistant", Description = "Required to be deterministic and visible by the specification. Only applies on an exact tie.", GroupName = G_TGT, Order = 0)]
		public VpsTargetTieBreak TargetTieBreak { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Minimum target distance (ticks)", Description = "A target closer than this does not qualify. With every candidate disqualified the trade is skipped, per section 13.", GroupName = G_TGT, Order = 1)]
		public double MinTargetDistanceTicks { get; set; }

		// ── 5 · BREAK-EVEN ────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Move stop to break-even on an intermediate line", Description = "Touching any indicator line strictly between the entry and the target moves the stop to the exact entry fill. The entry line and the target line never trigger it.", GroupName = G_BE, Order = 0)]
		public bool UseBreakEven { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100000)]
		[Display(Name = "Break-even offset (ticks)", Description = "0 = the exact entry price, as specified. A positive value parks the stop this many ticks in profit instead.", GroupName = G_BE, Order = 1)]
		public double BreakEvenOffsetTicks { get; set; }

		// ── 6 · SIZING ────────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Sizing mode", Description = "Fixed is the specified behaviour. RiskBased sizes each trade so the dollar risk to the sweep-candle stop lands near the target below.", GroupName = G_SIZE, Order = 0)]
		public VpsSizingMode SizingMode { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Contracts (fixed mode)", GroupName = G_SIZE, Order = 1)]
		public int Contracts { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Risk per trade ($) (risk-based mode)", GroupName = G_SIZE, Order = 2)]
		public double RiskPerTradeUSD { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Risk hard cap ($) (risk-based mode)", Description = "Never exceeded. If one contract risks more than this the trade is skipped.", GroupName = G_SIZE, Order = 3)]
		public double RiskHardCapUSD { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max contracts", GroupName = G_SIZE, Order = 4)]
		public int MaxContracts { get; set; }

		// ── 7 · BACKTEST ──────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Slippage (ticks)", Description = "Applied by NinjaTrader to backtest fills. Commission is set on the account's commission template, not here.", GroupName = G_BT, Order = 0)]
		public int SlippageTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use tick precision", Description = "Resolves same-candle stop/target order from real 1-tick data instead of an assumption. Section 13 asks for exactly this; with it OFF NinjaTrader assumes the STOP filled first.", GroupName = G_BT, Order = 1)]
		public bool UseTickPrecision { get; set; }

		// ── 8 · DEBUG & VISUALS ───────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Verbose logging", Description = "Logs every level update, sweep, confirmation, target choice and break-even trigger.", GroupName = G_DBG, Order = 0)]
		public bool VerboseLogging { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show visuals", Description = "Draws the five levels, sweep marks, entry, stop, target and break-even trigger. OFF by default: per-bar drawing is the single biggest cost in a NinjaTrader backtest. Turn it on to review a chart, off to run.", GroupName = G_DBG, Order = 1)]
		public bool ShowVisuals { get; set; }

		/// <summary>Applies every default. Called from State.SetDefaults.</summary>
		private void ApplyDefaults()
		{
			EnableLongs     = true;
			EnableShorts    = true;
			EntryCutoff     = "1500";
			MaxTradesPerDay = 0;
			OneTradeAtATime = true;

			// Every window is typed in IST as its SUMMER (US-DST) time. The winter
			// equivalents are derived, not typed:
			//   profile 1900-0130 IST summer = 0930-1600 New York = 2000-0230 IST winter
			//   range   0330-0530 IST summer = 1800-2000 New York = 0430-0630 IST winter
			SessionTimeZone    = "Asia/Kolkata";
			AutoAdjustForUsDst = true;

			ProfileSession   = "1900-0130";
			ProfileBins      = 60;
			ValueAreaPercent = 70.0;
			RangeSession     = "0330-0530";

			SweepConfirmWindowBars = 1;
			LevelProximityTicks    = 20;
			StopBufferTicks        = 0;
			SweepValueArea         = true;
			SweepRange             = true;

			TargetTieBreak         = VpsTargetTieBreak.PreferRange;
			MinTargetDistanceTicks = 4;

			UseBreakEven         = true;
			BreakEvenOffsetTicks = 0;

			SizingMode      = VpsSizingMode.Fixed;
			Contracts       = 1;
			RiskPerTradeUSD = 100.0;
			RiskHardCapUSD  = 150.0;
			MaxContracts    = 10;

			SlippageTicks    = 0;
			UseTickPrecision = true;

			VerboseLogging = false;
			ShowVisuals    = false;
		}
	}
}
