//+------------------------------------------------------------------+
//|  FAIR PRICE MEAN REVERSION · BOS / CHoCH / DISPLACEMENT           |
//|  MetaTrader 5 Expert Advisor                                      |
//|                                                                   |
//|  A port of the NinjaTrader 8 strategy of the same name, which is   |
//|  itself a port of the Pine original. The input groups below mirror |
//|  the NT8 groups so one configuration can be read across all three  |
//|  platforms, with the MT5-only settings (broker server clock,       |
//|  execution) added where the platform required them.               |
//|                                                                   |
//|  THERE IS NO "FIRST CANDLE COLOUR" ENTRY IN THIS STRATEGY.         |
//|  The first candle of a session establishes Fair Price and nothing  |
//|  else.                                                             |
//|                                                                   |
//|  See src/MT5/FairPriceMeanReversion/Include for the engine, and    |
//|  docs/DESIGN_DECISIONS.md for the platform deviations.             |
//+------------------------------------------------------------------+
#property copyright "Fair Price Mean Reversion"
#property version   "1.00"
#property description "Fair Price mean reversion using market structure. CHoCH/BOS displacement entries,"
#property description "dollar-targeted position sizing and percentage distance bands."

#include "Include/FpmrStrategy.mqh"

//--- 1 · SESSIONS -------------------------------------------------------------
input group "1 · Sessions"
input string InpSessionTimeZoneId  = "Asia/Kolkata"; // Session timezone (every window below is TYPED in this zone)
input bool   InpAutoAdjustForUsDst = true;           // Times are SUMMER (auto-adjust for winter)
input bool   InpSession1Enabled    = true;           // Session 1 enabled
input string InpSession1Window     = "1900-2100";    // Session 1 window (HHMM-HHMM, end exclusive)
input bool   InpSession2Enabled    = true;          // Session 2 enabled
input string InpSession2Window     = "0530-0730";    // Session 2 window
input bool   InpSession3Enabled    = true;           // Session 3 enabled
input string InpSession3Window     = "0030-0230";    // Session 3 window
// Which session's opening reference each window is measured against. "Own" is
// the original rule - the session's own first candle. Inheriting means the
// session is anchored to another session's Fair Price from the SAME trading
// day; if that session never set one, the inheriting session does not trade.
input FpFpInherit InpSession1FpFrom = FP_FP_OWN; // Session 1 Fair Price source
input FpFpInherit InpSession2FpFrom = FP_FP_OWN; // Session 2 Fair Price source
input FpFpInherit InpSession3FpFrom = FP_FP_S1;  // Session 3 Fair Price source

//--- 1b · BROKER SERVER CLOCK (MT5 only) --------------------------------------
// MT5 stamps bars in broker server time, which is an unnamed zone with its own
// seasonal shift. A backtest converts bars from years ago, so the offset cannot
// be read from TimeGMT() at startup - it has to be stated. Check your broker's
// specification; the startup log prints what this resolves to right now so a
// wrong setting is visible before a single order is placed.
input group "1b · Broker server clock"
input double      InpServerGmtOffsetHours = 2.0;          // Server STANDARD (winter) GMT offset, hours
input FpServerDst InpServerDst            = FP_SRVDST_US; // Which DST calendar the server clock follows

//--- 2 · FAIR PRICE -----------------------------------------------------------
input group "2 · Fair Price"
input FpSource InpFairPriceSource           = FP_SRC_OPEN;  // Fair Price source (first reference candle)
input int      InpFairPriceReferenceMinutes = 1;            // Reference candle minutes (1,2,3,4,5,6,10,12,15,20,30)
input double   InpZonePercent               = 0.3;          // Non-tradeable zone (% of Fair Price) - nothing closer is traded
input double   InpBand1Percent              = 0.5;          // Band 1 - outer edge of the NEAR band (fixed SL/TP region)
input double   InpBand2Percent              = 0.8;          // Band 2 - outer edge of the FAR band (0 = no outer limit)
// What happens to a setup that lands PAST Band 1 (the FAR band):
//   Fair Price target - take it and revert the whole way to Fair Price (original)
//   Exit like a NEAR setup - take it, but use the fixed SL/TP or the R:R multiple
//   Do not trade      - refuse everything past Band 1, reported as TOO FAR
input FpFarBandMode InpFarBandMode = FP_FAR_FAIR_PRICE; // FAR band behaviour (past Band 1)

