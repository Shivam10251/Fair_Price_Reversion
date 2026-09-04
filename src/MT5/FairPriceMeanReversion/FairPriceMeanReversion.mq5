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
input string InpSession1Window     = "1900-1930";    // Session 1 window (HHMM-HHMM, end exclusive)
input bool   InpSession2Enabled    = false;          // Session 2 enabled
input string InpSession2Window     = "2000-2100";    // Session 2 window
input bool   InpSession3Enabled    = false;          // Session 3 enabled
input string InpSession3Window     = "2330-0030";    // Session 3 window

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
input FpSource InpFairPriceSource           = FP_SRC_CLOSE; // Fair Price source (first reference candle)
input int      InpFairPriceReferenceMinutes = 1;            // Reference candle minutes (1,2,3,4,5,6,10,12,15,20,30)
input double   InpZonePercent               = 0.1;          // Non-tradeable zone (% of Fair Price)
input double   InpBand1Percent              = 0.3;          // Band 1 - near edge (% of Fair Price)
input double   InpBand2Percent              = 0.6;          // Band 2 - far edge (% of Fair Price)

//--- 3 · MARKET STRUCTURE -----------------------------------------------------
input group "3 · Market Structure"
input int               InpPivotLeftBars     = 3;                       // Pivot left bars
input int               InpPivotRightBars    = 2;                       // Pivot right bars (confirmation delay)
input FpBreakConfirm    InpBreakConfirmation = FP_BREAK_CLOSE;          // Break confirmation
input FpActiveLevelMode InpActiveLevelMode   = FP_LEVEL_LATEST_SWING;   // Active level update mode

//--- 4 · TRADE MANAGEMENT -----------------------------------------------------
input group "4 · Trade Management"
input double InpRewardRatio                      = 1.5;   // Band 1 (near) risk / reward ratio
input int    InpMaxTradesPerDay                  = 3;     // Max trades per DAY (0 = unlimited)
input int    InpMaxTradesPerSession              = 0;     // Max trades per SESSION (0 = unlimited)
input int    InpSetupValidityBars                = 30;    // Setup validity (bars, 0 = never expires)
input bool   InpOnlyOneOpenTrade                 = false; // Only one open trade at a time
input int    InpMaxConcurrentEntriesPerDirection = 3;     // Max concurrent entries per direction (needs a HEDGING account)
input bool   InpTakeChochEntries                 = true;  // Take CHoCH entries
input bool   InpTakeBosEntries                   = true;  // Take BOS entries
input double InpStopBufferTicks                  = 0.0;   // SL buffer (ticks beyond the displacement extreme)
input double InpMinStopTicks                     = 0.0;   // Minimum stop distance (ticks, 0 = off)
input bool   InpCloseAtSessionEnd                = false; // Close trades at session end
input int    InpMinBarsBeforeFirstTrade          = 3;     // Min bars before first trade (warm-up)
input FpSameBarPriority InpSameBarPriority       = FP_SAMEBAR_SL_FIRST; // Same-candle TP/SL report (reporting only)

//--- 4c · FIXED TP/SL ---------------------------------------------------------
// A master override for exits. When on, both the stop and the target are a fixed
// number of POINTS from the entry, replacing the structure stop and the
// distance-band targets for every trade. Entry gating is unchanged.
input group "4c · Fixed TP/SL"
input bool   InpUseFixedTpSl          = false; // Use fixed TP/SL (points) - master override
input double InpFixedStopLossPoints   = 20.0;  // Fixed stop loss (points)
input double InpFixedTakeProfitPoints = 40.0;  // Fixed take profit (points)

//--- 4d · TRAILING STOP -------------------------------------------------------
input group "4d · Trailing Stop"
input FpTrailMode InpTrailMode = FP_TRAIL_OFF; // Trailing stop mode (ignored under Fixed TP/SL)

//--- 5 · RISK SIZING ----------------------------------------------------------
input group "5 · Risk Sizing"
input double InpRiskTargetUSD    = 100.0; // Risk target (account currency)
input double InpRiskToleranceUSD = 20.0;  // Risk tolerance (reporting band only)
input double InpRiskHardCapUSD   = 150.0; // Risk hard cap (never exceeded)
input double InpMaxLots          = 10.0;  // Max lots (0 = only the broker's own ceiling)

//--- 5b · DAILY LIMITS --------------------------------------------------------
// One realised-P&L budget per TRADING DAY, shared by every session window. The
// day is evaluated in the session time zone, so an evening open belongs to the
// following day exactly as the trade counters do.
input group "5b · Daily Limits"
input bool   InpUseDailyPnlLimits   = false; // Use daily P&L limits
input double InpDailyLossLimitUSD   = 400.0; // Daily loss limit (positive number, 0 = off)
input double InpDailyProfitLimitUSD = 600.0; // Daily profit limit (0 = off)
input bool   InpFlattenOnDailyLimit = false; // Flatten open trades when a limit is hit

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
input ulong InpSlippagePoints = 10;      // Maximum slippage (points)

//--- 10 · DEBUG ---------------------------------------------------------------
input group "10 · Debug"
input bool InpVerboseLogging = false; // Verbose logging (every entry, skip and trail move)

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
   tcfg.slippagePoints      = InpSlippagePoints;
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

   cfg.fairPriceSource           = InpFairPriceSource;
   cfg.fairPriceReferenceMinutes = InpFairPriceReferenceMinutes;
   cfg.zonePercent               = InpZonePercent;
   cfg.band1Percent              = InpBand1Percent;
   cfg.band2Percent              = InpBand2Percent;

   cfg.pivotLeftBars     = InpPivotLeftBars;
   cfg.pivotRightBars    = InpPivotRightBars;
   cfg.breakConfirmation = InpBreakConfirmation;
   cfg.activeLevelMode   = InpActiveLevelMode;

   cfg.rewardRatio                      = InpRewardRatio;
   cfg.maxTradesPerDay                  = InpMaxTradesPerDay;
   cfg.maxTradesPerSession              = InpMaxTradesPerSession;
   cfg.setupValidityBars                = InpSetupValidityBars;
   cfg.onlyOneOpenTrade                 = onlyOneOpenTrade;
   cfg.maxConcurrentEntriesPerDirection = MathMax(1,InpMaxConcurrentEntriesPerDirection);
   cfg.takeChochEntries                 = InpTakeChochEntries;
   cfg.takeBosEntries                   = InpTakeBosEntries;
   cfg.stopBufferTicks                  = InpStopBufferTicks;
   cfg.minStopTicks                     = InpMinStopTicks;
   cfg.closeAtSessionEnd                = InpCloseAtSessionEnd;
   cfg.minBarsBeforeFirstTrade          = InpMinBarsBeforeFirstTrade;

   cfg.useFixedTpSl          = InpUseFixedTpSl;
   cfg.fixedStopLossPoints   = InpFixedStopLossPoints;
   cfg.fixedTakeProfitPoints = InpFixedTakeProfitPoints;

   cfg.trailMode = InpTrailMode;

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
   if(g_ready)
      Print(g_trades.RunSummary());
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
