// =============================================================================
//  FAIR PRICE MEAN REVERSION · BOS / CHoCH / DISPLACEMENT  —  NinjaTrader 8
//  Part 1 of 2 — user inputs.
//
//  Groups mirror the Pine input groups so the two platforms can be configured
//  side by side, with the three NT8-only groups (Risk sizing, News, Backtest
//  fidelity) added where the port required them.
// =============================================================================
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript.Strategies.FPMR;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class FairPriceMeanReversion : Strategy
	{
		private const string G_SES  = "1 · Sessions";
		private const string G_FP   = "2 · Fair Price";
		private const string G_MS   = "3 · Market Structure";
		private const string G_TM   = "4 · Trade Management";
		private const string G_XTP  = "4b · Extended-move TP";
		private const string G_RISK = "5 · Risk Sizing";
		private const string G_NEWS = "6 · News Fair Price";
		private const string G_FLT  = "7 · Filters";
		private const string G_VIS  = "8 · Visualisation";
		private const string G_BT   = "9 · Backtest Fidelity";
		private const string G_DBG  = "10 · Debug";

		// ── 1 · SESSIONS ──────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[TypeConverter(typeof(TimeZoneOptionConverter))]
		[Display(Name = "Session timezone", Description = "Pick from the list or type any id TimeZoneRegistry understands: an IANA id (America/New_York, Asia/Kolkata), a Windows id (India Standard Time), or the shorthand IST. All session windows and the news file are evaluated here.", GroupName = G_SES, Order = 0)]
		public string SessionTimeZoneId { get; set; }

		[NinjaScriptProperty]
		[TypeConverter(typeof(TimeZoneOptionConverter))]
		[Display(Name = "Bar timezone override", Description = "Leave blank to assume bar timestamps are in the data series' trading-hours timezone. Set explicitly if your NinjaTrader time display is configured differently.", GroupName = G_SES, Order = 1)]
		public string BarTimeZoneOverrideId { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 1 enabled", GroupName = G_SES, Order = 2)]
		public bool Session1Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 1 window", Description = "HHMM-HHMM in the session timezone. End is exclusive, matching TradingView.", GroupName = G_SES, Order = 3)]
		public string Session1Window { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 2 enabled", GroupName = G_SES, Order = 4)]
		public bool Session2Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 2 window", GroupName = G_SES, Order = 5)]
		public string Session2Window { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 3 enabled", GroupName = G_SES, Order = 6)]
		public bool Session3Enabled { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Session 3 window", GroupName = G_SES, Order = 7)]
		public string Session3Window { get; set; }

		// ── 2 · FAIR PRICE ────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Fair Price source", Description = "Which price of the session's first reference candle becomes Fair Price. Spec default = Close.", GroupName = G_FP, Order = 0)]
		public FpSource FairPriceSource { get; set; }

		[NinjaScriptProperty]
		[Range(1, 60)]
		[Display(Name = "Reference candle minutes", Description = "Timeframe the opening candle is read from, independent of the chart timeframe. Spec = 1.", GroupName = G_FP, Order = 1)]
		public int FairPriceReferenceMinutes { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Zone distance unit", GroupName = G_FP, Order = 2)]
		public FpZoneUnit ZoneUnit { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Zone distance", Description = "Half-width of the no-new-entry zone around Fair Price.", GroupName = G_FP, Order = 3)]
		public double ZoneDistance { get; set; }

		[NinjaScriptProperty]
		[Range(0, 20)]
		[Display(Name = "Line forward extension (bars)", GroupName = G_FP, Order = 4)]
		public int FairPriceExtendBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 50)]
		[Display(Name = "Fair Price drawings kept", GroupName = G_FP, Order = 5)]
		public int FairPriceHistory { get; set; }

		// ── 3 · MARKET STRUCTURE ──────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Pivot left bars", GroupName = G_MS, Order = 0)]
		public int PivotLeftBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Pivot right bars", Description = "A swing is CONFIRMED this many bars after it printed. This delay is the price of not repainting.", GroupName = G_MS, Order = 1)]
		public int PivotRightBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Break confirmation", Description = "Close: the candle must close through the level. Wick: a wick through it is enough.", GroupName = G_MS, Order = 2)]
		public FpBreakConfirm BreakConfirmation { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Active level update mode", Description = "LatestSwing: the newest confirmed pivot always wins. RoleValidOnly: only a role-correct swing may replace the active level.", GroupName = G_MS, Order = 3)]
		public FpActiveLevelMode ActiveLevelMode { get; set; }

		// ── 4 · TRADE MANAGEMENT ──────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Risk / Reward ratio", GroupName = G_TM, Order = 0)]
		public double RewardRatio { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max trades per DAY", Description = "0 = unlimited. Day boundary is evaluated in the session timezone.", GroupName = G_TM, Order = 1)]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max trades per SESSION", Description = "0 = unlimited.", GroupName = G_TM, Order = 2)]
		public int MaxTradesPerSession { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Setup validity (bars)", Description = "Bars from the CONFIRMATION bar of the active level. 0 = never expires.", GroupName = G_TM, Order = 3)]
		public int SetupValidityBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Only one open trade at a time", GroupName = G_TM, Order = 4)]
		public bool OnlyOneOpenTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Max concurrent entries per direction", Description = "Used only when 'Only one open trade' is off — NinjaTrader needs an explicit ceiling where Pine used pyramiding.", GroupName = G_TM, Order = 5)]
		public int MaxConcurrentEntriesPerDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Take CHoCH entries", GroupName = G_TM, Order = 6)]
		public bool TakeChochEntries { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Take BOS entries", GroupName = G_TM, Order = 7)]
		public bool TakeBosEntries { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "SL buffer (ticks)", Description = "Extra ticks beyond the displacement candle's extreme.", GroupName = G_TM, Order = 8)]
		public double StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Minimum stop distance (ticks)", Description = "0 = off. Displacement candles tighter than this are rejected with RISK.", GroupName = G_TM, Order = 9)]
		public double MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Close trades at session end", GroupName = G_TM, Order = 10)]
		public bool CloseAtSessionEnd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Min bars before first trade", Description = "Warm-up after the trading start point. Structure and Fair Price still run; only entries are blocked.", GroupName = G_TM, Order = 11)]
		public int MinBarsBeforeFirstTrade { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Same-candle TP/SL report", Description = "Reporting only. The real fill comes from the order fill resolution.", GroupName = G_TM, Order = 12)]
		public FpSameBarPriority SameBarPriority { get; set; }

		// ── 4b · EXTENDED-MOVE TP OVERRIDE ────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Enable extended-move TP override", GroupName = G_XTP, Order = 0)]
		public bool UseExtendedTp { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Trigger: distance from Fair Price (%)", Description = "Percentage of Fair Price. At FP 29,300 a value of 0.5 means 146.5 points.", GroupName = G_XTP, Order = 1)]
		public double ExtendedTpTriggerPercent { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Y — trades to apply it to", Description = "Refills to Y on every bar price is still beyond the trigger; resets to 0 at session start.", GroupName = G_XTP, Order = 2)]
		public int ExtendedTpTradeCount { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "TP target while active", GroupName = G_XTP, Order = 3)]
		public FpExtendedTpMode ExtendedTpMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "TP offset from Fair Price", Description = "Same unit as the zone distance. Pulls the target back toward the entry.", GroupName = G_XTP, Order = 4)]
		public double ExtendedTpOffset { get; set; }

		// ── 5 · RISK SIZING ───────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Range(1.0, double.MaxValue)]
		[Display(Name = "Risk target ($)", Description = "The dollar risk each trade aims for.", GroupName = G_RISK, Order = 0)]
		public double RiskTargetUSD { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Risk tolerance ($)", Description = "Reporting band around the target. Does not change sizing.", GroupName = G_RISK, Order = 1)]
		public double RiskToleranceUSD { get; set; }

		[NinjaScriptProperty]
		[Range(1.0, double.MaxValue)]
		[Display(Name = "Risk hard cap ($)", Description = "Never exceeded. If one contract risks more than this the trade is skipped.", GroupName = G_RISK, Order = 2)]
		public double RiskHardCapUSD { get; set; }

		[NinjaScriptProperty]
		[Range(1, 1000)]
		[Display(Name = "Max contracts", GroupName = G_RISK, Order = 3)]
		public int MaxContracts { get; set; }

		// ── 6 · NEWS FAIR PRICE ───────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Use news Fair Price", Description = "Master toggle. Off = behaviour is identical to the Pine version.", GroupName = G_NEWS, Order = 0)]
		public bool UseNewsFairPrice { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News file path", Description = "ForexFactory export, .json or .csv. Loaded once at startup.", GroupName = G_NEWS, Order = 1)]
		public string NewsFilePath { get; set; }

		[NinjaScriptProperty]
		[TypeConverter(typeof(TimeZoneOptionConverter))]
		[Display(Name = "News file timezone", Description = "Used only for rows WITHOUT an explicit UTC offset (i.e. every CSV row). ForexFactory's default profile is America/New_York.", GroupName = G_NEWS, Order = 2)]
		public string NewsFileTimeZoneId { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News CSV date format", Description = "Blank = auto-detect. Set a .NET format (e.g. MM-dd-yyyy) if auto-detection picks the wrong one.", GroupName = G_NEWS, Order = 3)]
		public string NewsCsvDateFormat { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 24.0)]
		[Display(Name = "News lookback (hours)", Description = "X — how far before session open a release still qualifies.", GroupName = G_NEWS, Order = 4)]
		public double NewsLookbackHours { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News impact filter", GroupName = G_NEWS, Order = 5)]
		public FpNewsImpactFilter NewsImpactFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News currency filter", Description = "Comma separated, e.g. USD or USD,EUR. Blank = every currency.", GroupName = G_NEWS, Order = 6)]
		public string NewsCurrencyFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Multiple event rule", Description = "Which release wins when several qualify in the window.", GroupName = G_NEWS, Order = 7)]
		public FpNewsMultipleEventRule NewsMultipleEventRule { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trading start on a news session", Description = "AfterNewsCandle: trading may begin before the session opens. AfterSessionOpen: normal session gating.", GroupName = G_NEWS, Order = 8)]
		public FpNewsTradingStart NewsTradingStart { get; set; }

		// ── 7 · FILTERS ───────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Use EMA filter", Description = "Master switch. BOTH EMAs enabled = crossover rule (long needs EMA1 > EMA2). ONE enabled = price-vs-EMA rule (long needs close above it). Neither = no effect.", GroupName = G_FLT, Order = 0)]
		public bool UseEmaFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable EMA 1", Description = "Use EMA 1 in the filter. On its own it becomes a price-vs-EMA1 trend filter.", GroupName = G_FLT, Order = 1)]
		public bool UseEma1 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 1 length", GroupName = G_FLT, Order = 2)]
		public int EmaFastLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Enable EMA 2", Description = "Use EMA 2 in the filter. On its own it becomes a price-vs-EMA2 trend filter.", GroupName = G_FLT, Order = 3)]
		public bool UseEma2 { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "EMA 2 length", GroupName = G_FLT, Order = 4)]
		public int EmaSlowLength { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use VWAP filter", Description = "Session-anchored. Long needs close above, short needs close below. Off = zero effect.", GroupName = G_FLT, Order = 5)]
		public bool UseVwapFilter { get; set; }

		// ── 8 · VISUALISATION ─────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Show Fair Price line", GroupName = G_VIS, Order = 0)]
		public bool ShowFairPriceLine { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show Fair Price zone", Description = "The +/- Zone distance band around Fair Price — the region that decides above / below / inside.", GroupName = G_VIS, Order = 1)]
		public bool ShowFairPriceZone { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show SL / TP zones", Description = "Shaded stop-loss and take-profit zones on every trade, with a dashed level line and price label. Independent of \"Show full visuals\".", GroupName = G_VIS, Order = 2)]
		public bool ShowTradeZones { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Shade session windows", Description = "Highlights each active session window across the full chart height.", GroupName = G_VIS, Order = 3)]
		public bool ShowSessionShading { get; set; }

		[XmlIgnore]
		[Display(Name = "Session shading colour", GroupName = G_VIS, Order = 4)]
		public Brush SessionShadingBrush { get; set; }

		[Browsable(false)]
		public string SessionShadingBrushSerialize
		{
			get { return Serialize.BrushToString(SessionShadingBrush); }
			set { SessionShadingBrush = Serialize.StringToBrush(value); }
		}

		[NinjaScriptProperty]
		[Range(1, 100)]
		[Display(Name = "Session shading opacity", Description = "1-100. Keep it low so the shading never competes with the candles.", GroupName = G_VIS, Order = 5)]
		public int SessionShadingOpacity { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Session shadings kept", GroupName = G_VIS, Order = 6)]
		public int SessionShadingHistory { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Trade drawings kept", GroupName = G_VIS, Order = 7)]
		public int TradeDrawingHistory { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show full visuals (diagnostics)", Description = "Adds the structure diagnostic layer: HH/HL/LH/LL swing labels, CHoCH/BOS/DISP marks and the dotted broken-level line. Slow — leave off for real backtests.", GroupName = G_VIS, Order = 8)]
		public bool ShowFullVisuals { get; set; }

		// ── 9 · BACKTEST FIDELITY ─────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Use tick precision", Description = "Resolves same-bar TP/SL from 1-tick data instead of an assumption. Slower, and needs tick history.", GroupName = G_BT, Order = 0)]
		public bool UseTickPrecision { get; set; }

		// ── 10 · DEBUG ────────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Verbose logging", Description = "Logs every entry, every skip and the first gate that blocked each displacement candle.", GroupName = G_DBG, Order = 0)]
		public bool VerboseLogging { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Mark rejected setups on chart", GroupName = G_DBG, Order = 1)]
		public bool ShowRejectionMarks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Show state panel", GroupName = G_DBG, Order = 2)]
		public bool ShowStatePanel { get; set; }

		/// <summary>Applies every default. Called from State.SetDefaults.</summary>
		private void ApplyDefaults()
		{
			SessionTimeZoneId         = "America/New_York";
			BarTimeZoneOverrideId     = string.Empty;
			Session1Enabled           = true;
			Session1Window            = "0930-1000";
			Session2Enabled           = false;
			Session2Window            = "1030-1130";
			Session3Enabled           = false;
			Session3Window            = "1400-1500";

			FairPriceSource           = FpSource.Close;
			FairPriceReferenceMinutes = 1;
			ZoneUnit                  = FpZoneUnit.Points;
			ZoneDistance              = 20.0;
			FairPriceExtendBars       = 5;
			FairPriceHistory          = 5;

			PivotLeftBars             = 3;
			PivotRightBars            = 2;
			BreakConfirmation         = FpBreakConfirm.Close;
			ActiveLevelMode           = FpActiveLevelMode.LatestSwing;

			RewardRatio                      = 1.5;
			MaxTradesPerDay                  = 3;
			MaxTradesPerSession              = 0;
			SetupValidityBars                = 30;
			OnlyOneOpenTrade                 = true;
			MaxConcurrentEntriesPerDirection = 3;
			TakeChochEntries                 = true;
			TakeBosEntries                   = true;
			StopBufferTicks                  = 0.0;
			MinStopTicks                     = 0.0;
			CloseAtSessionEnd                = false;
			MinBarsBeforeFirstTrade          = 3;
			SameBarPriority                  = FpSameBarPriority.SlFirst;

			UseExtendedTp            = false;
			ExtendedTpTriggerPercent = 0.5;
			ExtendedTpTradeCount     = 1;
			ExtendedTpMode           = FpExtendedTpMode.FairPriceAlways;
			ExtendedTpOffset         = 0.0;

			RiskTargetUSD    = 100.0;
			RiskToleranceUSD = 20.0;
			RiskHardCapUSD   = 150.0;
			MaxContracts     = 10;

			UseNewsFairPrice      = false;
			NewsFilePath          = string.Empty;
			NewsFileTimeZoneId    = "America/New_York";
			NewsCsvDateFormat     = string.Empty;
			NewsLookbackHours     = 1.0;
			NewsImpactFilter      = FpNewsImpactFilter.HighOnly;
			NewsCurrencyFilter    = "USD";
			NewsMultipleEventRule = FpNewsMultipleEventRule.First;
			NewsTradingStart      = FpNewsTradingStart.AfterSessionOpen;

			UseEmaFilter   = false;
			UseEma1        = true;
			EmaFastLength  = 9;
			UseEma2        = true;
			EmaSlowLength  = 21;
			UseVwapFilter  = false;

			ShowFairPriceLine     = true;
			ShowFairPriceZone     = true;
			ShowFullVisuals       = false;
			ShowTradeZones        = true;
			ShowSessionShading    = true;
			SessionShadingBrush   = Brushes.LightBlue;
			SessionShadingOpacity = 12;
			SessionShadingHistory = 20;
			TradeDrawingHistory   = 30;

			UseTickPrecision = true;

			VerboseLogging     = false;
			ShowRejectionMarks = false;
			ShowStatePanel     = false;
		}
	}
}
