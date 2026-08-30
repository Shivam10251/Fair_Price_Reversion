// =============================================================================
//  FPMR · Core · StructureEngine
//
//  Direct port of the Pine market-structure state machine (sections 5 and 6).
//  Exactly ONE active high and ONE active low exist at any moment. A newer
//  confirmed pivot replaces the older one; a broken level is consumed so the
//  same level can never fire twice.
//
//  The per-bar call order below is load-bearing and mirrors the Pine script:
//     1. BeginBar   — reset / seed / regime tracking
//     2. OnPivotHigh / OnPivotLow  — newly CONFIRMED swings
//     3. ExpireSetups
//     4. DetectBreak
//  Changing that order changes the signals.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public struct StructureBreak
	{
		public int           Direction;    // -1 bearish, +1 bullish, 0 none
		public FpBreakEvent  Event;
		public double        BrokenLevel;
		public int           BrokenBarIndex; // absolute bar index of the swing that made the level

		public bool Occurred { get { return Direction != 0; } }
	}

	public struct PivotAccepted
	{
		public bool        IsNew;
		public FpSwingRole Role;
		public double      Price;
		public int         PivotBarIndex;
		public bool        BecameActiveLevel;
	}

	public sealed class StructureEngine
	{
		private readonly FpActiveLevelMode _mode;

		public FpStructState State { get; private set; }

		public double      ActiveHigh      { get; private set; }
		public FpSwingRole ActiveHighRole  { get; private set; }
		public int         ActiveHighConf  { get; private set; }
		public int         ActiveHighBar   { get; private set; }

		public double      ActiveLow       { get; private set; }
		public FpSwingRole ActiveLowRole   { get; private set; }
		public int         ActiveLowConf   { get; private set; }
		public int         ActiveLowBar    { get; private set; }

		public string LastChoch { get; private set; }
		public string LastBos   { get; private set; }

		private double _prevPivotHigh = double.NaN;
		private double _prevPivotLow  = double.NaN;
		private int    _lastPos;

		public StructureEngine(FpActiveLevelMode mode)
		{
			_mode     = mode;
			LastChoch = "-";
			LastBos   = "-";
			HardReset(0);
		}

		public bool HasActiveHigh { get { return !double.IsNaN(ActiveHigh); } }
		public bool HasActiveLow  { get { return !double.IsNaN(ActiveLow); } }

		/// <summary>Age in bars of the newest active level, or -1 when there is none.</summary>
		public int ActiveLevelAge(int barIndex)
		{
			int newest = -1;
			if (HasActiveHigh) newest = Math.Max(newest, ActiveHighConf);
			if (HasActiveLow)  newest = Math.Max(newest, ActiveLowConf);
			return newest < 0 ? -1 : barIndex - newest;
		}

		/// <summary>
		/// Step 1 of the bar. Returns true when a reset fired, so the caller can log it.
		/// <paramref name="posState"/> is +1 above the zone, -1 below it, 0 inside / no Fair Price.
		/// </summary>
		public bool BeginBar(int posState, bool sessionStart, bool fairPriceLost)
		{
			bool regimeFlip = posState != 0 && posState != _lastPos;
			bool doReset    = sessionStart || regimeFlip || fairPriceLost;

			if (doReset)
				HardReset(posState);

			// Seed the state the first time price is clearly on one side of the zone.
			if (State == FpStructState.Undefined && posState != 0)
				State = (FpStructState)posState;

			if (posState != 0)
				_lastPos = posState;

			return doReset;
		}

		private void HardReset(int posState)
		{
			State          = (FpStructState)posState;
			ActiveHigh     = double.NaN;
			ActiveLow      = double.NaN;
			ActiveHighRole = FpSwingRole.None;
			ActiveLowRole  = FpSwingRole.None;
			ActiveHighConf = -1;
			ActiveLowConf  = -1;
			ActiveHighBar  = -1;
			ActiveLowBar   = -1;
			_prevPivotHigh = double.NaN;
			_prevPivotLow  = double.NaN;
			_lastPos       = 0;
		}

		/// <summary>Full clear used on strategy restart. Keeps no memory of the previous run.</summary>
		public void ResetAll()
		{
			HardReset(0);
			LastChoch = "-";
			LastBos   = "-";
		}

		/// <summary>Step 2a. Feed a newly CONFIRMED pivot high.</summary>
		public PivotAccepted OnPivotHigh(double price, int pivotBarIndex, int confirmBarIndex)
		{
			FpSwingRole role = double.IsNaN(_prevPivotHigh)
				? (State == FpStructState.Bearish ? FpSwingRole.LH : FpSwingRole.HH)
				: (price > _prevPivotHigh ? FpSwingRole.HH : FpSwingRole.LH);

			bool roleOk = _mode == FpActiveLevelMode.LatestSwing
			              || (State == FpStructState.Bullish && role == FpSwingRole.HH)
			              || (State == FpStructState.Bearish && role == FpSwingRole.LH)
			              || !HasActiveHigh;

			if (roleOk)
			{
				ActiveHigh     = price;
				ActiveHighRole = role;
				ActiveHighConf = confirmBarIndex;
				ActiveHighBar  = pivotBarIndex;
			}

			_prevPivotHigh = price;

			return new PivotAccepted
			{
				IsNew             = true,
				Role              = role,
				Price             = price,
				PivotBarIndex     = pivotBarIndex,
				BecameActiveLevel = roleOk
			};
		}

		/// <summary>Step 2b. Feed a newly CONFIRMED pivot low.</summary>
		public PivotAccepted OnPivotLow(double price, int pivotBarIndex, int confirmBarIndex)
		{
			FpSwingRole role = double.IsNaN(_prevPivotLow)
				? (State == FpStructState.Bullish ? FpSwingRole.HL : FpSwingRole.LL)
				: (price > _prevPivotLow ? FpSwingRole.HL : FpSwingRole.LL);

			bool roleOk = _mode == FpActiveLevelMode.LatestSwing
			              || (State == FpStructState.Bullish && role == FpSwingRole.HL)
			              || (State == FpStructState.Bearish && role == FpSwingRole.LL)
			              || !HasActiveLow;

			if (roleOk)
			{
				ActiveLow     = price;
				ActiveLowRole = role;
				ActiveLowConf = confirmBarIndex;
				ActiveLowBar  = pivotBarIndex;
			}

			_prevPivotLow = price;

			return new PivotAccepted
			{
				IsNew             = true,
				Role              = role,
				Price             = price,
				PivotBarIndex     = pivotBarIndex,
				BecameActiveLevel = roleOk
			};
		}

		/// <summary>
		/// Step 3. Setup validity, measured in bars from the CONFIRMATION bar of the level.
		/// setupBars == 0 means levels never expire.
		/// </summary>
		public void ExpireSetups(int barIndex, int setupBars)
		{
			if (setupBars <= 0)
				return;

			if (HasActiveHigh && ActiveHighConf >= 0 && barIndex - ActiveHighConf > setupBars)
			{
				ActiveHigh     = double.NaN;
				ActiveHighRole = FpSwingRole.None;
			}

			if (HasActiveLow && ActiveLowConf >= 0 && barIndex - ActiveLowConf > setupBars)
			{
				ActiveLow     = double.NaN;
				ActiveLowRole = FpSwingRole.None;
			}
		}

		/// <summary>
		/// Step 4. The candle that closes (or wicks) through the active level IS the
		/// displacement candle. A single candle can never break structure both ways —
		/// the bearish break is evaluated first, exactly as in Pine.
		/// </summary>
		public StructureBreak DetectBreak(double breakUpPrice, double breakDownPrice, int barIndex)
		{
			StructureBreak result = new StructureBreak { Direction = 0, Event = FpBreakEvent.None, BrokenLevel = double.NaN, BrokenBarIndex = -1 };

			bool bearBreak = HasActiveLow  && ActiveLowConf  >= 0 && barIndex >= ActiveLowConf  && breakDownPrice < ActiveLow;
			bool bullBreak = HasActiveHigh && ActiveHighConf >= 0 && barIndex >= ActiveHighConf && breakUpPrice   > ActiveHigh;

			if (bearBreak)
				bullBreak = false;

			if (bearBreak)
			{
				result.Direction      = -1;
				result.Event          = State == FpStructState.Bullish ? FpBreakEvent.CHoCH : FpBreakEvent.BOS;
				result.BrokenLevel    = ActiveLow;
				result.BrokenBarIndex = ActiveLowBar;

				State         = FpStructState.Bearish;
				ActiveLow     = double.NaN;   // level consumed
				ActiveLowRole = FpSwingRole.None;

				string txt = "Bearish @ " + result.BrokenLevel.ToString("0.#####");
				if (result.Event == FpBreakEvent.CHoCH) LastChoch = txt; else LastBos = txt;
			}
			else if (bullBreak)
			{
				result.Direction      = 1;
				result.Event          = State == FpStructState.Bearish ? FpBreakEvent.CHoCH : FpBreakEvent.BOS;
				result.BrokenLevel    = ActiveHigh;
				result.BrokenBarIndex = ActiveHighBar;

				State          = FpStructState.Bullish;
				ActiveHigh     = double.NaN;
				ActiveHighRole = FpSwingRole.None;

				string txt = "Bullish @ " + result.BrokenLevel.ToString("0.#####");
				if (result.Event == FpBreakEvent.CHoCH) LastChoch = txt; else LastBos = txt;
			}

			return result;
		}
	}
}