//--- 3 · MARKET STRUCTURE -----------------------------------------------------
input group "3 · Market Structure"
input int               InpPivotLeftBars     = 1;                       // Pivot left bars
input int               InpPivotRightBars    = 1;                       // Pivot right bars (confirmation delay)
input FpBreakConfirm    InpBreakConfirmation = FP_BREAK_CLOSE;          // Break confirmation
input FpActiveLevelMode InpActiveLevelMode   = FP_LEVEL_LATEST_SWING;   // Active level update mode

//--- 4 · TRADE MANAGEMENT -----------------------------------------------------
input group "4 · Trade Management"
// Entry model - which way a structure break is allowed to trade.
//   Reversion       : above the zone shorts only, below it longs only. Both
//                     event types are eligible, per the two switches below.
//   BOS continuation: above the zone LONGS on a bullish BOS only, below it
//                     SHORTS on a bearish BOS only. Breaks pointing back toward
//                     Fair Price are refused, and so is EVERY CHoCH - the
//                     "Take CHoCH entries" switch has no effect in this model.
//                     A FAR-band setup exits like a NEAR one, since a target at
//                     Fair Price sits behind a trade running away from it.
input FpEntryModel InpEntryModel                 = FP_ENTRY_REVERSION; // Entry model (direction of the trade)
input double InpRewardRatio                      = 1.5;   // Band 1 (near) risk / reward ratio
input int    InpMaxTradesPerDay                  = 30;     // Max trades per DAY (0 = unlimited)
input int    InpMaxTradesPerSession              = 10;     // Max trades per SESSION (0 = unlimited)
input int    InpSetupValidityBars                = 30;    // Setup validity (bars, 0 = never expires)
input bool   InpOnlyOneOpenTrade                 = false; // Only one open trade at a time
input int    InpMaxConcurrentEntriesPerDirection = 3;     // Max concurrent entries per direction (needs a HEDGING account)
input bool   InpTakeChochEntries                 = true;  // Take CHoCH entries
input bool   InpTakeBosEntries                   = true;  // Take BOS entries
input double InpStopBufferPoints                 = 0.0;   // SL buffer (index points beyond the displacement extreme)
input double InpMinStopPoints                    = 0.0;   // Minimum stop distance (index points, 0 = off)
input bool   InpCloseAtSessionEnd                = false; // Close trades at session end
input int    InpMinBarsBeforeFirstTrade          = 3;     // Min bars before first trade (warm-up)
input FpSameBarPriority InpSameBarPriority       = FP_SAMEBAR_SL_FIRST; // Same-candle TP/SL report (reporting only)

//  UNITS. Every distance in this EA is an INDEX POINT - one full point of the
//  Nasdaq index, which is exactly one MNQ point. Typing 20 gives a 20 point
//  stop on both, e.g. 25313.30 -> 25293.30. Only the MONEY differs: a NAS100
//  lot is $10 per point, an MNQ contract is $2, so the same 20 point stop is
//  $200 per lot here and $40 per contract there. These are NOT MT5 "points"
//  (0.01 on this symbol) and not ticks.

//--- 4c · FIXED TP/SL FOR THE NEAR BAND ---------------------------------------
// Applies to NEAR-band setups only - entries between the non-tradeable zone edge
// and Band 1. Those take a fixed stop and a fixed target in price points instead
// of the displacement-candle stop and the Band 1 risk/reward multiple.
// FAR-band setups (past Band 1) are untouched: they keep the displacement
// candle's stop and target Fair Price itself.
input group "4c · Fixed TP/SL (near band)"
input bool   InpUseFixedNearBand      = true;  // Near band uses fixed SL/TP instead of the R:R multiple
input double InpFixedStopLossPoints   = 30.0;  // Fixed stop loss (index points = MNQ points)
input double InpFixedTakeProfitPoints = 48.0;  // Fixed take profit (index points = MNQ points)

