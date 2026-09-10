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
		private const string G_FIX  = "4c · Stop & Target";
		private const string G_TRL  = "4d · Trailing Stop";
		private const string G_RISK = "5 · Risk Sizing";
		private const string G_LIM  = "5b · Daily Limits";
		private const string G_NEWS = "6 · News Fair Price";
		private const string G_NEWX = "6b · News Surprise";
		private const string G_NEWC = "6c · News Continuation";
		private const string G_FLT  = "7 · Filters";
		private const string G_BT   = "9 · Backtest Fidelity";
		private const string G_DBG  = "10 · Debug";

		// ── 1 · SESSIONS ──────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[TypeConverter(typeof(TimeZoneOptionConverter))]
		[Display(Name = "Session timezone", Description = "The zone every session window below is TYPED in. Pick from the list or type any id TimeZoneRegistry understands: an IANA id (Asia/Kolkata, America/New_York), a Windows id (India Standard Time), or the shorthand IST.", GroupName = G_SES, Order = 0)]
		public string SessionTimeZoneId { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Times are SUMMER (auto-adjust for winter)", Description = "ON: type every window as its US-DST (summer) time and the strategy pins it to the equivalent US Eastern time, so in winter it shifts an hour later on your clock automatically and keeps tracking the same market hours. OFF: the times are taken literally in the zone above and never move.", GroupName = G_SES, Order = 1)]
		public bool AutoAdjustForUsDst { get; set; }

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
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Non-tradeable zone (% of Fair Price)", Description = "Half-width of the no-new-entry zone around Fair Price, as a PERCENTAGE of Fair Price. At FP 20,000 a value of 0.1 means 20 points either side. Must be smaller than Band 1 %.", GroupName = G_FP, Order = 2)]
		public double ZonePercent { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Band 1 — near edge (% of Fair Price)", Description = "Outer edge of the NEAR setup band, as a percentage of Fair Price. From the zone edge out to here, take profit targets the risk/reward multiple below. Must be larger than the zone % and smaller than Band 2 %.", GroupName = G_FP, Order = 3)]
		public double Band1Percent { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Band 2 — far edge (% of Fair Price)", Description = "Outer edge of the FAR setup band, as a percentage of Fair Price. From Band 1 out to here, take profit targets Fair Price itself. Beyond this an entry is too far and is skipped (TOO FAR). Must be larger than Band 1 %.", GroupName = G_FP, Order = 4)]
		public double Band2Percent { get; set; }

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
		[Display(Name = "Entry model", Description = "Which way a structure break is allowed to trade. Reversion: above the Fair Price zone shorts only, below it longs only — the trade heads back toward Fair Price, and both event types are eligible per the switches below. BOS continuation: above the zone LONGS on a bullish BOS only, below it SHORTS on a bearish BOS only. Breaks pointing back toward Fair Price are refused, and so is EVERY CHoCH — 'Take CHoCH entries' has no effect in this model. FAR-band setups take the R:R target instead of Fair Price, which now sits behind the trade.", GroupName = G_TM, Order = 0)]
		public FpEntryModel EntryModel { get; set; }

		[NinjaScriptProperty]
		[Range(0.1, double.MaxValue)]
		[Display(Name = "Band 1 (near) risk / reward ratio", Description = "Reward multiple used for NEAR-band setups (entry between the zone edge and Band 1). FAR-band setups target Fair Price instead and ignore this, except under the BOS-continuation entry model, where they use this multiple too. Ignored entirely when Fixed TP/SL is on.", GroupName = G_TM, Order = 1)]
		public double RewardRatio { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max trades per DAY", Description = "0 = unlimited. Day boundary is evaluated in the session timezone.", GroupName = G_TM, Order = 2)]
		public int MaxTradesPerDay { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Max trades per SESSION", Description = "0 = unlimited.", GroupName = G_TM, Order = 3)]
		public int MaxTradesPerSession { get; set; }

		[NinjaScriptProperty]
		[Range(0, int.MaxValue)]
		[Display(Name = "Setup validity (bars)", Description = "Bars from the CONFIRMATION bar of the active level. 0 = never expires.", GroupName = G_TM, Order = 4)]
		public int SetupValidityBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Only one open trade at a time", GroupName = G_TM, Order = 5)]
		public bool OnlyOneOpenTrade { get; set; }

		[NinjaScriptProperty]
		[Range(1, 20)]
		[Display(Name = "Max concurrent entries per direction", Description = "Used only when 'Only one open trade' is off — NinjaTrader needs an explicit ceiling where Pine used pyramiding.", GroupName = G_TM, Order = 6)]
		public int MaxConcurrentEntriesPerDirection { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Take CHoCH entries", GroupName = G_TM, Order = 7)]
		public bool TakeChochEntries { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Take BOS entries", GroupName = G_TM, Order = 8)]
		public bool TakeBosEntries { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "SL buffer (ticks)", Description = "Extra ticks beyond the displacement candle's extreme.", GroupName = G_TM, Order = 9)]
		public double StopBufferTicks { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Minimum stop distance (ticks)", Description = "0 = off. Displacement candles tighter than this are rejected with RISK.", GroupName = G_TM, Order = 10)]
		public double MinStopTicks { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Close trades at session end", GroupName = G_TM, Order = 11)]
		public bool CloseAtSessionEnd { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Min bars before first trade", Description = "Warm-up after the trading start point. Structure and Fair Price still run; only entries are blocked.", GroupName = G_TM, Order = 12)]
		public int MinBarsBeforeFirstTrade { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Same-candle TP/SL report", Description = "Reporting only. The real fill comes from the order fill resolution.", GroupName = G_TM, Order = 13)]
		public FpSameBarPriority SameBarPriority { get; set; }

		// ── 4c · FIXED TP/SL ──────────────────────────────────────────────────────
		// A master override for exits. When on, both the stop and the target are a
		// fixed number of POINTS from the entry, replacing the structure stop and the
		// distance-band targets for every trade. Entry gating is unchanged.
		[NinjaScriptProperty]
		[Display(Name = "Stop loss mode", Description = "Where the STOP comes from. FixedPoints: a fixed number of points from the entry, so every trade carries the same risk and therefore the same position size — this is what makes the $ risk target produce a constant contract count. StructureCandle: the displacement candle's own extreme plus the SL buffer, so risk varies candle by candle.", GroupName = G_FIX, Order = 0)]
		public FpStopMode StopMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Fixed stop loss (points)", Description = "Stop distance in points from the entry, used when Stop loss mode is FixedPoints. On MNQ 1 point = $2, so 25 points = $50 per contract.", GroupName = G_FIX, Order = 1)]
		public double FixedStopLossPoints { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Take profit mode", Description = "Where the TARGET comes from. FairPrice: the full reversion back to Fair Price, pulled in by the take-profit zone below — the mean-reversion default. Bands: the original distance-band system (NEAR takes the R:R multiple, FAR takes Fair Price). FixedPoints: a fixed number of points. RewardRatio: always the R:R multiple of the actual risk.", GroupName = G_FIX, Order = 2)]
		public FpTargetMode TargetMode { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Take profit ZONE (points from Fair Price)", Description = "Pulls the target this many points SHORT of Fair Price, onto the near edge of a zone around it. 0 = target Fair Price exactly. 5 = a short entered above Fair Price targets FairPrice + 5, a long entered below targets FairPrice - 5. Applies to every Fair Price target, in the FairPrice and Bands modes alike, and exists because the last few points into the mean are the least reliable.", GroupName = G_FIX, Order = 3)]
		public double TakeProfitZonePoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Fixed take profit (points)", Description = "Target distance in points from the entry, used when Take profit mode is FixedPoints.", GroupName = G_FIX, Order = 4)]
		public double FixedTakeProfitPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Minimum reward:risk", Description = "A trade whose target is closer than this multiple of its stop distance is refused with R:R TOO LOW. 1.0 = never risk more than the trade can make. This gate does real work with a Fair Price target: an entry that breaks structure just outside the no-trade zone has very little room left to Fair Price, and that is exactly the trade this rejects. 0 = off.", GroupName = G_FIX, Order = 5)]
		public double MinRewardRiskRatio { get; set; }

		// ── 4d · TRAILING & BREAKEVEN ───────────────────────────────────
		// Both only ever tighten the stop toward price, never loosen it.
		[NinjaScriptProperty]
		[Display(Name = "Move stop to breakeven at X R", Description = "Once the trade is X multiples of its own initial risk in profit, the stop jumps to the ENTRY FILL and stays there. 1 = breakeven at +1R, so a 25-point stop moves up once price is 25 points onside. 2 = breakeven at +2R. -1 (or any value at or below zero) turns it off. R is measured from the entry fill to the INITIAL stop, so later trailing cannot shrink R and pull the trigger forward. The move happens once and only ever tightens. Works with every stop mode, including a fixed-points stop, and is independent of the trailing stop mode below.", GroupName = G_TRL, Order = 0)]
		public double BreakEvenAtR { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trailing stop mode", Description = "Off: the stop stays at entry. R-step: at +1R the stop moves to breakeven, +2R to +1R, +3R to +2R, and so on (R = entry-to-initial-stop). Structure: the stop trails confirmed swings — down to each lower swing high for shorts, up to each higher swing low for longs, using the same pivot and SL-buffer settings as entries. Ignored under Fixed TP/SL.", GroupName = G_TRL, Order = 1)]
		public FpTrailMode TrailMode { get; set; }

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

		// ── 5b · DAILY LIMITS ─────────────────────────────────────────────────────
		// One realised-P&L budget per TRADING DAY, shared by every session window.
		// The day is the strategy's own trading day (see GetTradingDay), so a CME
		// evening open belongs to the following day exactly as the trade counters do.
		[NinjaScriptProperty]
		[Display(Name = "Use daily P&L limits", Description = "Master toggle for the daily loss and profit limits. Off = no P&L gating at all.", GroupName = G_LIM, Order = 0)]
		public bool UseDailyPnlLimits { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Daily loss limit ($)", Description = "Realised loss for the trading day that stops all further entries. 0 = no loss limit. Entered as a positive number.", GroupName = G_LIM, Order = 1)]
		public double DailyLossLimitUSD { get; set; }

		[NinjaScriptProperty]
		[Range(0, double.MaxValue)]
		[Display(Name = "Daily profit limit ($)", Description = "Realised profit for the trading day that stops all further entries. 0 = no profit limit.", GroupName = G_LIM, Order = 2)]
		public double DailyProfitLimitUSD { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Flatten open trade when a limit is hit", Description = "On: the open position is closed at market the moment a limit is breached. Off: no NEW entries, but the trade already running is left to reach its own stop or target.", GroupName = G_LIM, Order = 3)]
		public bool FlattenOnDailyLimit { get; set; }

		// ── 6 · NEWS FAIR PRICE ───────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Use news trading", Description = "MASTER on/off for every news-related entry and setup. Off = the calendar is never loaded and the strategy ignores news entirely — no news Fair Price, no pre-session news window, no surprise handling — regardless of the settings below. On = the individual news settings below take effect.", GroupName = G_NEWS, Order = 0)]
		public bool UseNewsTrading { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Use news Fair Price", Description = "Sub-toggle under 'Use news trading'. Off = behaviour is identical to the Pine version (session first-candle Fair Price only).", GroupName = G_NEWS, Order = 1)]
		public bool UseNewsFairPrice { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News file path", Description = "ForexFactory export, .json or .csv. Loaded once at startup.", GroupName = G_NEWS, Order = 2)]
		public string NewsFilePath { get; set; }

		[NinjaScriptProperty]
		[TypeConverter(typeof(TimeZoneOptionConverter))]
		[Display(Name = "News file timezone", Description = "Used only for rows WITHOUT an explicit UTC offset (i.e. every CSV row). ForexFactory's default profile is America/New_York.", GroupName = G_NEWS, Order = 3)]
		public string NewsFileTimeZoneId { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News CSV date format", Description = "Blank = auto-detect. Set a .NET format (e.g. MM-dd-yyyy) if auto-detection picks the wrong one.", GroupName = G_NEWS, Order = 4)]
		public string NewsCsvDateFormat { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, 24.0)]
		[Display(Name = "News lookback (hours)", Description = "X — how far before session open a release still qualifies.", GroupName = G_NEWS, Order = 5)]
		public double NewsLookbackHours { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News impact filter", GroupName = G_NEWS, Order = 6)]
		public FpNewsImpactFilter NewsImpactFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News currency filter", Description = "Comma separated, e.g. USD or USD,EUR. Blank = every currency.", GroupName = G_NEWS, Order = 7)]
		public string NewsCurrencyFilter { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Multiple event rule", Description = "Which release wins when several qualify in the window.", GroupName = G_NEWS, Order = 8)]
		public FpNewsMultipleEventRule NewsMultipleEventRule { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trading start on a news session", Description = "AfterNewsCandle: trading may begin before the session opens. AfterSessionOpen: normal session gating.", GroupName = G_NEWS, Order = 9)]
		public FpNewsTradingStart NewsTradingStart { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Trade pre-session news reversions from news time", Description = "When a qualifying release BEFORE the session prices in (actual == forecast), any move it caused is treated as unfair and the pre-news price is marked as Fair Price to revert toward. Turn this on to begin trading that reversion from the NEWS candle while the session window is still closed — e.g. news 18:00, session opens 19:00, trades from 18:00. Needs 'Use news Fair Price' on and a news lookback long enough to span the gap. Independent of 'Trading start on a news session'; applies only to the reversion (priced-in) case.", GroupName = G_NEWS, Order = 10)]
		public bool NewsReversionFromNewsTime { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Actual value source", Description = "Where the RELEASED figure comes from. The calendar FILE is always the schedule and the forecast — NinjaTrader exposes no queryable list of upcoming releases, only a live push as each one prints. Nt8CalendarThenFile: use NinjaTrader's live economic calendar, falling back to an Actual column in the file. FileOnly: ignore the live feed — the only setting that works in a BACKTEST, where no push ever arrives. Nt8CalendarOnly: live push only.", GroupName = G_NEWS, Order = 12)]
		public FpNewsActualSource NewsActualSource { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Handle news INSIDE the session", Description = "On: a qualifying release that lands inside an open session is acted on too. If it MATCHED its forecast the news candle's open replaces the session Fair Price for the rest of the session; if it MISSED, nothing happens — no mid-session continuation — and the session's own Fair Price stands. Off: only releases BEFORE the session open are considered.", GroupName = G_NEWS, Order = 13)]
		public bool NewsHandleInSession { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "News Fair Price expires at session open", Description = "On: a news Fair Price is valid only BEFORE the session opens (for the pre-session reversion window). At the session open it is discarded and the session's own first-candle Fair Price governs the rest of the session. Off: the news Fair Price governs the entire session (the original behaviour).", GroupName = G_NEWS, Order = 11)]
		public bool NewsFairPriceExpiresAtSessionOpen { get; set; }

		// ── 6b · NEWS SURPRISE ────────────────────────────────────────────────────
		// Decides WHICH price is fair after a release, from forecast vs actual.
		//   actual ~= forecast      -> priced in; the pre-news price stays fair.
		//   meaningful difference   -> the market may have repriced; wait for the
		//                              post-news consolidation to name a new one.
		// Needs a calendar file carrying forecast and actual columns. Without them
		// the unknown-value rule below decides, and nothing here changes behaviour.
		[NinjaScriptProperty]
		[Display(Name = "Use surprise classification", Description = "Master toggle for group 6b. OFF = every qualifying release uses the pre-news price as Fair Price, i.e. exactly the previous behaviour.", GroupName = G_NEWX, Order = 0)]
		public bool UseNewsSurprise { get; set; }

		[NinjaScriptProperty]
		[Range(0, 1000)]
		[Display(Name = "Expected tolerance (%)", Description = "|actual - forecast| at or below this share of the larger value counts as EXPECTED, so the pre-news price stays fair.", GroupName = G_NEWX, Order = 1)]
		public double NewsExpectedTolerancePercent { get; set; }

		[NinjaScriptProperty]
		[Range(0, 10000)]
		[Display(Name = "Large surprise threshold (%)", Description = "Above this the release counts as a LARGE surprise. Between the two thresholds it is a partial surprise.", GroupName = G_NEWX, Order = 2)]
		public double NewsUnexpectedThresholdPercent { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "When forecast or actual is missing", Description = "A forward-looking calendar has no actuals. TreatAsExpected keeps the old pre-news behaviour; SkipEvent makes the release override nothing.", GroupName = G_NEWX, Order = 3)]
		public FpNewsUnknownRule NewsUnknownRule { get; set; }

		[NinjaScriptProperty]
		[Range(1, 200)]
		[Display(Name = "Consolidation window (bars)", Description = "Consecutive reference candles that must fit inside the range below for the market to count as having accepted a new price.", GroupName = G_NEWX, Order = 4)]
		public int NewsConsolidationBars { get; set; }

		[NinjaScriptProperty]
		[Range(1, 100000)]
		[Display(Name = "Consolidation max range (ticks)", Description = "Total high-low span the window must fit inside. Wider = accepts a new Fair Price sooner and looser.", GroupName = G_NEWX, Order = 5)]
		public double NewsConsolidationRangeTicks { get; set; }

		[NinjaScriptProperty]
		[Range(1, 2000)]
		[Display(Name = "Consolidation search limit (bars)", Description = "Give up this many candles after the release. On giving up the news override is abandoned and the session runs without a news Fair Price.", GroupName = G_NEWX, Order = 6)]
		public int NewsConsolidationSearchBars { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow continuation on a large surprise", Description = "ASSUMPTION, defaulted OFF — you left this question unanswered. On: after a LARGE surprise the strategy trades WITH the displacement instead of fading it, inverting its normal side rule. Off: it waits for the new Fair Price and reverts to that.", GroupName = G_NEWX, Order = 7)]
		public bool NewsAllowContinuation { get; set; }

		// ── 6c · NEWS CONTINUATION ────────────────────────────────────────────────
		// Fires only for a PRE-SESSION release whose actual missed its forecast by more
		// than the surprise threshold. The market repriced for a real reason, so the
		// first trade goes WITH the news candle instead of fading it.
		[NinjaScriptProperty]
		[Display(Name = "Trade news continuation", Description = "On: a pre-session release that MISSES its forecast by more than the surprise threshold produces an immediate entry at the CLOSE of the news candle, in the direction of that candle. Off: a missed forecast instead waits for a post-news consolidation to name a new Fair Price, and no trade is taken from the news candle itself.", GroupName = G_NEWC, Order = 0)]
		public bool NewsTradeContinuation { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Continuation stop loss (points)", Description = "Stop distance for the news-candle continuation entry, in points from the entry.", GroupName = G_NEWC, Order = 1)]
		public double NewsContinuationStopPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Continuation take profit (points)", Description = "Target distance for the news-candle continuation entry. Equal to the stop = a 1:1 trade.", GroupName = G_NEWC, Order = 2)]
		public double NewsContinuationTargetPoints { get; set; }

		[NinjaScriptProperty]
		[Range(1, int.MaxValue)]
		[Display(Name = "Continuation trades per release", Description = "How many trades one continuation release may produce in total, counting the news-candle entry itself. 1 = the news-candle entry and nothing more. Raise it to allow the BOS follow-ons below.", GroupName = G_NEWC, Order = 3)]
		public int NewsContinuationMaxTrades { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Allow BOS follow-on entries", Description = "On: after the news-candle entry, further entries are taken on each BOS in the SAME direction as the news candle — a bullish BOS after a green news candle, a bearish BOS after a red one — until the trade budget above is spent or the session ends. These use the follow-on stop and reward below, NOT the news-candle figures. Off: the news candle produces exactly one trade.", GroupName = G_NEWC, Order = 4)]
		public bool NewsContinuationAllowBosFollowOn { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Follow-on stop loss (points)", Description = "Stop distance for a BOS follow-on entry, in points from the entry.", GroupName = G_NEWC, Order = 5)]
		public double NewsContinuationFollowOnStopPoints { get; set; }

		[NinjaScriptProperty]
		[Range(0.0, double.MaxValue)]
		[Display(Name = "Follow-on reward:risk", Description = "Target for a BOS follow-on entry, as a multiple of its stop distance.", GroupName = G_NEWC, Order = 6)]
		public double NewsContinuationFollowOnRewardRatio { get; set; }

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

		// ── 9 · BACKTEST FIDELITY ─────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Use tick precision", Description = "Resolves same-bar TP/SL from 1-tick data instead of an assumption. Slower, and needs tick history.", GroupName = G_BT, Order = 0)]
		public bool UseTickPrecision { get; set; }

		// ── 10 · DEBUG ────────────────────────────────────────────────────────────
		[NinjaScriptProperty]
		[Display(Name = "Verbose logging", Description = "Logs every entry, every skip and the first gate that blocked each displacement candle.", GroupName = G_DBG, Order = 0)]
		public bool VerboseLogging { get; set; }

		[NinjaScriptProperty]
		[Display(Name = "Print news event reports", Description = "Prints the forecast/actual/fair-price judgement for every release the strategy acts on.", GroupName = G_DBG, Order = 1)]
		public bool PrintNewsReports { get; set; }

		/// <summary>Applies every default. Called from State.SetDefaults.</summary>
		private void ApplyDefaults()
		{
			// Windows are typed in IST as their SUMMER (US-DST) times. The winter
			// equivalents are derived, never typed:
			//   1900-2030 IST summer = 0930-1100 New York = 2000-2130 IST winter
			SessionTimeZoneId         = "Asia/Kolkata";
			AutoAdjustForUsDst        = true;
			BarTimeZoneOverrideId     = string.Empty;
			Session1Enabled           = true;
			Session1Window            = "1900-2030";
			Session2Enabled           = false;
			Session2Window            = "2000-2100";
			Session3Enabled           = false;
			Session3Window            = "2330-0030";

			FairPriceSource           = FpSource.Close;
			FairPriceReferenceMinutes = 1;
			ZonePercent               = 0.1;
			Band1Percent              = 0.3;
			Band2Percent              = 0.6;

			PivotLeftBars             = 3;
			PivotRightBars            = 2;
			BreakConfirmation         = FpBreakConfirm.Close;
			ActiveLevelMode           = FpActiveLevelMode.LatestSwing;

			EntryModel                       = FpEntryModel.Reversion;
			RewardRatio                      = 1.5;
			MaxTradesPerDay                  = 0;   // unlimited - the $700 daily loss limit is what stops the day
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

			// Mean reversion, sized off a constant stop: a 25-point stop on MNQ is
			// $50 a contract, so the $100 risk target buys exactly 2 contracts every
			// time. The target is the reversion to Fair Price itself.
			StopMode              = FpStopMode.FixedPoints;
			FixedStopLossPoints   = 25.0;
			TargetMode            = FpTargetMode.FairPrice;
			TakeProfitZonePoints  = 0.0;
			FixedTakeProfitPoints = 40.0;
			MinRewardRiskRatio    = 1.0;

			BreakEvenAtR          = -1.0;   // off until the user asks for it
			TrailMode             = FpTrailMode.Off;

			RiskTargetUSD    = 100.0;
			RiskToleranceUSD = 20.0;
			RiskHardCapUSD   = 150.0;
			MaxContracts     = 10;

			UseNewsTrading        = true;
			UseNewsFairPrice      = true;
			NewsFilePath          = @"C:\Users\DELL\Desktop\Projects\Fair_Price_Reversion_P\config\ff_calendar_thisweek.csv";
			// The supplied ForexFactory export is written in UTC, not New York: its USD
			// PPI row reads 12:30pm, and PPI releases at 08:30 New York = 12:30 UTC. The
			// NFIB row at 10:00 = 06:00 New York confirms it. Set this wrong and every
			// news Fair Price lands on the wrong candle.
			NewsFileTimeZoneId    = "UTC";
			NewsCsvDateFormat     = "MM-dd-yyyy";
			// 1900 IST session open, 1800 IST release (1230 UTC) = exactly 1h apart.
			// The window is [open - lookback, open), so a 1.0 lookback would put the
			// release exactly on the boundary. 2.0 keeps it comfortably inside.
			NewsLookbackHours     = 2.0;
			NewsImpactFilter      = FpNewsImpactFilter.HighOnly;
			NewsCurrencyFilter    = "USD";
			NewsMultipleEventRule = FpNewsMultipleEventRule.First;
			NewsTradingStart      = FpNewsTradingStart.AfterSessionOpen;
			NewsReversionFromNewsTime         = false;
			// The news candle's open governs the WHOLE session; the session's own
			// first-candle Fair Price is discarded, as specified.
			NewsFairPriceExpiresAtSessionOpen = false;
			NewsActualSource                  = FpNewsActualSource.Nt8CalendarThenFile;
			NewsHandleInSession               = true;

			UseEmaFilter   = false;
			UseEma1        = true;
			EmaFastLength  = 9;
			UseEma2        = true;
			EmaSlowLength  = 21;
			UseVwapFilter  = false;

			UseDailyPnlLimits   = true;
			DailyLossLimitUSD   = 700.0;
			DailyProfitLimitUSD = 0.0;    // no profit cap was asked for
			FlattenOnDailyLimit = false;

			// ONE threshold, not two: at or below it the release was priced in and the
			// strategy reverts; above it the market repriced and the strategy continues.
			// Passing the same number for both collapses the middle "partial" band to
			// nothing, so there are exactly the two branches specified.
			UseNewsSurprise                = true;
			NewsExpectedTolerancePercent   = 4.0;
			NewsUnexpectedThresholdPercent = 4.0;
			// Without an actual there is nothing to judge, and assuming "priced in"
			// would trade a reversion off a level the release may have invalidated.
			NewsUnknownRule                = FpNewsUnknownRule.SkipEvent;
			NewsConsolidationBars          = 5;
			NewsConsolidationRangeTicks    = 40.0;
			NewsConsolidationSearchBars    = 60;
			NewsAllowContinuation          = true;

			NewsTradeContinuation               = true;
			NewsContinuationStopPoints          = 40.0;
			NewsContinuationTargetPoints        = 40.0;   // 1:1
			NewsContinuationMaxTrades           = 1;
			NewsContinuationAllowBosFollowOn    = false;
			NewsContinuationFollowOnStopPoints  = 25.0;
			NewsContinuationFollowOnRewardRatio = 1.5;

			UseTickPrecision = true;

			VerboseLogging   = true;
			PrintNewsReports = true;
		}
	}
}
