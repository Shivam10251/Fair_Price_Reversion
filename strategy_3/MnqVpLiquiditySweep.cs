// =============================================================================
//  MNQ VOLUME PROFILE LIQUIDITY SWEEP  —  NinjaTrader 8
//  Part 2 of 3 — lifecycle and per-bar orchestration.
//
//  HIGH-SIDE SWEEP -> SHORT     VAH or RH swept, bearish rejection OR bearish
//                               engulfing, short.
//  LOW-SIDE SWEEP  -> LONG      VAL or RL swept, bullish rejection OR bullish
//                               engulfing, long.
//  TARGET IS DYNAMIC            The farthest qualifying opposite-side level from
//                               the actual entry price. Never a fixed R.
//  BREAK-EVEN                   Any OTHER indicator line between entry and
//                               target, once touched, moves the stop to the
//                               exact entry fill.
//
//  WHERE THE LEVELS COME FROM
//  The supplied indicator is TradingView Pine and cannot be called from
//  NinjaScript, so VolumeProfileEngine is a line-for-line port of its
//  f_profile() function rather than an approximation. See that file.
//
//  WHEN THE LEVELS BECOME VALID — this drives everything downstream.
//  The Pine indicator computes VAH/POC/VAL only when the profile session ENDS
//  (doCalc = vpEnd or barstate.islast). It therefore never trades against a
//  half-built profile, and neither does this strategy: the levels in force are
//  always those of the most recently COMPLETED profile session, and RH/RL those
//  of the most recently COMPLETED range. Before the first of each has closed
//  there are no levels and no trades are possible.
//
//  NO LOOK-AHEAD
//  Calculate = OnBarClose, so OnBarUpdate sees only closed bars. Levels are
//  published on the first bar AFTER their session ends. Nothing reads a bar that
//  has not printed.
// =============================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.VPS;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class MnqVpLiquiditySweep : Strategy
	{
		// ── Modules ───────────────────────────────────────────────────────────────
		private VolumeProfileEngine _profile;
		private VpsSweepTracker     _sweeps;
		private VpsSession          _profileWindow;
		private VpsSession          _rangeWindow;
		/// <summary>Window running from midnight to the "no entries after" clock time.</summary>
		private VpsSession          _entryCutoff;

		/// <summary>The levels currently in force — the last COMPLETED sessions.</summary>
		private readonly VpsLevelSet _levels = new VpsLevelSet();

		// ── Resolved configuration ────────────────────────────────────────────────
		private TimeZoneInfo _barTz;
		private TimeSpan     _barLength;
		private double       _tickSize   = 0.25;
		private double       _pointValue = 2.0;
		private string       _tickFormat = "0.##";

		private bool   _configError;
		private string _configErrorText = string.Empty;

		// ── Session tracking ──────────────────────────────────────────────────────
		private DateTime _profileOccurrence = DateTime.MinValue;
		private DateTime _rangeOccurrence   = DateTime.MinValue;
		private bool     _inProfile;
		private bool     _inRange;
		private double   _rangeHigh, _rangeLow;

		// ── Running state ─────────────────────────────────────────────────────────
		private DateTime _tradingDay = DateTime.MinValue;
		private int      _tradesDay;
		private int      _tradeSeq;

		// Diagnostics.
		// A strategy with several stacked preconditions can produce zero trades for a
		// dozen different reasons, and "nothing happened" is the least useful possible
		// output. These count every stage of the funnel so a barren run reports WHERE
		// it stopped instead of leaving it to be guessed at.
		private int _diagBars;
		private int _diagBarsInProfile, _diagBarsInRange;
		private int _diagProfilePublished, _diagProfileFailed;
		private int _diagRangePublished;
		private int _diagBarsWithLevels, _diagBarsEntryAllowed;
		private int _diagSweeps, _diagConfirmations, _diagEntries;
		private int _diagSkipWindow, _diagSkipDayCap, _diagSkipInTrade;
		private int _diagSkipNoTarget, _diagSkipRisk, _diagSkipSize;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Liquidity sweeps of the session volume profile's VAH/VAL and the time range's "
				            + "RH/RL, confirmed by rejection or an engulfing candle, targeting the farthest "
				            + "opposite-side level, with break-even on any intermediate line.";
				Name        = "MnqVpLiquiditySweep";

				Calculate                   = Calculate.OnBarClose;
				EntriesPerDirection         = 1;
				EntryHandling               = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy= false;
				ExitOnSessionCloseSeconds   = 30;
				IsFillLimitOnTouch          = false;
				MaximumBarsLookBack         = MaximumBarsLookBack.TwoHundredFiftySix;
				OrderFillResolution         = OrderFillResolution.Standard;
				Slippage                    = 0;
				StartBehavior               = StartBehavior.WaitUntilFlat;
				TimeInForce                 = TimeInForce.Gtc;
				TraceOrders                 = false;
				RealtimeErrorHandling       = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling          = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade         = 2;
				IsInstantiatedOnEachOptimizationIteration = true;

				ApplyDefaults();
			}
			else if (State == State.Configure)
			{
				Slippage = SlippageTicks;

				// Section 13 asks for the real chronological order of a same-candle
				// stop/target whenever intrabar data is available. 1-tick fill
				// resolution is NinjaTrader's way of providing exactly that.
				if (UseTickPrecision && BarsPeriod.BarsPeriodType != BarsPeriodType.Tick)
				{
					OrderFillResolution      = OrderFillResolution.High;
					OrderFillResolutionType  = BarsPeriodType.Tick;
					OrderFillResolutionValue = 1;
				}
			}
			else if (State == State.DataLoaded)
			{
				BuildModules();
			}
			else if (State == State.Terminated)
			{
				PrintRunSummary();
			}
		}

		// ── Per bar ───────────────────────────────────────────────────────────────
		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0 || _configError)
				return;

			if (CurrentBar < Math.Max(2, BarsRequiredToTrade))
				return;

			_diagBars++;

			DateTime barOpen = Time[0] - _barLength;

			// 1. Build the two sessions. Levels publish on the first bar AFTER a
			//    session closes, so nothing here can see an unfinished profile.
			AdvanceProfileSession(barOpen);
			AdvanceRangeSession(barOpen);

			// 2. Daily counters.
			DateTime day = TradingDayOf(barOpen);
			if (day != _tradingDay)
			{
				_tradingDay = day;
				_tradesDay  = 0;
			}

			// 3. Manage the open trade before looking for a new one — break-even must
			//    be evaluated on this bar even when no new signal exists.
			ManageOpenTrade();

			// 4. Look for sweeps and confirmations.
			_sweeps.Expire(CurrentBar);
			EvaluateSetups(barOpen);

			if (ShowVisuals)
				DrawLevels();
		}

		// ── Profile session ───────────────────────────────────────────────────────
		private void AdvanceProfileSession(DateTime barOpenInBarZone)
		{
			DateTime tz = VpsTimeZone.Convert(barOpenInBarZone, _barTz, _profileWindow.Zone);

			bool inside = _profileWindow.Contains(tz);
			DateTime occurrence = inside ? _profileWindow.OccurrenceOf(tz) : DateTime.MinValue;

			// A new occurrence starts a fresh accumulation. Comparing the occurrence
			// rather than a simple in/out edge keeps a gap in the data, a holiday or a
			// session that wraps midnight from splitting one profile into two.
			if (inside && occurrence != _profileOccurrence)
			{
				_profile.Reset();
				_profileOccurrence = occurrence;
			}

			if (inside)
			{
				// The Pine script collects high/low/volume per bar and spreads the
				// volume across the bins the bar's range overlaps.
				_profile.Add(High[0], Low[0], Volume[0]);
				_diagBarsInProfile++;
			}
			else if (_inProfile)
			{
				// The session just ended: this is the Pine vpEnd branch.
				PublishProfile();
			}

			_inProfile = inside;
		}

		private void PublishProfile()
		{
			if (_profile.SampleCount == 0)
				return;

			VpProfileResult r = _profile.Compute();
			if (!r.Valid)
			{
				_diagProfileFailed++;

				if (VerboseLogging)
					Print(string.Format(CultureInfo.InvariantCulture,
						"{0}  PROFILE {1:yyyy-MM-dd HH:mm} produced no value area ({2} bars, range {3}).",
						Time[0], _profileOccurrence, r.Samples, r.Top - r.Bottom));
				return;
			}

			_levels.Vah            = r.Vah;
			_levels.Poc            = r.Poc;
			_levels.Val            = r.Val;
			_levels.HasProfile     = true;
			_levels.ProfileSession = _profileOccurrence;
			_diagProfilePublished++;

			// The first publish is always announced, so a run that produced no trades
			// can be told apart from a run that never produced levels at all.
			if (_diagProfilePublished == 1)
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  FIRST PROFILE published from {1} bars: VAH {2} POC {3} VAL {4}",
					Time[0], r.Samples,
					r.Vah.ToString(_tickFormat, CultureInfo.InvariantCulture),
					r.Poc.ToString(_tickFormat, CultureInfo.InvariantCulture),
					r.Val.ToString(_tickFormat, CultureInfo.InvariantCulture)));

			// A new profile invalidates any sweep armed against the old levels.
			_sweeps.Clear(VpsLevel.Vah);
			_sweeps.Clear(VpsLevel.Val);

			if (VerboseLogging)
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  PROFILE {1:yyyy-MM-dd} closed | {2} bars | VAH {3} POC {4} VAL {5}",
					Time[0], _profileOccurrence, r.Samples,
					r.Vah.ToString(_tickFormat, CultureInfo.InvariantCulture),
					r.Poc.ToString(_tickFormat, CultureInfo.InvariantCulture),
					r.Val.ToString(_tickFormat, CultureInfo.InvariantCulture)));
		}

		// ── Range session ─────────────────────────────────────────────────────────
		private void AdvanceRangeSession(DateTime barOpenInBarZone)
		{
			DateTime tz = VpsTimeZone.Convert(barOpenInBarZone, _barTz, _rangeWindow.Zone);

			bool inside = _rangeWindow.Contains(tz);
			DateTime occurrence = inside ? _rangeWindow.OccurrenceOf(tz) : DateTime.MinValue;

			if (inside)
				_diagBarsInRange++;

			if (inside && occurrence != _rangeOccurrence)
			{
				_rangeHigh       = High[0];
				_rangeLow        = Low[0];
				_rangeOccurrence = occurrence;
			}
			else if (inside)
			{
				_rangeHigh = Math.Max(_rangeHigh, High[0]);
				_rangeLow  = Math.Min(_rangeLow,  Low[0]);
			}
			else if (_inRange)
			{
				PublishRange();
			}

			_inRange = inside;
		}

		private void PublishRange()
		{
			if (_rangeHigh <= _rangeLow)
				return;

			_levels.Rh           = _rangeHigh;
			_levels.Rl           = _rangeLow;
			_levels.HasRange     = true;
			_levels.RangeSession = _rangeOccurrence;
			_diagRangePublished++;

			if (_diagRangePublished == 1)
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  FIRST RANGE published: RH {1} RL {2}",
					Time[0],
					_rangeHigh.ToString(_tickFormat, CultureInfo.InvariantCulture),
					_rangeLow.ToString(_tickFormat, CultureInfo.InvariantCulture)));

			_sweeps.Clear(VpsLevel.Rh);
			_sweeps.Clear(VpsLevel.Rl);

			if (VerboseLogging)
				Print(string.Format(CultureInfo.InvariantCulture,
					"{0}  RANGE {1:yyyy-MM-dd HH:mm} closed | RH {2} RL {3}",
					Time[0], _rangeOccurrence,
					_rangeHigh.ToString(_tickFormat, CultureInfo.InvariantCulture),
					_rangeLow.ToString(_tickFormat, CultureInfo.InvariantCulture)));
		}

		/// <summary>
		/// The day a trade counts against. Uses the profile session's zone so the
		/// counter rolls with the market the profile is built on, not the PC clock.
		/// </summary>
		private DateTime TradingDayOf(DateTime barOpenInBarZone)
		{
			return VpsTimeZone.Convert(barOpenInBarZone, _barTz, _profileWindow.Zone).Date;
		}

		private void PrintRunSummary()
		{
			if (_configError)
			{
				Print("MnqVpLiquiditySweep DID NOT TRADE: configuration error - " + _configErrorText);
				return;
			}

			Print(string.Format(CultureInfo.InvariantCulture,
				"\n===== MnqVpLiquiditySweep funnel =====\n"
			  + "  bars processed               {0}\n"
			  + "  bars inside profile session  {1}  -> profiles published {2} (failed {3})\n"
			  + "  bars inside range session    {4}  -> ranges published   {5}\n"
			  + "  bars with a usable level set {6}\n"
			  + "  bars where entry was allowed {7}\n"
			  + "  sweeps detected              {8}\n"
			  + "  confirmations                {9}\n"
			  + "  ENTRIES                      {10}\n"
			  + "  confirmations rejected by:\n"
			  + "    outside entry window       {11}\n"
			  + "    daily trade cap            {12}\n"
			  + "    a trade already open       {13}\n"
			  + "    no valid opposite target   {14}\n"
			  + "    stop under one tick        {15}\n"
			  + "    sizing                     {16}\n"
			  + "  fill resolution {17}\n"
			  + "{18}"
			  + "=====================================",
				_diagBars,
				_diagBarsInProfile, _diagProfilePublished, _diagProfileFailed,
				_diagBarsInRange, _diagRangePublished,
				_diagBarsWithLevels, _diagBarsEntryAllowed,
				_diagSweeps, _diagConfirmations, _diagEntries,
				_diagSkipWindow, _diagSkipDayCap, _diagSkipInTrade,
				_diagSkipNoTarget, _diagSkipRisk, _diagSkipSize,
				UseTickPrecision ? "High (1 tick)" : "Standard",
				FirstBlockedStage()));
		}

		/// <summary>Names the first stage of the funnel that produced nothing.</summary>
		private string FirstBlockedStage()
		{
			if (_diagBars == 0)
				return "  DIAGNOSIS: no bars were processed at all. Check the backtest date range, and that\n"
				     + "             the series is 1 Minute.\n";

			if (_diagBarsInProfile == 0)
				return "  DIAGNOSIS: no bar ever fell inside the PROFILE session, so VAH/POC/VAL were never\n"
				     + "             built. Compare the resolved window logged at startup against your data,\n"
				     + "             and check Tools > Options > General > Time zone.\n";

			if (_diagProfilePublished == 0)
				return "  DIAGNOSIS: bars fell inside the profile session but it never CLOSED inside the\n"
				     + "             backtest, so no levels were ever published. Extend the date range.\n";

			if (_diagBarsInRange == 0)
				return "  DIAGNOSIS: no bar ever fell inside the RANGE session, so RH/RL were never built.\n"
				     + "             Entries can never open without them. Check the range window.\n";

			if (_diagRangePublished == 0)
				return "  DIAGNOSIS: bars fell inside the range session but it never CLOSED inside the\n"
				     + "             backtest, so RH/RL were never published.\n";

			if (_diagBarsEntryAllowed == 0)
				return "  DIAGNOSIS: the entry window never opened. Entries run from the range close to the\n"
				     + "             'No entries after' cutoff - widen the cutoff.\n";

			if (_diagSweeps == 0)
				return "  DIAGNOSIS: price never traded through any level, so nothing was ever swept.\n"
				     + "             Compare the published levels against the chart.\n";

			if (_diagConfirmations == 0)
				return "  DIAGNOSIS: sweeps happened but none was confirmed. Widen 'Engulfing window' or\n"
				     + "             'Level proximity (ticks)'.\n";

			if (_diagEntries == 0)
				return "  DIAGNOSIS: setups were confirmed but every one was rejected - see the counts above.\n";

			return string.Empty;
		}

	}
}