//--- 4d · TRAILING STOP -------------------------------------------------------
input group "4d · Trailing Stop"
input FpTrailMode InpTrailMode = FP_TRAIL_OFF; // Trailing stop mode (ignored under Fixed TP/SL)

//--- 4e · REVERSE SIGNALS -----------------------------------------------------
// Takes the opposite side of every setup. The setup is still read, gated and
// classified exactly as before - only the order that reaches the broker changes.
//
//   Mirror : same stop and target DISTANCES on the other side of the entry.
//            Risk and R multiple are unchanged, but because the bracket keeps
//            its near-stop/far-target shape, a reversed trade can lose the very
//            setup the original lost - so this is NOT a P&L inverse.
//   Swap   : the stop and target LEVELS are exchanged. The reversed trade then
//            loses exactly when the original would have won, which IS the true
//            P&L inverse. The risk distance becomes the old target distance, so
//            the position is re-sized on it and the lot count drops.
input group "4e · Reverse signals"
input FpReverseMode InpReverseMode = FP_REVERSE_OFF; // Reverse mode (see below)

//--- 5 · RISK SIZING ----------------------------------------------------------
input group "5 · Risk Sizing"
input double InpRiskTargetUSD    = 500.0;  // Risk target (account currency)
input double InpRiskToleranceUSD = 100.0; // Risk tolerance (reporting band only)
input double InpRiskHardCapUSD   = 1000.0;// Risk hard cap (never exceeded)
input double InpMaxLots          = 10.0;  // Max lots (0 = only the broker's own ceiling)

//--- 5b · DAILY LIMITS --------------------------------------------------------
// One realised-P&L budget per TRADING DAY, shared by every session window. The
// day is evaluated in the session time zone, so an evening open belongs to the
// following day exactly as the trade counters do.
input group "5b · Daily Limits"
input bool   InpUseDailyPnlLimits   = true;  // Use daily P&L limits
input double InpDailyLossLimitUSD   = 2500.0; // Daily loss limit (positive number, 0 = off)
input double InpDailyProfitLimitUSD = 600.0; // Daily profit limit (0 = off)
input bool   InpFlattenOnDailyLimit = true;  // Flatten open trades when a limit is hit

//--- 7 · FILTERS --------------------------------------------------------------
input group "7 · Filters"
input bool         InpUseEmaFilter  = false;       // Use EMA filter (master switch)
input bool         InpUseEma1       = true;        // Enable EMA 1
input int          InpEmaFastLength = 9;           // EMA 1 length
input bool         InpUseEma2       = true;        // Enable EMA 2
input int          InpEmaSlowLength = 21;          // EMA 2 length
input bool         InpUseVwapFilter = false;       // Use VWAP filter (session anchored)
input FpVolumeMode InpVolumeMode    = FP_VOL_AUTO; // VWAP volume source

//--- 8 · EXECUTION (MT5 only) -------------------------------------------------
input group "8 · Execution"
input ulong InpMagicNumber    = 8451207; // Magic number (identifies this EA's positions)
input double InpSlippagePoints = 1.0;    // Maximum slippage (index points)

//--- 11 · CHART DISPLAY -------------------------------------------------------
// Drawn behind the candles, which is how MT5 gives a filled rectangle the
// "transparent" look - chart objects have no alpha channel, so a box in front
// would hide the price action it is describing.
// A NON-VISUAL backtest draws nothing: nobody can see it and the objects would
// only slow the run down. Use the tester's Visual mode, or a live chart, to see
// any of this.
input group "11 · Chart display"
input bool  InpShowChartObjects  = true;         // Draw session boxes, Fair Price and trade zones
input color InpSessionBoxColor   = clrGainsboro; // Session box (open to close)
input color InpFairPriceColor    = clrDarkOrange;// Fair Price line
input color InpStopZoneColor     = clrLightPink; // Stop loss zone
input color InpTargetZoneColor   = clrPaleGreen; // Take profit zone
input bool  InpKeepObjectsOnExit = false;        // Keep the drawings after the EA is removed

