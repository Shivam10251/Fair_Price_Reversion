#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

// =====================================================================================================
//  Multi-Session First Candle Strategy  (NinjaTrader 8 / NinjaScript)
// -----------------------------------------------------------------------------------------------------
//  CONCEPT
//      For each of up to three independently configurable trading sessions, the strategy isolates the
//      FIRST 1-minute candle of that session, waits for it to close, and trades in the direction of
//      that candle (green = long, red = short, doji = user-selectable).
//
//  DESIGN NOTES THAT MATTER FOR CORRECTNESS
//      1. Calculate = Calculate.OnBarClose.  OnBarUpdate therefore fires exactly once per completed
//         1-minute bar, using only closed-bar information => no look-ahead bias is structurally
//         possible for the entry decision.
//      2. NinjaTrader time-stamps a bar at its CLOSE.  The 09:30:00-09:30:59 candle carries
//         Time[0] == 09:31:00.  All session comparisons are therefore done against the bar's OPEN
//         time, computed as Time[0] - 1 minute in the data's own time zone (subtracting before the
//         time-zone conversion keeps the arithmetic monotonic across DST boundaries).
//      3. Time zone handling uses TimeZoneInfo with the source zone taken from
//         Bars.TradingHours.TimeZoneInfo (the zone NinjaTrader time-stamps the series in) and the
//         destination zone resolved as US Eastern.  DST is handled by the OS tz database, not by a
//         hand-rolled second-Sunday-in-March calculation.
//      4. Entries are market orders submitted at the close of the first candle.  In NinjaTrader's
//         backtest engine that order is filled at the OPEN of the following bar.  Stop loss and
//         take profit are therefore expressed in TICKS via CalculationMode.Ticks so that NinjaTrader
//         anchors them to the ACTUAL average fill price rather than to an assumed close price.
//      5. Each session tracks its own "occurrence" (a concrete DateTime for the current instance of
//         that session window).  First-candle state is keyed off the occurrence, not off the trading
//         day, so overnight sessions, holidays and missing data cannot desynchronise the reset.
// =====================================================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
	#region Public enums (prefixed to avoid collisions with other NinjaScript files)

	public enum MsfcDistanceUnit
	{
		Points,
		Ticks
	}

	public enum MsfcDojiAction
	{
		NoTrade,
		Long,
		Short
	}

	/// <summary>Where the protective stop is placed.</summary>
	public enum MsfcStopMode
	{
		/// <summary>A fixed distance from the entry, in the configured Distance Unit.</summary>
		FixedDistance,
		/// <summary>At the first candle's own extreme: its LOW for a long, its HIGH for a short.</summary>
		FirstCandleExtreme
	}

	/// <summary>How the profit target is derived.</summary>
	public enum MsfcTargetMode
	{
		/// <summary>A fixed distance from the entry, in the configured Distance Unit.</summary>
		FixedDistance,
		/// <summary>A multiple of the actual risk taken on this trade (R:R).</summary>
		RewardMultiple
	}

	public enum MsfcEmaFilterMode
	{
		PriceVsEma1,
		PriceVsEma2,
		Ema1VsEma2,
		PriceVsBothEmas
	}

	public enum MsfcOverlapMode
	{
		FirstMatchingSessionOnly,
		AllMatchingSessions
	}

	public enum MsfcDayResetMode
	{
		NinjaTraderTradingDay,
		EasternCalendarDate
	}

	public enum MsfcTimeZoneMode
	{
		ConvertToEasternTime,
		UseChartTimeZone
	}

	#endregion

	public class MultiSessionFirstCandleStrategy : Strategy
	{
		#region Nested types

		/// <summary>
		/// Runtime state for one configured session window.
		/// </summary>
		private class SessionRuntime
		{
			public int      Index;                 // 1, 2 or 3
			public bool     Enabled;
			public TimeSpan Start;
			public TimeSpan End;
			public int      MaxTrades;
			public bool     CrossesMidnight;       // true when Start > End (e.g. 22:00 -> 02:00)
			public bool     IsValid;

			// Occurrence = the concrete date+time at which the CURRENT instance of this window began.
			public DateTime CurrentOccurrence    = DateTime.MinValue;
			public DateTime CurrentOccurrenceEnd = DateTime.MinValue;
			public bool     FirstCandleProcessed;  // guards against re-processing / duplicate entries
			public int      TradesTaken;           // reset per occurrence and per trading day
			public int      TotalTrades;           // lifetime counter, for the summary report

			public string Label { get { return "S" + Index.ToString(CultureInfo.InvariantCulture); } }
		}

		#endregion

		#region Private fields

		// --- indicator instances / computed series -------------------------------------------------
		private EMA    ema1;
		private EMA    ema2;
		private double vwapCumulativePv;
		private double vwapCumulativeVolume;
		private double vwapValue;

		private const int PlotEma1 = 0;
		private const int PlotEma2 = 1;
		private const int PlotVwap = 2;

		// --- time zone -----------------------------------------------------------------------------
		private TimeZoneInfo sourceTimeZone;
		private TimeZoneInfo easternTimeZone;
		private bool         timeZoneConversionRequired;

		// --- configuration / validation ------------------------------------------------------------
		private readonly List<SessionRuntime> sessions = new List<SessionRuntime>();
		private bool   configurationValid = true;
		private int    stopLossTicks;
		private int    takeProfitTicks;

		/// <summary>
		/// The bracket decided for the trade currently being submitted / held. With a
		/// first-candle stop the distance changes on every signal, so it can no longer
		/// live in the two precomputed tick counts above.
		/// </summary>
		private MsfcTradePlan activePlan;
		private string        activeSignalName = string.Empty;
		private int    minimumBarsForIndicators;

		// --- daily state ---------------------------------------------------------------------------
		private DateTime currentTradingDay = DateTime.MinValue;
		private SessionIterator sessionIterator;
		private int      dailyTradeCount;
		private bool     aTradeClosedToday;

		// --- open trade state ----------------------------------------------------------------------
		private bool           inTrade;
		private int            activeTradeId;
		private int            activeEntryBar;
		private double         activeEntryPrice;
		private double         activeStopPrice;
		private double         activeTargetPrice;
		private MarketPosition activeDirection = MarketPosition.Flat;
		private int            activeSessionIndex;
		private DateTime       activeSessionEnd = DateTime.MinValue;

		private string pendingEntrySignal;
		private int    pendingEntrySessionIndex;

		private int tradeIdSeed;

		// --- drawing bookkeeping -------------------------------------------------------------------
		private readonly List<int> drawnTradeIds = new List<int>();

		// --- custom statistics ---------------------------------------------------------------------
		private int statLongTrades;
		private int statShortTrades;
		private int statTpHits;
		private int statSlHits;
		private int statOtherExits;
		private int statWins;
		private int statLosses;
		private int consecutiveWins;
		private int consecutiveLosses;
		private int maxConsecutiveWins;
		private int maxConsecutiveLosses;

		#endregion

		#region OnStateChange

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Trades the direction of the first 1-minute candle of up to three independently "
							+ "configurable sessions, with optional EMA / VWAP filters, fixed SL/TP and on-chart "
							+ "risk / reward zones.";
				Name        = "MultiSessionFirstCandleStrategy";

				// --- Core NinjaTrader strategy plumbing -------------------------------------------
				// OnBarClose is mandatory here: the entry decision must only ever see completed bars.
				Calculate                                   = Calculate.OnBarClose;
				EntriesPerDirection                         = 1;
				EntryHandling                               = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy                = true;
				ExitOnSessionCloseSeconds                   = 30;
				IsFillLimitOnTouch                          = false;
				// Infinite look-back is required so that drawing objects anchored to the entry bar can
				// still resolve their bar index on long-running trades.
				MaximumBarsLookBack                         = MaximumBarsLookBack.Infinite;
				// High fill resolution with a 1-tick series resolves the "stop and target inside the
				// same 1-minute bar" ambiguity accurately instead of assuming the worst case.
				OrderFillResolution                         = OrderFillResolution.High;
				OrderFillResolutionType                     = BarsPeriodType.Tick;
				OrderFillResolutionValue                    = 1;
				Slippage                                    = 0;
				StartBehavior                               = StartBehavior.WaitUntilFlat;
				TimeInForce                                 = TimeInForce.Gtc;
				TraceOrders                                 = false;
				RealtimeErrorHandling                       = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling                          = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade                         = 20;
				IsInstantiatedOnEachOptimizationIteration   = true;
				IsUnmanaged                                 = false;

				// --- General ----------------------------------------------------------------------
				EnableStrategy           = true;
				MaxTradesPerDay          = 3;
				AllowNewTradeAfterClose  = true;
				DayResetMode             = MsfcDayResetMode.NinjaTraderTradingDay;
				TimeZoneMode             = MsfcTimeZoneMode.ConvertToEasternTime;
				SessionTimeZoneId        = "Asia/Kolkata";
				AutoAdjustForUsDst       = true;
				StrictFirstCandle        = true;
				OverlapMode              = MsfcOverlapMode.FirstMatchingSessionOnly;
				ExitAtSessionEnd         = false;
				PrintSummaryToOutput     = true;

				// --- Sessions (examples only - fully user configurable) ---------------------------
				// Typed in IST as SUMMER (US-DST) values; winter is derived, never typed.
				//   1900-0130 IST summer = 0930-1600 New York = 2000-0230 IST winter
				//   0330-0830 IST summer = 1800-2300 New York = 0430-0930 IST winter
				//   0930-1730 IST summer = 0000-0800 New York = 1030-1830 IST winter
				EnableSession1  = true;  Session1Start = 1900; Session1End = 130;  Session1MaxTrades = 1;
				EnableSession2  = true;  Session2Start = 330;  Session2End = 830;  Session2MaxTrades = 1;
				EnableSession3  = true;  Session3Start = 930;  Session3End = 1730; Session3MaxTrades = 1;

				// --- Risk management --------------------------------------------------------------
				StopMode            = MsfcStopMode.FixedDistance;
				StopBufferTicks     = 0;
				TargetMode          = MsfcTargetMode.FixedDistance;
				RewardRatio         = 2.0;
				DistanceUnit        = MsfcDistanceUnit.Points;
				StopLossValue       = 20;
				TakeProfitValue     = 40;
				DojiAction          = MsfcDojiAction.NoTrade;
				RiskPerTradeDollars = 100;
				RiskBufferDollars   = 30;
				MaxContracts        = 10;

				// --- EMA --------------------------------------------------------------------------
				EmaPeriod1    = 9;
				EmaPeriod2    = 21;
				ShowEma1      = true;
				ShowEma2      = true;
				UseEmaFilter  = false;
				EmaFilterMode = MsfcEmaFilterMode.PriceVsEma1;

				// --- VWAP -------------------------------------------------------------------------
				ShowVwap      = true;
				UseVwapFilter = false;

				// --- Visualization ----------------------------------------------------------------
				ShowTradeZones      = true;
				ShowEntryLine       = true;
				ShowStopLossLine    = true;
				ShowTakeProfitLine  = true;
				ShowRiskZone        = true;
				ShowRewardZone      = true;
				ZoneOpacity         = 15;
				MaxDrawnTrades      = 50;
				DrawOnHistoricalBars = true;
				EntryLineBrush      = Brushes.DodgerBlue;
				StopLossBrush       = Brushes.Firebrick;
				TakeProfitBrush     = Brushes.SeaGreen;

				// --- Plots ------------------------------------------------------------------------
				AddPlot(new Stroke(Brushes.Goldenrod,  2), PlotStyle.Line, "EMA1");
				AddPlot(new Stroke(Brushes.MediumOrchid, 2), PlotStyle.Line, "EMA2");
				AddPlot(new Stroke(Brushes.DeepSkyBlue, 2), PlotStyle.Line, "VWAP");
			}
			else if (State == State.Configure)
			{
				ValidateParameters();
				FreezeBrushes();
				ApplyPlotVisibility();
			}
			else if (State == State.DataLoaded)
			{
				sessionIterator = new SessionIterator(Bars);

				ResolveTimeZones();
				ValidateBarsPeriod();
				BuildSessions();

				ema1 = EMA(EmaPeriod1);
				ema2 = EMA(EmaPeriod2);
				minimumBarsForIndicators = Math.Max(EmaPeriod1, EmaPeriod2) + 1;

				// Convert the user's SL/TP into ticks once, using the instrument's real TickSize.
				stopLossTicks   = ToTicks(StopLossValue);
				takeProfitTicks = ToTicks(TakeProfitValue);

				if ((StopMode   == MsfcStopMode.FixedDistance   && stopLossTicks   <= 0)
				 || (TargetMode == MsfcTargetMode.FixedDistance && takeProfitTicks <= 0))
				{
					LogConfigError(string.Format(CultureInfo.InvariantCulture,
						"Stop loss / take profit resolve to {0} / {1} ticks. Both must be greater than zero "
						+ "(TickSize = {2}).", stopLossTicks, takeProfitTicks, TickSize));
				}

				PrintSizingPlan();

				ResetTradeState();
				ResetDailyCounters(DateTime.MinValue);
			}
			else if (State == State.Realtime)
			{
				// Historical drawing objects are kept; runtime state is already consistent because the
				// session occurrence keys are recomputed from bar time, not from a bar counter.
				if (inTrade && Position.MarketPosition == MarketPosition.Flat)
					ResetTradeState();
			}
			else if (State == State.Terminated)
			{
				if (PrintSummaryToOutput && SystemPerformance != null && sessions.Count == 3)
					PrintCustomSummary();
			}
		}

		#endregion

		#region Validation helpers

		private void LogConfigError(string message)
		{
			configurationValid = false;
			Log("MultiSessionFirstCandleStrategy: " + message, LogLevel.Error);
			Print("[CONFIG ERROR] " + message);
		}

		/// <summary>
		/// Validates everything that can be checked without instrument data.
		/// </summary>
		private void ValidateParameters()
		{
			configurationValid = true;

			// The fixed distances only matter in the fixed modes; a first-candle stop
			// with an R target never reads them, so they must not fail the run there.
			if (StopMode == MsfcStopMode.FixedDistance && StopLossValue <= 0)
				LogConfigError("Stop Loss Value must be greater than zero when Stop Mode = FixedDistance.");

			if (TargetMode == MsfcTargetMode.FixedDistance && TakeProfitValue <= 0)
				LogConfigError("Take Profit Value must be greater than zero when Target Mode = FixedDistance.");

			if (TargetMode == MsfcTargetMode.RewardMultiple && RewardRatio <= 0)
				LogConfigError("Reward Ratio (R) must be greater than zero when Target Mode = RewardMultiple.");

			if (StopBufferTicks < 0)
				LogConfigError("Stop Buffer (ticks) cannot be negative.");

			if (EmaPeriod1 < 1 || EmaPeriod2 < 1)
				LogConfigError("EMA periods must be greater than or equal to 1.");

			{
				if (RiskPerTradeDollars <= 0)
					LogConfigError("Risk Per Trade ($) must be greater than zero.");

				if (RiskBufferDollars < 0)
					LogConfigError("Risk Buffer ($) cannot be negative.");

				if (RiskBufferDollars >= RiskPerTradeDollars)
					LogConfigError("Risk Buffer ($) must be smaller than Risk Per Trade ($); otherwise the "
								 + "lower edge of the accepted band is zero or negative and any size passes.");

				if (MaxContracts < 1)
					LogConfigError("Max Contracts must be at least 1.");
			}

			if (MaxTradesPerDay < 0)
				LogConfigError("Maximum Trades Per Day cannot be negative.");

			ValidateSessionTimes(1, EnableSession1, Session1Start, Session1End, Session1MaxTrades);
			ValidateSessionTimes(2, EnableSession2, Session2Start, Session2End, Session2MaxTrades);
			ValidateSessionTimes(3, EnableSession3, Session3Start, Session3End, Session3MaxTrades);

			if (!EnableSession1 && !EnableSession2 && !EnableSession3)
				Print("[WARNING] All three sessions are disabled - the strategy will never trade.");
		}

		private void ValidateSessionTimes(int index, bool enabled, int start, int end, int maxTrades)
		{
			if (!enabled)
				return;

			if (!IsValidHhmm(start))
				LogConfigError(string.Format(CultureInfo.InvariantCulture,
					"Session {0} start time '{1}' is not a valid HHmm value (0000-2359).", index, start));

			if (!IsValidHhmm(end))
				LogConfigError(string.Format(CultureInfo.InvariantCulture,
					"Session {0} end time '{1}' is not a valid HHmm value (0000-2359).", index, end));

			if (IsValidHhmm(start) && IsValidHhmm(end) && start == end)
				LogConfigError(string.Format(CultureInfo.InvariantCulture,
					"Session {0} start equals end ({1}). A session must have a non-zero duration.", index, start));

			if (maxTrades < 0)
				LogConfigError(string.Format(CultureInfo.InvariantCulture,
					"Session {0} max trades cannot be negative.", index));
		}

		private static bool IsValidHhmm(int hhmm)
		{
			if (hhmm < 0 || hhmm > 2359)
				return false;
			return (hhmm % 100) < 60;
		}

		private static TimeSpan HhmmToTimeSpan(int hhmm)
		{
			return new TimeSpan(hhmm / 100, hhmm % 100, 0);
		}

		/// <summary>
		/// The strategy is explicitly specified against 1-minute candles. Running it on any other
		/// period would silently redefine "first candle", so it refuses to run instead.
		/// </summary>
		private void ValidateBarsPeriod()
		{
			if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
			{
				LogConfigError(string.Format(CultureInfo.InvariantCulture,
					"This strategy requires a 1 Minute primary data series. Current series is {0} {1}.",
					BarsPeriod.Value, BarsPeriod.BarsPeriodType));
			}
		}

		private int ToTicks(double value)
		{
			if (DistanceUnit == MsfcDistanceUnit.Ticks)
				return (int)Math.Round(value, MidpointRounding.AwayFromZero);

			if (TickSize <= 0)
				return 0;

			return (int)Math.Round(value / TickSize, MidpointRounding.AwayFromZero);
		}

		#endregion

		#region Time zone handling

		/// <summary>
		/// Resolves the source zone (the zone NinjaTrader time-stamps this series in, i.e. the zone of
		/// the applied Trading Hours template) and the destination zone (US Eastern). DST is handled by
		/// the operating system's time zone database.
		/// </summary>
		private void ResolveTimeZones()
		{
			// NinjaTrader stamps EVERY bar in the global display time zone
			// (Tools > Options > General > Time zone), falling back to the PC's zone
			// when unset. Time[0] is already expressed there.
			//
			// This deliberately does NOT read Bars.TradingHours.TimeZoneInfo. That is
			// the zone the instrument's SESSION TEMPLATE is authored in — Central for
			// CME index futures — and says nothing about how bar timestamps are
			// expressed. Using it relabels an already-local timestamp and converts it a
			// second time, shifting every session by the difference between the two
			// zones. That is a real bug this strategy previously carried.
			try
			{
				sourceTimeZone = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo ?? TimeZoneInfo.Local;
			}
			catch (Exception)
			{
				sourceTimeZone = TimeZoneInfo.Local;
			}

			easternTimeZone = null;

			try
			{
				easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); // Windows
			}
			catch (Exception)
			{
				try
				{
					easternTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); // IANA
				}
				catch (Exception)
				{
					easternTimeZone = null;
				}
			}

			if (TimeZoneMode == MsfcTimeZoneMode.UseChartTimeZone)
			{
				timeZoneConversionRequired = false;
			}
			else if (easternTimeZone == null || sourceTimeZone == null)
			{
				timeZoneConversionRequired = false;
				Print("[WARNING] US Eastern time zone could not be resolved on this machine. Session times "
					+ "will be interpreted in the data series' own time zone.");
			}
			else
			{
				timeZoneConversionRequired = !string.Equals(sourceTimeZone.Id, easternTimeZone.Id,
					StringComparison.OrdinalIgnoreCase);
			}

			Print(string.Format(CultureInfo.InvariantCulture,
				"[INFO] Data series time zone: {0} | Session times interpreted as: {1}",
				sourceTimeZone == null ? "unknown" : sourceTimeZone.Id,
				timeZoneConversionRequired ? (easternTimeZone == null ? "unknown" : easternTimeZone.Id)
										   : (sourceTimeZone == null ? "unknown" : sourceTimeZone.Id)));
		}

		/// <summary>Converts a bar time from the series' zone into the session (Eastern) zone.</summary>
		private DateTime ToSessionTime(DateTime barTime)
		{
			if (!timeZoneConversionRequired)
				return barTime;

			try
			{
				return TimeZoneInfo.ConvertTime(barTime, sourceTimeZone, easternTimeZone);
			}
			catch (Exception)
			{
				return barTime;
			}
		}

		/// <summary>
		/// Returns the OPEN time of the current bar, expressed in session (Eastern) time.
		/// NinjaTrader stamps bars at their close, so the open time is close - bar period.
		/// The subtraction is performed in the source zone first, which keeps the arithmetic monotonic
		/// through DST transitions.
		/// </summary>
		private DateTime GetBarOpenSessionTime()
		{
			return ToSessionTime(Time[0].AddMinutes(-BarsPeriod.Value));
		}

		#endregion

		#region Session construction and state

		/// <summary>The DST calendar the session times are pinned to. MNQ is a CME product.</summary>
		private const string DstAnchorTimeZoneId = "America/New_York";

		/// <summary>
		/// How far the zone the user TYPES in runs ahead of the anchor zone during the
		/// anchor's summer time. Asia/Kolkata against US Eastern gives +9h30. Read from
		/// the tz database at a reference instant inside US DST rather than assumed.
		/// </summary>
		private TimeSpan SummerShift()
		{
			TimeZoneInfo typed = ResolveZone(SessionTimeZoneId);

			if (typed == null || easternTimeZone == null)
				return TimeSpan.Zero;

			DateTimeOffset summerReference =
				new DateTimeOffset(new DateTime(2025, 7, 15, 12, 0, 0, DateTimeKind.Utc));

			return typed.GetUtcOffset(summerReference) - easternTimeZone.GetUtcOffset(summerReference);
		}

		private static TimeZoneInfo ResolveZone(string id)
		{
			if (string.IsNullOrWhiteSpace(id))
				return null;

			string key = id.Trim();

			// .NET Framework knows Windows ids only, so map the ones actually used.
			if (key.Equals("Asia/Kolkata", StringComparison.OrdinalIgnoreCase)
			 || key.Equals("Asia/Calcutta", StringComparison.OrdinalIgnoreCase)
			 || key.Equals("IST", StringComparison.OrdinalIgnoreCase))
				key = "India Standard Time";
			else if (key.Equals("America/New_York", StringComparison.OrdinalIgnoreCase))
				key = "Eastern Standard Time";
			else if (key.Equals("Europe/London", StringComparison.OrdinalIgnoreCase))
				key = "GMT Standard Time";

			try { return TimeZoneInfo.FindSystemTimeZoneById(key); }
			catch (Exception) { return null; }
		}

		/// <summary>Shifts an HHMM clock value by whole minutes, wrapping across midnight.</summary>
		private static int ShiftHhmm(int hhmm, int deltaMinutes)
		{
			int minutes = (hhmm / 100) * 60 + (hhmm % 100) + deltaMinutes;

			minutes %= 1440;
			if (minutes < 0)
				minutes += 1440;

			return (minutes / 60) * 100 + (minutes % 60);
		}

		private void BuildSessions()
		{
			// The user types SUMMER times in SessionTimeZoneId. Rebasing them once onto
			// US Eastern — where sessions are already evaluated — gives a window that is
			// fixed there all year, so the winter shift happens by itself. Shifting the
			// typed window +1h in winter and evaluating a fixed Eastern window are the
			// same arithmetic, but this form asks the OS tz database for the transition
			// dates instead of hardcoding them.
			int delta = 0;

			if (AutoAdjustForUsDst && timeZoneConversionRequired)
			{
				TimeSpan shift = SummerShift();
				delta = -(int)shift.TotalMinutes;
			}

			sessions.Clear();
			sessions.Add(CreateSession(1, EnableSession1, ShiftHhmm(Session1Start, delta), ShiftHhmm(Session1End, delta), Session1MaxTrades));
			sessions.Add(CreateSession(2, EnableSession2, ShiftHhmm(Session2Start, delta), ShiftHhmm(Session2End, delta), Session2MaxTrades));
			sessions.Add(CreateSession(3, EnableSession3, ShiftHhmm(Session3Start, delta), ShiftHhmm(Session3End, delta), Session3MaxTrades));

			PrintSessionPlan(delta);
			WarnAboutOverlaps();
		}

		/// <summary>
		/// Prints what each window resolves to in summer AND winter, so the shift is
		/// visible before a bar is processed rather than inferred from fills.
		/// </summary>
		private void PrintSessionPlan(int delta)
		{
			if (delta == 0)
			{
				Print("[INFO] DST auto-adjust is OFF or not applicable — session times are taken literally.");
				return;
			}

			// Winter adds whatever the anchor's DST is worth, on the user's clock.
			TimeZoneInfo typed = ResolveZone(SessionTimeZoneId);
			DateTimeOffset winter = new DateTimeOffset(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc));
			int extra = typed == null || easternTimeZone == null
				? 60
				: (int)((typed.GetUtcOffset(winter) - easternTimeZone.GetUtcOffset(winter)).TotalMinutes
				        + delta);

			Print(string.Format(CultureInfo.InvariantCulture,
				"[INFO] Session times typed in {0} as SUMMER values, pinned to {1} so winter follows automatically.\n"
			  + "       Session 1 {2}: {3:0000}-{4:0000} summer / {5:0000}-{6:0000} winter  (= {7:0000}-{8:0000} ET)\n"
			  + "       Session 2 {9}: {10:0000}-{11:0000} summer / {12:0000}-{13:0000} winter  (= {14:0000}-{15:0000} ET)\n"
			  + "       Session 3 {16}: {17:0000}-{18:0000} summer / {19:0000}-{20:0000} winter  (= {21:0000}-{22:0000} ET)",
				SessionTimeZoneId, DstAnchorTimeZoneId,
				EnableSession1 ? "ON " : "off", Session1Start, Session1End,
				ShiftHhmm(Session1Start, extra), ShiftHhmm(Session1End, extra),
				ShiftHhmm(Session1Start, delta), ShiftHhmm(Session1End, delta),
				EnableSession2 ? "ON " : "off", Session2Start, Session2End,
				ShiftHhmm(Session2Start, extra), ShiftHhmm(Session2End, extra),
				ShiftHhmm(Session2Start, delta), ShiftHhmm(Session2End, delta),
				EnableSession3 ? "ON " : "off", Session3Start, Session3End,
				ShiftHhmm(Session3Start, extra), ShiftHhmm(Session3End, extra),
				ShiftHhmm(Session3Start, delta), ShiftHhmm(Session3End, delta)));
		}

		private SessionRuntime CreateSession(int index, bool enabled, int start, int end, int maxTrades)
		{
			SessionRuntime s = new SessionRuntime
			{
				Index     = index,
				Enabled   = enabled,
				MaxTrades = maxTrades,
				IsValid   = IsValidHhmm(start) && IsValidHhmm(end) && start != end
			};

			if (s.IsValid)
			{
				s.Start           = HhmmToTimeSpan(start);
				s.End             = HhmmToTimeSpan(end);
				s.CrossesMidnight = s.Start > s.End;
			}

			return s;
		}

		/// <summary>
		/// Overlap documentation:
		///   - Sessions are evaluated in ascending index order (1, 2, 3).
		///   - A single bar can qualify as the first candle of more than one session only when two
		///     sessions share the same start minute.
		///   - With OverlapMode = FirstMatchingSessionOnly (default) only the lowest-numbered session
		///     may generate the trade; the higher-numbered sessions still mark their first candle as
		///     consumed so they cannot fire later on a different bar of the same occurrence.
		///   - With OverlapMode = AllMatchingSessions each session is allowed to attempt an entry, but
		///     the flat-position guard means only the first one actually fills; the rest are skipped.
		///     This mode exists so the behaviour is explicit rather than accidental.
		/// </summary>
		private void WarnAboutOverlaps()
		{
			for (int i = 0; i < sessions.Count; i++)
			{
				for (int j = i + 1; j < sessions.Count; j++)
				{
					SessionRuntime a = sessions[i];
					SessionRuntime b = sessions[j];

					if (!a.Enabled || !b.Enabled || !a.IsValid || !b.IsValid)
						continue;

					if (a.Start == b.Start)
						Print(string.Format(CultureInfo.InvariantCulture,
							"[WARNING] Session {0} and Session {1} start at the same time. Overlap mode = {2}.",
							a.Index, b.Index, OverlapMode));
				}
			}
		}

		/// <summary>
		/// Determines whether the given bar open time falls inside the session window and, if so, what
		/// the concrete start/end DateTime of the current occurrence of that window is.
		/// Correctly handles windows that cross midnight (Start > End).
		/// </summary>
		private bool TryGetOccurrence(SessionRuntime s, DateTime barOpenSessionTime,
									  out DateTime occurrenceStart, out DateTime occurrenceEnd)
		{
			occurrenceStart = DateTime.MinValue;
			occurrenceEnd   = DateTime.MinValue;

			if (!s.Enabled || !s.IsValid)
				return false;

			TimeSpan tod  = barOpenSessionTime.TimeOfDay;
			DateTime date = barOpenSessionTime.Date;

			if (!s.CrossesMidnight)
			{
				if (tod < s.Start || tod >= s.End)
					return false;

				occurrenceStart = date + s.Start;
				occurrenceEnd   = date + s.End;
				return true;
			}

			// Crosses midnight: [Start .. 24:00) on day D, then [00:00 .. End) on day D+1.
			if (tod >= s.Start)
			{
				occurrenceStart = date + s.Start;
				occurrenceEnd   = date.AddDays(1) + s.End;
				return true;
			}

			if (tod < s.End)
			{
				occurrenceStart = date.AddDays(-1) + s.Start;
				occurrenceEnd   = date + s.End;
				return true;
			}

			return false;
		}

		/// <summary>
		/// Detects the start of a new occurrence of every session and resets that session's
		/// first-candle / trade-count state accordingly.
		/// </summary>
		private void UpdateSessionOccurrences(DateTime barOpenSessionTime)
		{
			for (int i = 0; i < sessions.Count; i++)
			{
				SessionRuntime s = sessions[i];
				DateTime occStart, occEnd;

				if (!TryGetOccurrence(s, barOpenSessionTime, out occStart, out occEnd))
					continue;

				if (occStart != s.CurrentOccurrence)
				{
					s.CurrentOccurrence     = occStart;
					s.CurrentOccurrenceEnd  = occEnd;
					s.FirstCandleProcessed  = false;
					s.TradesTaken           = 0;
				}
			}
		}

		/// <summary>
		/// True when the current bar is the first candle of the session's current occurrence.
		/// In strict mode the bar's open must match the session start minute exactly; otherwise any
		/// first in-window bar qualifies (useful for illiquid instruments with gaps in the data).
		/// </summary>
		private bool IsFirstCandleOfSession(SessionRuntime s, DateTime barOpenSessionTime)
		{
			if (s.FirstCandleProcessed || s.CurrentOccurrence == DateTime.MinValue)
				return false;

			if (barOpenSessionTime == s.CurrentOccurrence)
				return true;

			if (!StrictFirstCandle)
				return true;

			// Strict mode and the session's opening minute is missing from the data: consume the
			// session for this occurrence rather than trading a late, unrepresentative candle.
			s.FirstCandleProcessed = true;
			Print(string.Format(CultureInfo.InvariantCulture,
				"[SKIP] Session {0} occurrence {1:yyyy-MM-dd HH:mm}: no bar opening exactly at the session "
				+ "start (first available bar opens {2:HH:mm}). Session skipped (Strict First Candle = true).",
				s.Index, s.CurrentOccurrence, barOpenSessionTime));

			return false;
		}

		/// <summary>
		/// Returns the sessions whose first candle is the current bar, honouring the overlap mode.
		/// </summary>
		private List<SessionRuntime> GetSessionsForCurrentBar(DateTime barOpenSessionTime)
		{
			List<SessionRuntime> matches = new List<SessionRuntime>();

			for (int i = 0; i < sessions.Count; i++)
			{
				SessionRuntime s = sessions[i];

				if (!IsFirstCandleOfSession(s, barOpenSessionTime))
					continue;

				if (matches.Count > 0 && OverlapMode == MsfcOverlapMode.FirstMatchingSessionOnly)
				{
					// A lower-numbered session already owns this candle. Consume this session's first
					// candle so it cannot fire on a later bar of the same occurrence.
					s.FirstCandleProcessed = true;
					continue;
				}

				matches.Add(s);
			}

			return matches;
		}

		#endregion

		#region Trading day handling

		/// <summary>
		/// New trading day detection.
		///   NinjaTraderTradingDay (default): uses Bars.GetTradingDayFromLocal(Time[0]), i.e. the trading
		///     date defined by the applied Trading Hours template. For CME index futures ETH this groups
		///     the 18:00 ET Sunday open with Monday, which is what a futures trader expects, so an
		///     18:00-23:00 session and a 00:00-08:00 session belong to the SAME trading day.
		///   EasternCalendarDate: uses the plain Eastern calendar date of the bar. Choose this if you
		///     want the daily counter to roll over at Eastern midnight instead.
		/// </summary>
		private DateTime GetTradingDay(DateTime barOpenSessionTime)
		{
			if (DayResetMode == MsfcDayResetMode.EasternCalendarDate)
				return barOpenSessionTime.Date;

			try
			{
				return sessionIterator.GetTradingDay(Time[0]).Date;
			}
			catch (Exception)
			{
				return barOpenSessionTime.Date;
			}
		}

		private bool IsNewTradingDay(DateTime tradingDay)
		{
			return tradingDay != currentTradingDay;
		}

		/// <summary>
		/// Resets the DAILY counters only. Per-session first-candle state and per-session trade counts
		/// are deliberately NOT touched here: they are keyed off each session's own occurrence and are
		/// reset in UpdateSessionOccurrences(). Clearing them here would be a bug for a session that
		/// straddles the day boundary (e.g. 22:00-02:00 with Eastern-calendar reset), because the
		/// rollover at midnight would re-arm the first-candle detector inside a live session.
		/// </summary>
		private void ResetDailyCounters(DateTime tradingDay)
		{
			currentTradingDay = tradingDay;
			dailyTradeCount   = 0;
			aTradeClosedToday = false;
		}

		#endregion

		#region OnBarUpdate

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			if (CurrentBar < 1)
				return;

			// --- indicators are updated first so the plots are continuous even when trading is off ---
			UpdateVwap();
			UpdatePlots();

			if (!configurationValid || !EnableStrategy)
				return;

			if (CurrentBar < Math.Max(BarsRequiredToTrade, minimumBarsForIndicators))
				return;

			DateTime barOpenSessionTime = GetBarOpenSessionTime();

			// --- daily rollover ---------------------------------------------------------------------
			DateTime tradingDay = GetTradingDay(barOpenSessionTime);
			if (IsNewTradingDay(tradingDay))
				ResetDailyCounters(tradingDay);

			// --- session occurrence tracking --------------------------------------------------------
			UpdateSessionOccurrences(barOpenSessionTime);

			// --- keep the on-chart trade zones anchored to the live bar -----------------------------
			UpdateTradeVisuals();

			// --- optional flatten at the end of the originating session -----------------------------
			CheckSessionEndExit(barOpenSessionTime);

			// --- the actual signal logic ------------------------------------------------------------
			ProcessFirstCandles(barOpenSessionTime);
		}

		#endregion

		#region Indicators (EMA plots + session VWAP)

		/// <summary>
		/// Session-anchored VWAP computed from the primary 1-minute series:
		///     VWAP = sum(typicalPrice * volume) / sum(volume), reset at each new NinjaTrader session.
		/// This deliberately avoids a secondary tick series: a 1-minute approximation is accurate to a
		/// fraction of a tick for MNQ and keeps backtests fast. It also avoids depending on the Order
		/// Flow VWAP indicator, which is not available on every NinjaTrader licence tier.
		/// </summary>
		private void UpdateVwap()
		{
			if (Bars.IsFirstBarOfSession)
			{
				vwapCumulativePv     = 0;
				vwapCumulativeVolume = 0;
			}

			double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
			double volume       = Volume[0];

			vwapCumulativePv     += typicalPrice * volume;
			vwapCumulativeVolume += volume;

			vwapValue = vwapCumulativeVolume > 0 ? vwapCumulativePv / vwapCumulativeVolume : Close[0];
		}

		private void UpdatePlots()
		{
			// EMA seeds itself from the first bar, so the values are valid immediately and no
			// zero-line artefact is plotted during the warm-up period.
			if (ema1 != null)
				Values[PlotEma1][0] = ema1[0];

			if (ema2 != null)
				Values[PlotEma2][0] = ema2[0];

			Values[PlotVwap][0] = vwapValue;
		}

		private void ApplyPlotVisibility()
		{
			if (Plots == null || Plots.Length < 3)
				return;

			if (!ShowEma1) Plots[PlotEma1].Brush = Brushes.Transparent;
			if (!ShowEma2) Plots[PlotEma2].Brush = Brushes.Transparent;
			if (!ShowVwap) Plots[PlotVwap].Brush = Brushes.Transparent;
		}

		#endregion

		#region Entry filters

		/// <summary>
		/// Optional EMA confirmation. Returns true when the filter is disabled, so the base
		/// candle-colour logic is untouched unless the user explicitly opts in.
		/// </summary>
		private bool PassesEMAFilter(bool isLong)
		{
			if (!UseEmaFilter)
				return true;

			if (ema1 == null || ema2 == null)
				return true;

			double price = Close[0];
			double e1    = ema1[0];
			double e2    = ema2[0];

			switch (EmaFilterMode)
			{
				case MsfcEmaFilterMode.PriceVsEma1:
					return isLong ? price > e1 : price < e1;

				case MsfcEmaFilterMode.PriceVsEma2:
					return isLong ? price > e2 : price < e2;

				case MsfcEmaFilterMode.Ema1VsEma2:
					return isLong ? e1 > e2 : e1 < e2;

				case MsfcEmaFilterMode.PriceVsBothEmas:
					return isLong ? (price > e1 && price > e2) : (price < e1 && price < e2);
			}

			return true;
		}

		/// <summary>Optional VWAP confirmation. Long requires close above VWAP, short below.</summary>
		private bool PassesVWAPFilter(bool isLong)
		{
			if (!UseVwapFilter)
				return true;

			if (vwapCumulativeVolume <= 0)
				return false; // no volume accumulated yet - refuse rather than trade on a stale value

			return isLong ? Close[0] > vwapValue : Close[0] < vwapValue;
		}

		#endregion

		#region Signal processing and entries

		private void ProcessFirstCandles(DateTime barOpenSessionTime)
		{
			List<SessionRuntime> matches = GetSessionsForCurrentBar(barOpenSessionTime);

			for (int i = 0; i < matches.Count; i++)
				ProcessFirstCandle(matches[i], barOpenSessionTime);
		}

		/// <summary>
		/// Evaluates the closed first candle of one session and submits an entry when every gate passes.
		/// The session's first candle is marked as consumed regardless of the outcome, so a rejected
		/// signal can never be re-evaluated on a later bar of the same session occurrence.
		/// </summary>
		private void ProcessFirstCandle(SessionRuntime s, DateTime barOpenSessionTime)
		{
			s.FirstCandleProcessed = true;

			// --- gates that do not depend on candle direction ---------------------------------------
			if (Position.MarketPosition != MarketPosition.Flat)
			{
				Print(FormatSkip(s, barOpenSessionTime, "a position is already open"));
				return;
			}

			if (pendingEntrySignal != null)
			{
				Print(FormatSkip(s, barOpenSessionTime, "a previous entry order is still working"));
				return;
			}

			if (!AllowNewTradeAfterClose && aTradeClosedToday)
			{
				Print(FormatSkip(s, barOpenSessionTime,
					"'Allow New Trade After Previous Trade Closes' is OFF and a trade already closed today"));
				return;
			}

			if (dailyTradeCount >= MaxTradesPerDay)
			{
				Print(FormatSkip(s, barOpenSessionTime, "the daily trade limit has been reached"));
				return;
			}

			if (s.TradesTaken >= s.MaxTrades)
			{
				Print(FormatSkip(s, barOpenSessionTime, "this session's trade limit has been reached"));
				return;
			}

			// --- candle direction (only knowable now, at the close of the candle) -------------------
			bool isLong;

			if (Close[0] > Open[0])
			{
				isLong = true;
			}
			else if (Close[0] < Open[0])
			{
				isLong = false;
			}
			else
			{
				if (DojiAction == MsfcDojiAction.NoTrade)
				{
					Print(FormatSkip(s, barOpenSessionTime, "the first candle is a doji and Doji Action = NoTrade"));
					return;
				}

				isLong = DojiAction == MsfcDojiAction.Long;
			}

			// --- optional filters -------------------------------------------------------------------
			if (!PassesEMAFilter(isLong))
			{
				Print(FormatSkip(s, barOpenSessionTime, "the EMA filter rejected the signal"));
				return;
			}

			if (!PassesVWAPFilter(isLong))
			{
				Print(FormatSkip(s, barOpenSessionTime, "the VWAP filter rejected the signal"));
				return;
			}

			// --- bracket (the stop distance is only knowable once the candle has closed) -------------
			MsfcTradePlan plan;
			string        planReason;

			if (!TryBuildTradePlan(isLong, out plan, out planReason))
			{
				Print(FormatSkip(s, barOpenSessionTime, planReason));
				return;
			}

			// --- position sizing (last gate: a signal that cannot be sized is not taken) -------------
			int    quantity;
			string sizingReason;

			if (!TryCalculatePositionSize(plan.RiskPoints, out quantity, out sizingReason))
			{
				Print(FormatSkip(s, barOpenSessionTime, sizingReason));
				return;
			}

			if (isLong)
				EnterLongTrade(s, barOpenSessionTime, quantity, plan);
			else
				EnterShortTrade(s, barOpenSessionTime, quantity, plan);
		}

		private string BuildSignalName(SessionRuntime s, bool isLong)
		{
			// The signal name is unique per session and direction so SetStopLoss / SetProfitTarget can
			// be bound to this specific entry via StopTargetHandling.PerEntryExecution.
			return s.Label + (isLong ? "_Long" : "_Short");
		}

		/// <summary>
		/// One trade's bracket, fixed at the moment the signal is generated.
		///
		/// A fixed-distance leg is expressed in TICKS so NinjaTrader anchors it to the
		/// ACTUAL average fill price. A structural leg (the candle extreme) is expressed
		/// as an absolute PRICE, because the whole point of that stop is the level
		/// itself — anchoring it to a fill that gapped would move it off the candle.
		/// </summary>
		private struct MsfcTradePlan
		{
			public bool   StopIsAbsolute;
			public double StopPrice;
			public int    StopTicks;

			public bool   TargetIsAbsolute;
			public double TargetPrice;
			public int    TargetTicks;

			/// <summary>Signal-time risk distance in points; drives sizing and the R multiple.</summary>
			public double RiskPoints;
			/// <summary>Close of the signal candle — the price the decision was made at.</summary>
			public double SignalPrice;
		}

		/// <summary>
		/// Builds the bracket for a signal, or explains why no valid one exists.
		/// Called at the close of the first candle, so every input is from a closed bar.
		/// </summary>
		private bool TryBuildTradePlan(bool isLong, out MsfcTradePlan plan, out string reason)
		{
			plan   = new MsfcTradePlan();
			reason = string.Empty;

			double signalPrice = Close[0];
			plan.SignalPrice   = signalPrice;

			// ---- stop -------------------------------------------------------------------------
			if (StopMode == MsfcStopMode.FirstCandleExtreme)
			{
				double buffer = Math.Max(0.0, StopBufferTicks) * TickSize;
				double raw    = isLong ? Low[0] - buffer : High[0] + buffer;

				plan.StopIsAbsolute = true;
				plan.StopPrice      = Instrument.MasterInstrument.RoundToTickSize(raw);
				plan.RiskPoints     = isLong ? signalPrice - plan.StopPrice : plan.StopPrice - signalPrice;

				if (plan.RiskPoints < TickSize)
				{
					reason = string.Format(CultureInfo.InvariantCulture,
						"the first candle's {0} ({1}) is less than one tick from its close ({2}), so a "
						+ "candle-extreme stop would be at or through the entry",
						isLong ? "low" : "high", plan.StopPrice, signalPrice);
					return false;
				}
			}
			else
			{
				if (stopLossTicks <= 0)
				{
					reason = "the fixed stop distance resolves to zero ticks";
					return false;
				}

				plan.StopIsAbsolute = false;
				plan.StopTicks      = stopLossTicks;
				plan.RiskPoints     = stopLossTicks * TickSize;
			}

			// ---- target -----------------------------------------------------------------------
			if (TargetMode == MsfcTargetMode.RewardMultiple)
			{
				double reward = plan.RiskPoints * RewardRatio;

				plan.TargetIsAbsolute = true;
				plan.TargetPrice      = Instrument.MasterInstrument.RoundToTickSize(
					isLong ? signalPrice + reward : signalPrice - reward);

				bool viable = isLong
					? plan.TargetPrice >= signalPrice + TickSize
					: plan.TargetPrice <= signalPrice - TickSize;

				if (!viable)
				{
					reason = string.Format(CultureInfo.InvariantCulture,
						"a {0}R target on a {1} point risk rounds back onto the entry price",
						RewardRatio, plan.RiskPoints);
					return false;
				}
			}
			else
			{
				if (takeProfitTicks <= 0)
				{
					reason = "the fixed target distance resolves to zero ticks";
					return false;
				}

				plan.TargetIsAbsolute = false;
				plan.TargetTicks      = takeProfitTicks;
			}

			return true;
		}

		/// <summary>
		/// Dollar risk of ONE contract at the configured stop distance.
		/// Uses the instrument's real PointValue, so the same settings behave correctly on MNQ
		/// ($2/point), NQ ($20/point), ES, CL, GC etc. without any change.
		/// </summary>
		private double RiskPerContract(double stopPoints)
		{
			double pointValue = Instrument != null && Instrument.MasterInstrument != null
				? Instrument.MasterInstrument.PointValue
				: 0;

			return stopPoints * pointValue;
		}

		/// <summary>
		/// Position sizing.
		///
		/// Target risk is RiskPerTradeDollars with a tolerance of +/- RiskBufferDollars, i.e. the
		/// accepted band is [target - buffer, target + buffer]  ->  $70 to $130 by default.
		///
		/// The stop distance is known BEFORE the entry is submitted — either the fixed setting or the
		/// measured distance from the signal candle's close to its extreme — so the dollar risk of a
		/// given quantity is exact and does not depend on the fill price.
		///
		/// Algorithm:
		///   1. Ideal quantity = target / riskPerContract.
		///   2. Consider floor() and ceil() of that ideal (both clamped to [1, MaxContracts]).
		///   3. Keep only candidates whose total risk falls inside the band.
		///   4. Of those, choose the one closest to the target; on a tie choose the SMALLER quantity,
		///      because under-risking is the cheaper error.
		///   5. If no whole number of contracts lands inside the band, the trade is SKIPPED and the
		///      reason is printed. Nothing is forced through at the wrong size.
		///
		/// Example (MNQ, $2/point, 20-point stop = $40/contract, target $100, buffer $30):
		///   ideal = 2.5  ->  candidates 2 ($80, inside) and 3 ($120, inside)
		///   |80-100| == |120-100|  ->  tie  ->  2 contracts.
		/// Example (MNQ, 60-point stop = $120/contract):
		///   ideal = 0.83 -> candidate 1 ($120, inside band) -> 1 contract.
		/// Example (MNQ, 80-point stop = $160/contract):
		///   ideal = 0.63 -> candidate 1 ($160, ABOVE $130) -> no valid size -> trade skipped.
		/// </summary>
		private bool TryCalculatePositionSize(double stopPoints, out int quantity, out string reason)
		{
			quantity = 0;
			reason   = string.Empty;

			double riskPerContract = RiskPerContract(stopPoints);

			if (riskPerContract <= 0)
			{
				reason = "the per-contract risk could not be computed (PointValue or stop distance is zero)";
				return false;
			}

			double target = RiskPerTradeDollars;
			double lower  = target - RiskBufferDollars;
			double upper  = target + RiskBufferDollars;

			double ideal = target / riskPerContract;

			int low  = Math.Max(1, (int)Math.Floor(ideal));
			int high = Math.Max(1, (int)Math.Ceiling(ideal));

			int    best         = 0;
			double bestDistance = double.MaxValue;

			for (int q = low; q <= high; q++)
			{
				if (q < 1 || q > MaxContracts)
					continue;

				double risk = q * riskPerContract;

				if (risk < lower || risk > upper)
					continue;

				double distance = Math.Abs(risk - target);

				// Strictly-less-than keeps the SMALLER quantity on an exact tie, because the loop
				// walks upward from the smaller candidate.
				if (distance < bestDistance)
				{
					bestDistance = distance;
					best         = q;
				}
			}

			if (best == 0)
			{
				reason = string.Format(CultureInfo.InvariantCulture,
					"no whole number of contracts fits the risk band: 1 contract risks {0:C} at a {1} point stop, "
					+ "and the accepted band is {2:C} to {3:C} (target {4:C} +/- {5:C})",
					riskPerContract, stopPoints, lower, upper, target, RiskBufferDollars);
				return false;
			}

			quantity = best;
			return true;
		}

		/// <summary>
		/// Printed once at startup so the size the strategy will actually trade is visible before any
		/// money is on the line, rather than being discovered from the first fill.
		/// </summary>
		private void PrintSizingPlan()
		{
			Print(string.Format(CultureInfo.InvariantCulture,
				"[SIZING] Every trade is risk-sized: target {0:C} +/- {1:C} (band {2:C}-{3:C}), max {4} contracts. "
				+ "Stop = {5}. Target = {6}.",
				RiskPerTradeDollars, RiskBufferDollars,
				RiskPerTradeDollars - RiskBufferDollars, RiskPerTradeDollars + RiskBufferDollars,
				MaxContracts,
				StopMode == MsfcStopMode.FirstCandleExtreme
					? "the first candle's own extreme" + (StopBufferTicks > 0
						? " plus a " + StopBufferTicks.ToString("0.##", CultureInfo.InvariantCulture) + " tick buffer"
						: string.Empty)
					: (stopLossTicks * TickSize).ToString("0.#####", CultureInfo.InvariantCulture) + " points (fixed)",
				TargetMode == MsfcTargetMode.RewardMultiple
					? RewardRatio.ToString("0.##", CultureInfo.InvariantCulture) + "R"
					: (takeProfitTicks * TickSize).ToString("0.#####", CultureInfo.InvariantCulture) + " points (fixed)"));

			// A first-candle stop has no fixed distance, so there is no single size to
			// pre-compute. Only the fixed-distance case can be shown ahead of a signal.
			if (StopMode != MsfcStopMode.FixedDistance)
			{
				Print("[SIZING] Stop distance varies per signal, so the contract count is decided at each "
					+ "signal and printed with it. A signal whose risk cannot be fitted to the band is skipped.");
				return;
			}

			int    quantity;
			string reason;
			double stopPoints  = stopLossTicks * TickSize;
			double perContract = RiskPerContract(stopPoints);

			if (TryCalculatePositionSize(stopPoints, out quantity, out reason))
				Print(string.Format(CultureInfo.InvariantCulture,
					"[SIZING] Stop {0} points = {1:C}/contract -> {2} contract(s) = {3:C} risk per trade.",
					stopPoints, perContract, quantity, quantity * perContract));
			else
				Print("[SIZING] WARNING - with the current settings no trade can be sized: " + reason
					+ ". Every signal will be skipped until the stop distance or the risk band changes.");
		}

		private void EnterLongTrade(SessionRuntime s, DateTime barOpenSessionTime, int quantity, MsfcTradePlan plan)
		{
			string signal = BuildSignalName(s, true);
			activePlan       = plan;
			activeSignalName = signal;
			SetTradeTargets(signal, plan);
			RegisterPendingEntry(s, signal, true, barOpenSessionTime, quantity);
			EnterLong(quantity, signal);
		}

		private void EnterShortTrade(SessionRuntime s, DateTime barOpenSessionTime, int quantity, MsfcTradePlan plan)
		{
			string signal = BuildSignalName(s, false);
			activePlan       = plan;
			activeSignalName = signal;
			SetTradeTargets(signal, plan);
			RegisterPendingEntry(s, signal, false, barOpenSessionTime, quantity);
			EnterShort(quantity, signal);
		}

		/// <summary>
		/// Attaches the protective bracket.
		///
		/// A FIXED-DISTANCE leg uses CalculationMode.Ticks deliberately: NinjaTrader then anchors it to
		/// the ACTUAL average fill price, which in a backtest is the open of the next bar rather than
		/// the close of the signal candle. An absolute price derived from Close[0] would silently
		/// misplace it by the overnight / next-bar gap.
		///
		/// A STRUCTURAL leg (the candle extreme, or an R multiple of it) uses CalculationMode.Price,
		/// because there the LEVEL is the decision. Re-anchoring it to a gapped fill would move the
		/// stop off the candle it is supposed to sit behind.
		/// </summary>
		private void SetTradeTargets(string signalName, MsfcTradePlan plan)
		{
			if (plan.StopIsAbsolute)
				SetStopLoss(signalName, CalculationMode.Price, plan.StopPrice, false);
			else
				SetStopLoss(signalName, CalculationMode.Ticks, plan.StopTicks, false);

			if (plan.TargetIsAbsolute)
				SetProfitTarget(signalName, CalculationMode.Price, plan.TargetPrice);
			else
				SetProfitTarget(signalName, CalculationMode.Ticks, plan.TargetTicks);
		}

		private void RegisterPendingEntry(SessionRuntime s, string signal, bool isLong,
										  DateTime barOpenSessionTime, int quantity)
		{
			pendingEntrySignal       = signal;
			pendingEntrySessionIndex = s.Index;

			// Counters increment on submission so that two sessions sharing the same start minute can
			// never both slip past the limit inside one bar.
			s.TradesTaken++;
			s.TotalTrades++;
			dailyTradeCount++;

			if (isLong) statLongTrades++; else statShortTrades++;

			activeSessionIndex = s.Index;
			activeSessionEnd   = s.CurrentOccurrenceEnd;

			double perContract = RiskPerContract(activePlan.RiskPoints);

			Print(string.Format(CultureInfo.InvariantCulture,
				"[SIGNAL] {0:yyyy-MM-dd HH:mm} ET | Session {1} first candle O={2} H={3} L={4} C={5} -> {6} | "
				+ "stop {7} ({8:0.##} pts) | qty {9} @ {10:C}/contract = {11:C} risk | "
				+ "session trades {12}/{13}, daily trades {14}/{15}",
				barOpenSessionTime, s.Index, Open[0], High[0], Low[0], Close[0], isLong ? "LONG" : "SHORT",
				activePlan.StopIsAbsolute
					? activePlan.StopPrice.ToString("0.#####", CultureInfo.InvariantCulture)
					: "fill -/+ " + (activePlan.StopTicks * TickSize).ToString("0.#####", CultureInfo.InvariantCulture),
				activePlan.RiskPoints,
				quantity, perContract, quantity * perContract,
				s.TradesTaken, s.MaxTrades, dailyTradeCount, MaxTradesPerDay));
		}

		private string FormatSkip(SessionRuntime s, DateTime barOpenSessionTime, string reason)
		{
			return string.Format(CultureInfo.InvariantCulture,
				"[SKIP] {0:yyyy-MM-dd HH:mm} ET | Session {1} first candle ignored because {2}.",
				barOpenSessionTime, s.Index, reason);
		}

		#endregion

		#region Trade management

		/// <summary>
		/// Optional flatten when the session that produced the trade ends. Off by default: the base
		/// specification manages trades with the SL/TP bracket only.
		/// </summary>
		private void CheckSessionEndExit(DateTime barOpenSessionTime)
		{
			if (!ExitAtSessionEnd || !inTrade || activeSessionEnd == DateTime.MinValue)
				return;

			if (barOpenSessionTime < activeSessionEnd)
				return;

			// Exit the quantity actually held, not the static parameter - risk-based sizing means the
			// two are usually different.
			if (Position.MarketPosition == MarketPosition.Long)
				ExitLong(Position.Quantity, "SessionEnd", BuildSignalNameForActive());
			else if (Position.MarketPosition == MarketPosition.Short)
				ExitShort(Position.Quantity, "SessionEnd", BuildSignalNameForActive());
		}

		private string BuildSignalNameForActive()
		{
			return "S" + activeSessionIndex.ToString(CultureInfo.InvariantCulture)
				 + (activeDirection == MarketPosition.Long ? "_Long" : "_Short");
		}

		protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice, int quantity,
											  int filled, double averageFillPrice, OrderState orderState,
											  DateTime time, ErrorCode error, string comment)
		{
			if (order == null || pendingEntrySignal == null || order.Name != pendingEntrySignal)
				return;

			if (orderState == OrderState.Rejected || orderState == OrderState.Cancelled)
			{
				// Roll the counters back: no trade actually happened.
				SessionRuntime s = FindSession(pendingEntrySessionIndex);
				if (s != null)
				{
					s.TradesTaken = Math.Max(0, s.TradesTaken - 1);
					s.TotalTrades = Math.Max(0, s.TotalTrades - 1);
				}

				dailyTradeCount = Math.Max(0, dailyTradeCount - 1);

				Print(string.Format(CultureInfo.InvariantCulture,
					"[ORDER] Entry '{0}' {1}. Counters rolled back. {2}", order.Name, orderState, error));

				pendingEntrySignal = null;
			}
			else if (orderState == OrderState.Filled)
			{
				pendingEntrySignal = null;
			}
		}

		protected override void OnExecutionUpdate(Execution execution, string executionId, double price,
												  int quantity, MarketPosition marketPosition, string orderId,
												  DateTime time)
		{
			if (execution == null || execution.Order == null)
				return;

			// NinjaTrader does not hand us an "is this an exit" flag, so derive it from the order
			// action. Entries are submitted as Buy / SellShort; every exit produced by ExitLong,
			// ExitShort, SetStopLoss or SetProfitTarget is a Sell / BuyToCover.
			bool isExit = execution.Order.OrderAction == OrderAction.Sell
					   || execution.Order.OrderAction == OrderAction.BuyToCover;

			if (execution.Order.OrderState != OrderState.Filled &&
				execution.Order.OrderState != OrderState.PartFilled)
				return;

			if (!isExit && Position.MarketPosition != MarketPosition.Flat)
			{
				// A partially filled entry raises several executions; only the first one opens a new
				// trade record, the rest simply re-anchor the bracket to the updated average price.
				OnEntryFilled(!inTrade);
				return;
			}

			if (isExit && Position.MarketPosition == MarketPosition.Flat && inTrade)
				OnExitFilled(execution, price);
		}

		private void OnEntryFilled(bool isNewTrade)
		{
			activeDirection  = Position.MarketPosition;
			activeEntryPrice = Position.AveragePrice;

			if (isNewTrade)
			{
				activeEntryBar = CurrentBar;
				activeTradeId  = ++tradeIdSeed;
				inTrade        = true;
			}

			bool isLong = activeDirection == MarketPosition.Long;

			// The stop: an absolute level stays exactly where it was decided; a fixed
			// distance is measured from the average fill, which is what Ticks mode did.
			if (activePlan.StopIsAbsolute)
			{
				activeStopPrice = activePlan.StopPrice;
			}
			else
			{
				double slOffset = activePlan.StopTicks * TickSize;
				activeStopPrice = Instrument.MasterInstrument.RoundToTickSize(
					isLong ? activeEntryPrice - slOffset : activeEntryPrice + slOffset);
			}

			// The target. Under RewardMultiple it is RE-ANCHORED to the real fill so the
			// R multiple is measured against the risk actually taken, not the risk that
			// was estimated from the signal candle's close before the bar gapped.
			if (TargetMode == MsfcTargetMode.RewardMultiple)
			{
				double realisedRisk = Math.Abs(activeEntryPrice - activeStopPrice);

				activeTargetPrice = Instrument.MasterInstrument.RoundToTickSize(isLong
					? activeEntryPrice + realisedRisk * RewardRatio
					: activeEntryPrice - realisedRisk * RewardRatio);

				if (!string.IsNullOrEmpty(activeSignalName))
					SetProfitTarget(activeSignalName, CalculationMode.Price, activeTargetPrice);
			}
			else if (activePlan.TargetIsAbsolute)
			{
				activeTargetPrice = activePlan.TargetPrice;
			}
			else
			{
				double tpOffset = activePlan.TargetTicks * TickSize;
				activeTargetPrice = Instrument.MasterInstrument.RoundToTickSize(
					isLong ? activeEntryPrice + tpOffset : activeEntryPrice - tpOffset);
			}

			if (isNewTrade)
			{
				double riskPoints = Math.Abs(activeEntryPrice - activeStopPrice);

				Print(string.Format(CultureInfo.InvariantCulture,
					"[ENTRY] #{0} {1} {2} @ {3} | SL {4} ({5}) | TP {6} ({7}) | session {8} | "
					+ "risk {9:0.##} pts = {10:C} | R:R {11:0.00}",
					activeTradeId, Position.Quantity, activeDirection, activeEntryPrice,
					activeStopPrice, activePlan.StopIsAbsolute ? "first-candle extreme" : "fixed distance",
					activeTargetPrice, TargetMode == MsfcTargetMode.RewardMultiple
						? RewardRatio.ToString("0.##", CultureInfo.InvariantCulture) + "R" : "fixed distance",
					activeSessionIndex,
					riskPoints, Position.Quantity * RiskPerContract(riskPoints),
					riskPoints > 0 ? Math.Abs(activeTargetPrice - activeEntryPrice) / riskPoints : 0.0));
			}

			UpdateTradeVisuals();
		}

		private void OnExitFilled(Execution execution, double exitPrice)
		{
			// Classify the exit. NinjaTrader names the orders generated by SetStopLoss / SetProfitTarget
			// "Stop loss" and "Profit target"; the price comparison is a defensive fallback.
			string orderName = execution.Order.Name ?? string.Empty;
			bool   isTarget  = orderName.IndexOf("Profit target", StringComparison.OrdinalIgnoreCase) >= 0;
			bool   isStop    = orderName.IndexOf("Stop loss",     StringComparison.OrdinalIgnoreCase) >= 0;

			double signedTicks = (activeDirection == MarketPosition.Long
				? exitPrice - activeEntryPrice
				: activeEntryPrice - exitPrice) / TickSize;

			if (!isTarget && !isStop)
			{
				statOtherExits++;
			}
			else if (isTarget)
			{
				statTpHits++;
			}
			else
			{
				statSlHits++;
			}

			bool isWin = signedTicks > 0;

			if (isWin)
			{
				statWins++;
				consecutiveWins++;
				consecutiveLosses = 0;
				maxConsecutiveWins = Math.Max(maxConsecutiveWins, consecutiveWins);
			}
			else
			{
				statLosses++;
				consecutiveLosses++;
				consecutiveWins = 0;
				maxConsecutiveLosses = Math.Max(maxConsecutiveLosses, consecutiveLosses);
			}

			aTradeClosedToday = true;

			Print(string.Format(CultureInfo.InvariantCulture,
				"[EXIT ] #{0} {1} @ {2} via '{3}' | {4:0.0} ticks | {5}",
				activeTradeId, activeDirection, exitPrice, orderName, signedTicks, isWin ? "WIN" : "LOSS"));

			FinalizeTradeVisuals(isTarget ? "TP" : (isStop ? "SL" : "EXIT"), exitPrice);
			ResetTradeState();
		}

		private void ResetTradeState()
		{
			inTrade            = false;
			activeDirection    = MarketPosition.Flat;
			activeEntryPrice   = 0;
			activeStopPrice    = 0;
			activeTargetPrice  = 0;
			activeEntryBar     = -1;
			activeSessionEnd   = DateTime.MinValue;
			pendingEntrySignal = null;
			activeSignalName   = string.Empty;
			activePlan         = new MsfcTradePlan();
		}

		private SessionRuntime FindSession(int index)
		{
			for (int i = 0; i < sessions.Count; i++)
				if (sessions[i].Index == index)
					return sessions[i];

			return null;
		}

		#endregion

		#region Visualization

		private void FreezeBrushes()
		{
			// Frozen brushes are a NinjaTrader 8 rendering requirement for performance.
			if (EntryLineBrush   != null && !EntryLineBrush.IsFrozen   && EntryLineBrush.CanFreeze)   EntryLineBrush.Freeze();
			if (StopLossBrush    != null && !StopLossBrush.IsFrozen    && StopLossBrush.CanFreeze)    StopLossBrush.Freeze();
			if (TakeProfitBrush  != null && !TakeProfitBrush.IsFrozen  && TakeProfitBrush.CanFreeze)  TakeProfitBrush.Freeze();
		}

		private bool DrawingAllowed()
		{
			if (!ShowTradeZones)
				return false;

			if (State == State.Historical && !DrawOnHistoricalBars)
				return false;

			return true;
		}

		private string TagPrefix(int tradeId)
		{
			return "MSFC_" + tradeId.ToString(CultureInfo.InvariantCulture) + "_";
		}

		/// <summary>
		/// Redraws the live trade's zones anchored from the entry bar to the current bar. Re-using the
		/// same tags means NinjaTrader updates the existing objects instead of creating new ones, so an
		/// open trade costs a fixed five drawing objects regardless of how long it lasts.
		/// </summary>
		private void UpdateTradeVisuals()
		{
			if (!inTrade || !DrawingAllowed() || activeEntryBar < 0)
				return;

			int startBarsAgo = CurrentBar - activeEntryBar;
			if (startBarsAgo < 0)
				return;

			DrawTradeZones(activeTradeId, startBarsAgo, 0);
		}

		private void DrawTradeZones(int tradeId, int startBarsAgo, int endBarsAgo)
		{
			string p = TagPrefix(tradeId);

			if (ShowRiskZone)
				Draw.Rectangle(this, p + "Risk", false, startBarsAgo, activeEntryPrice, endBarsAgo,
							   activeStopPrice, Brushes.Transparent, StopLossBrush, ZoneOpacity);

			if (ShowRewardZone)
				Draw.Rectangle(this, p + "Reward", false, startBarsAgo, activeEntryPrice, endBarsAgo,
							   activeTargetPrice, Brushes.Transparent, TakeProfitBrush, ZoneOpacity);

			if (ShowEntryLine)
				Draw.Line(this, p + "Entry", false, startBarsAgo, activeEntryPrice, endBarsAgo,
						  activeEntryPrice, EntryLineBrush, DashStyleHelper.Solid, 2);

			if (ShowStopLossLine)
				Draw.Line(this, p + "Stop", false, startBarsAgo, activeStopPrice, endBarsAgo,
						  activeStopPrice, StopLossBrush, DashStyleHelper.Dash, 2);

			if (ShowTakeProfitLine)
				Draw.Line(this, p + "Target", false, startBarsAgo, activeTargetPrice, endBarsAgo,
						  activeTargetPrice, TakeProfitBrush, DashStyleHelper.Dash, 2);
		}

		/// <summary>
		/// Freezes the zones at the exit bar and stamps the outcome, so a closed trade is visually
		/// distinct from a live one. Also enforces the drawing-object budget.
		/// </summary>
		private void FinalizeTradeVisuals(string result, double exitPrice)
		{
			if (!DrawingAllowed() || activeEntryBar < 0)
				return;

			int startBarsAgo = CurrentBar - activeEntryBar;
			if (startBarsAgo < 0)
				return;

			DrawTradeZones(activeTradeId, startBarsAgo, 0);

			string p = TagPrefix(activeTradeId);

			Draw.Text(this, p + "Result", result, 0,
					  exitPrice + (activeDirection == MarketPosition.Long ? 2 : -2) * TickSize,
					  result == "TP" ? TakeProfitBrush : StopLossBrush);

			drawnTradeIds.Add(activeTradeId);
			PruneOldDrawings();
		}

		private void PruneOldDrawings()
		{
			while (drawnTradeIds.Count > Math.Max(1, MaxDrawnTrades))
			{
				int oldest = drawnTradeIds[0];
				drawnTradeIds.RemoveAt(0);

				string p = TagPrefix(oldest);
				RemoveDrawObject(p + "Risk");
				RemoveDrawObject(p + "Reward");
				RemoveDrawObject(p + "Entry");
				RemoveDrawObject(p + "Stop");
				RemoveDrawObject(p + "Target");
				RemoveDrawObject(p + "Result");
			}
		}

		#endregion

		#region Custom statistics summary

		/// <summary>
		/// NinjaTrader's own performance engine supplies Net Profit, Profit Factor, Sharpe, drawdown and
		/// the rest, so none of that is duplicated here. Only the metrics specific to THIS strategy are
		/// reported, printed to the Output window at the end of a run.
		/// </summary>
		private void PrintCustomSummary()
		{
			StringBuilder sb = new StringBuilder();

			sb.AppendLine("===== Multi-Session First Candle Strategy - custom metrics =====");
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Instrument        : {0}",
				Instrument != null ? Instrument.FullName : "n/a"));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Session 1 trades  : {0}", sessions[0].TotalTrades));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Session 2 trades  : {0}", sessions[1].TotalTrades));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Session 3 trades  : {0}", sessions[2].TotalTrades));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Long trades       : {0}", statLongTrades));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Short trades      : {0}", statShortTrades));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Take-profit hits  : {0}", statTpHits));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Stop-loss hits    : {0}", statSlHits));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Other exits       : {0}", statOtherExits));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Wins / Losses     : {0} / {1}", statWins, statLosses));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Max consec. wins  : {0}", maxConsecutiveWins));
			sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Max consec. losses: {0}", maxConsecutiveLosses));
			sb.AppendLine("================================================================");

			Print(sb.ToString());
		}

		#endregion

		#region Properties - 1) General

		[NinjaScriptProperty]
		[Display(Name = "Enable Strategy", Order = 1, GroupName = "1) General")]
		public bool EnableStrategy { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Maximum Trades Per Day", Order = 3, GroupName = "1) General")]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow New Trade After Previous Trade Closes", Order = 4, GroupName = "1) General")]
		public bool AllowNewTradeAfterClose { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Daily Reset Mode", Order = 5, GroupName = "1) General")]
		public MsfcDayResetMode DayResetMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session Time Zone Mode", Order = 6, GroupName = "1) General")]
		public MsfcTimeZoneMode TimeZoneMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session timezone (the zone you TYPE the times in)",
				 Description = "Defaults to Asia/Kolkata. Session start/end values below are read as clock times in this zone.",
				 Order = 7, GroupName = "1) General")]
		public string SessionTimeZoneId { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Times are SUMMER (auto-adjust for winter)",
				 Description = "ON: type every session as its US-DST (summer) time and the strategy pins it to the equivalent US Eastern time, so in winter it shifts an hour later on your clock automatically and keeps tracking the same market hours. OFF: the times are taken literally and never move.",
				 Order = 8, GroupName = "1) General")]
		public bool AutoAdjustForUsDst { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Strict First Candle (must open exactly at session start)", Order = 9, GroupName = "1) General")]
		public bool StrictFirstCandle { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session Overlap Mode", Order = 10, GroupName = "1) General")]
		public MsfcOverlapMode OverlapMode { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Print Summary To Output Window", Order = 9, GroupName = "1) General")]
		public bool PrintSummaryToOutput { get; set; }

		#endregion

		#region Properties - 2) Session 1

		[NinjaScriptProperty]
		[Display(Name = "Enable Session 1", Order = 1, GroupName = "2) Session 1")]
		public bool EnableSession1 { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 1 Start (HHmm, Eastern)", Order = 2, GroupName = "2) Session 1")]
		public int Session1Start { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 1 End (HHmm, Eastern)", Order = 3, GroupName = "2) Session 1")]
		public int Session1End { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Session 1 Max Trades", Order = 4, GroupName = "2) Session 1")]
		public int Session1MaxTrades { get; set; }

		#endregion

		#region Properties - 3) Session 2

		[NinjaScriptProperty]
		[Display(Name = "Enable Session 2", Order = 1, GroupName = "3) Session 2")]
		public bool EnableSession2 { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 2 Start (HHmm, Eastern)", Order = 2, GroupName = "3) Session 2")]
		public int Session2Start { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 2 End (HHmm, Eastern)", Order = 3, GroupName = "3) Session 2")]
		public int Session2End { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Session 2 Max Trades", Order = 4, GroupName = "3) Session 2")]
		public int Session2MaxTrades { get; set; }

		#endregion

		#region Properties - 4) Session 3

		[NinjaScriptProperty]
		[Display(Name = "Enable Session 3", Order = 1, GroupName = "4) Session 3")]
		public bool EnableSession3 { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 3 Start (HHmm, Eastern)", Order = 2, GroupName = "4) Session 3")]
		public int Session3Start { get; set; }

		[NinjaScriptProperty]
		[Range(0, 2359)]
		[Display(Name = "Session 3 End (HHmm, Eastern)", Order = 3, GroupName = "4) Session 3")]
		public int Session3End { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Session 3 Max Trades", Order = 4, GroupName = "4) Session 3")]
		public int Session3MaxTrades { get; set; }

		#endregion

		#region Properties - 5) Risk Management

		[NinjaScriptProperty]
		[Display(Name = "Stop Mode",
				 Description = "FixedDistance: the stop sits a set distance from the entry. "
							 + "FirstCandleExtreme: the stop sits at the first candle's own LOW for a long "
							 + "or its HIGH for a short, so the risk is whatever that candle actually was.",
				 Order = 1, GroupName = "5) Risk Management")]
		public MsfcStopMode StopMode { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Stop Buffer (ticks beyond the candle extreme)",
				 Description = "Only used when Stop Mode = FirstCandleExtreme. Pushes the stop this many "
							 + "ticks past the candle's high/low so a one-tick poke does not take it out.",
				 Order = 2, GroupName = "5) Risk Management")]
		public double StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Target Mode",
				 Description = "FixedDistance: the target sits a set distance from the entry. "
							 + "RewardMultiple: the target is a multiple of the risk actually taken (R:R).",
				 Order = 3, GroupName = "5) Risk Management")]
		public MsfcTargetMode TargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.01, double.MaxValue)]
		[Display(Name = "Reward Ratio (R)",
				 Description = "Only used when Target Mode = RewardMultiple. 2 means the target is twice "
							 + "the distance to the stop, measured from the actual fill price.",
				 Order = 4, GroupName = "5) Risk Management")]
		public double RewardRatio { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Distance Unit (for the fixed modes)", Order = 5, GroupName = "5) Risk Management")]
		public MsfcDistanceUnit DistanceUnit { get; set; }

		[NinjaScriptProperty]
		[Range(0.0001, double.MaxValue)]
		[Display(Name = "Stop Loss Value (fixed mode only)", Order = 6, GroupName = "5) Risk Management")]
		public double StopLossValue { get; set; }

		[NinjaScriptProperty]
		[Range(0.0001, double.MaxValue)]
		[Display(Name = "Take Profit Value (fixed mode only)", Order = 7, GroupName = "5) Risk Management")]
		public double TakeProfitValue { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Doji Action", Order = 8, GroupName = "5) Risk Management")]
		public MsfcDojiAction DojiAction { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Exit At End Of Originating Session", Order = 9, GroupName = "5) Risk Management")]
		public bool ExitAtSessionEnd { get; set; }

		[NinjaScriptProperty]
		[Range(1, double.MaxValue)]
		[Display(Name = "Risk Per Trade ($)", Order = 10, GroupName = "5) Risk Management")]
		public double RiskPerTradeDollars { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Risk Buffer +/- ($)", Order = 11, GroupName = "5) Risk Management")]
		public double RiskBufferDollars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Max Contracts", Order = 12, GroupName = "5) Risk Management")]
		public int MaxContracts { get; set; }

		#endregion

		#region Properties - 6) EMA

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 1 Period", Order = 1, GroupName = "6) EMA")]
		public int EmaPeriod1 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 2 Period", Order = 2, GroupName = "6) EMA")]
		public int EmaPeriod2 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 1", Order = 3, GroupName = "6) EMA")]
		public bool ShowEma1 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show EMA 2", Order = 4, GroupName = "6) EMA")]
		public bool ShowEma2 { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use EMA Filter", Order = 5, GroupName = "6) EMA")]
		public bool UseEmaFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "EMA Filter Mode", Order = 6, GroupName = "6) EMA")]
		public MsfcEmaFilterMode EmaFilterMode { get; set; }

		#endregion

		#region Properties - 7) VWAP

		[NinjaScriptProperty]
		[Display(Name = "Show VWAP", Order = 1, GroupName = "7) VWAP")]
		public bool ShowVwap { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VWAP Filter", Order = 2, GroupName = "7) VWAP")]
		public bool UseVwapFilter { get; set; }

		#endregion

		#region Properties - 8) Visualization

		[NinjaScriptProperty]
		[Display(Name = "Show Trade Zones (master switch)", Order = 1, GroupName = "8) Visualization")]
		public bool ShowTradeZones { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Entry Line", Order = 2, GroupName = "8) Visualization")]
		public bool ShowEntryLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Stop Loss Line", Order = 3, GroupName = "8) Visualization")]
		public bool ShowStopLossLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Take Profit Line", Order = 4, GroupName = "8) Visualization")]
		public bool ShowTakeProfitLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Risk Zone", Order = 5, GroupName = "8) Visualization")]
		public bool ShowRiskZone { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Reward Zone", Order = 6, GroupName = "8) Visualization")]
		public bool ShowRewardZone { get; set; }

		[NinjaScriptProperty]
		[Range(0, 100)]
		[Display(Name = "Zone Opacity (0-100)", Order = 7, GroupName = "8) Visualization")]
		public int ZoneOpacity { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max Drawn Trades (performance cap)", Order = 8, GroupName = "8) Visualization")]
		public int MaxDrawnTrades { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Draw On Historical Bars", Order = 9, GroupName = "8) Visualization")]
		public bool DrawOnHistoricalBars { get; set; }

		[XmlIgnore]
		[Display(Name = "Entry Line Colour", Order = 10, GroupName = "8) Visualization")]
		public Brush EntryLineBrush { get; set; }

		[Browsable(false)]
		public string EntryLineBrushSerialize
		{
			get { return Serialize.BrushToString(EntryLineBrush); }
			set { EntryLineBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Stop Loss Colour", Order = 11, GroupName = "8) Visualization")]
		public Brush StopLossBrush { get; set; }

		[Browsable(false)]
		public string StopLossBrushSerialize
		{
			get { return Serialize.BrushToString(StopLossBrush); }
			set { StopLossBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "Take Profit Colour", Order = 12, GroupName = "8) Visualization")]
		public Brush TakeProfitBrush { get; set; }

		[Browsable(false)]
		public string TakeProfitBrushSerialize
		{
			get { return Serialize.BrushToString(TakeProfitBrush); }
			set { TakeProfitBrush = Serialize.StringToBrush(value); }
		}

		#endregion

		#region Properties - plot accessors

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Ema1Plot { get { return Values[PlotEma1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> Ema2Plot { get { return Values[PlotEma2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> VwapPlot { get { return Values[PlotVwap]; } }

		#endregion
	}
}