//--- 10 · DEBUG ---------------------------------------------------------------
input group "10 · Debug"
input bool InpVerboseLogging = false; // Verbose logging (every entry, skip and trail move)
// WARNING: verbose logging prints a line on almost every bar. In the Strategy
// Tester's VISUAL mode the journal is rendered live, and that volume of output
// crashes the terminal under the Mac (Wine) build within seconds. Leave this OFF
// for visual runs; it is safe for ordinary non-visual backtests.

//--- Runtime ------------------------------------------------------------------
CFpmrStrategy  g_strategy;
CTradeManager  g_trades;
FpSymbolMeta   g_symbol;
bool           g_ready = false;

//+------------------------------------------------------------------+
//| Init                                                             |
//+------------------------------------------------------------------+
int OnInit()
  {
   g_ready=false;

   if(!FpLoadSymbolMeta(_Symbol,g_symbol))
     {
      Print("FPMR CONFIGURATION ERROR: "+g_symbol.error);
      return(INIT_FAILED);
     }

   // Concurrent entries need a hedging account, where each entry is its own
   // position with its own stop and target. On a netting account MT5 merges
   // everything into one net position, so per-trade brackets cannot exist and
   // the second entry would overwrite the first one's stop. Forcing single-trade
   // behaviour is the only safe reading of the settings there.
   bool onlyOneOpenTrade=InpOnlyOneOpenTrade;

   if(!g_symbol.hedgingAccount && !onlyOneOpenTrade)
     {
      onlyOneOpenTrade=true;
      Print("FPMR: this is a NETTING account, so concurrent entries are not possible - MT5 would merge them "
            "into one net position and the per-trade stops and targets would overwrite each other. "
            "'Only one open trade at a time' has been FORCED ON for this run. Use a hedging account to "
            "trade several entries at once.");
     }

   FpTradeConfig tcfg;
   tcfg.magic               = InpMagicNumber;
   // The broker counts slippage in ITS points (0.01 on NAS100), so the index
   // points typed above are converted once here rather than leaving the panel
   // with one input on a different scale from its neighbours.
   double symPoint=(g_symbol.point>0.0 ? g_symbol.point : 0.01);
   tcfg.slippagePoints      = (ulong)MathMax(1.0,MathRound(InpSlippagePoints/symPoint));
   tcfg.riskTargetUsd       = InpRiskTargetUSD;
   tcfg.riskToleranceUsd    = InpRiskToleranceUSD;
   tcfg.riskHardCapUsd      = InpRiskHardCapUSD;
   tcfg.useDailyPnlLimits   = InpUseDailyPnlLimits;
   tcfg.dailyLossLimitUsd   = InpDailyLossLimitUSD;
   tcfg.dailyProfitLimitUsd = InpDailyProfitLimitUSD;
   tcfg.flattenOnDailyLimit = InpFlattenOnDailyLimit;
   tcfg.sameBarPriority     = InpSameBarPriority;
   tcfg.verboseLogging      = InpVerboseLogging;

   g_trades.Init(g_symbol,tcfg);

   FpmrConfig cfg;

   cfg.sessionTimeZoneId    = InpSessionTimeZoneId;
   cfg.autoAdjustForUsDst   = InpAutoAdjustForUsDst;
   cfg.serverGmtOffsetHours = InpServerGmtOffsetHours;
   cfg.serverDst            = InpServerDst;
   cfg.session1Enabled      = InpSession1Enabled;   cfg.session1Window = InpSession1Window;
   cfg.session2Enabled      = InpSession2Enabled;   cfg.session2Window = InpSession2Window;
   cfg.session3Enabled      = InpSession3Enabled;   cfg.session3Window = InpSession3Window;
   cfg.session1FpFrom       = InpSession1FpFrom;
   cfg.session2FpFrom       = InpSession2FpFrom;
   cfg.session3FpFrom       = InpSession3FpFrom;

   cfg.fairPriceSource           = InpFairPriceSource;
   cfg.fairPriceReferenceMinutes = InpFairPriceReferenceMinutes;
   cfg.zonePercent               = InpZonePercent;
   cfg.band1Percent              = InpBand1Percent;
   cfg.band2Percent              = InpBand2Percent;
   cfg.farBandMode               = InpFarBandMode;

   cfg.pivotLeftBars     = InpPivotLeftBars;
   cfg.pivotRightBars    = InpPivotRightBars;
   cfg.breakConfirmation = InpBreakConfirmation;
   cfg.activeLevelMode   = InpActiveLevelMode;

   cfg.entryModel                       = InpEntryModel;
   cfg.rewardRatio                      = InpRewardRatio;
   cfg.maxTradesPerDay                  = InpMaxTradesPerDay;
   cfg.maxTradesPerSession              = InpMaxTradesPerSession;
   cfg.setupValidityBars                = InpSetupValidityBars;
   cfg.onlyOneOpenTrade                 = onlyOneOpenTrade;
   cfg.maxConcurrentEntriesPerDirection = MathMax(1,InpMaxConcurrentEntriesPerDirection);
   cfg.takeChochEntries                 = InpTakeChochEntries;
   cfg.takeBosEntries                   = InpTakeBosEntries;
   cfg.stopBufferPoints                 = InpStopBufferPoints;
   cfg.minStopPoints                    = InpMinStopPoints;
   cfg.closeAtSessionEnd                = InpCloseAtSessionEnd;
   cfg.minBarsBeforeFirstTrade          = InpMinBarsBeforeFirstTrade;

   cfg.useFixedNearBand      = InpUseFixedNearBand;
   cfg.fixedStopLossPoints   = InpFixedStopLossPoints;
   cfg.fixedTakeProfitPoints = InpFixedTakeProfitPoints;

   cfg.trailMode      = InpTrailMode;
   cfg.reverseMode    = InpReverseMode;

   cfg.riskTargetUsd    = InpRiskTargetUSD;
   cfg.riskToleranceUsd = InpRiskToleranceUSD;
   cfg.riskHardCapUsd   = InpRiskHardCapUSD;
   cfg.maxLots          = InpMaxLots;

   cfg.useEmaFilter  = InpUseEmaFilter;
   cfg.useEma1       = InpUseEma1;
   cfg.emaFastLength = InpEmaFastLength;
   cfg.useEma2       = InpUseEma2;
   cfg.emaSlowLength = InpEmaSlowLength;
   cfg.useVwapFilter = InpUseVwapFilter;
   cfg.volumeMode    = InpVolumeMode;

   cfg.paint.enabled    = InpShowChartObjects;
   cfg.paint.sessionBox = InpSessionBoxColor;
   cfg.paint.fairPrice  = InpFairPriceColor;
   cfg.paint.stopZone   = InpStopZoneColor;
   cfg.paint.targetZone = InpTargetZoneColor;
   cfg.paint.keepOnExit = InpKeepObjectsOnExit;

   cfg.verboseLogging = InpVerboseLogging;

   if(!g_strategy.Init(cfg,g_symbol,GetPointer(g_trades)))
      return(INIT_FAILED);   // Init already logged which setting is wrong

   g_trades.ReconcileExisting(Bars(_Symbol,_Period)-2);

   if(InpUseVwapFilter && InpVolumeMode!=FP_VOL_REAL
      && (ENUM_SYMBOL_CALC_MODE)SymbolInfoInteger(_Symbol,SYMBOL_TRADE_CALC_MODE)==SYMBOL_CALC_MODE_FOREX)
      Print("FPMR: the VWAP filter is on for a forex symbol, whose feed publishes tick counts rather than "
            "traded volume. The value is therefore a TICK-weighted average price, not a true VWAP.");

   g_ready=true;
   return(INIT_SUCCEEDED);
  }

//+------------------------------------------------------------------+
//| Deinit                                                           |
//+------------------------------------------------------------------+
void OnDeinit(const int reason)
  {
   if(!g_ready)
      return;

   Print(g_trades.RunSummary());
   g_strategy.OnDeinitEvent();
  }

//+------------------------------------------------------------------+
//| Tick                                                             |
//+------------------------------------------------------------------+
void OnTick()
  {
   if(!g_ready)
      return;

   g_strategy.OnTickEvent();
  }
//+------------------------------------------------------------------+
