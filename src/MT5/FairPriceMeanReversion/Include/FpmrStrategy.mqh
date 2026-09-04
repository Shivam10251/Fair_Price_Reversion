//+------------------------------------------------------------------+
//| FPMR · Strategy engine                                           |
//|                                                                  |
//| Per-bar orchestration, ported from FairPriceMeanReversion.cs.     |
//|                                                                  |
//| THERE IS NO "FIRST CANDLE COLOUR" ENTRY IN THIS STRATEGY.         |
//| The first candle of a session establishes Fair Price and nothing  |
//| else.                                                             |
//|                                                                  |
//| One engine only:                                                  |
//|     Fair Price -> regime (above / below / inside zone)            |
//|     -> market-structure state machine (HH/HL/LH/LL)               |
//|     -> break of the single ACTIVE level = CHoCH or BOS            |
//|     -> the breaking candle IS the displacement candle             |
//|     -> SL = displacement candle extreme, TP = R:R (or Fair Price) |
//|     -> position size chosen so the dollar risk lands near target  |
//|                                                                  |
//| BAR MODEL. NinjaTrader ran Calculate.OnBarClose, so its Close[0]  |
//| was the bar that had just finished. MT5 fires OnTick continuously |
//| and indexes the FORMING bar as 0, so this engine detects a new    |
//| bar and then works entirely on shift 1 - the bar that just        |
//| closed. Entries therefore reach the market at the open of the new |
//| bar, which is the same fill point the NT8 build documented.       |
//+------------------------------------------------------------------+
#ifndef FPMR_STRATEGY_MQH
#define FPMR_STRATEGY_MQH

#include "FpmrEnums.mqh"
#include "FpmrTimeZone.mqh"
#include "DstAnchor.mqh"
#include "FpmrSymbol.mqh"
#include "SessionManager.mqh"
#include "FairPriceEngine.mqh"
#include "PivotDetector.mqh"
#include "StructureEngine.mqh"
#include "SetupBands.mqh"
#include "SessionVwap.mqh"
#include "RiskSizer.mqh"
#include "RejectionReporter.mqh"
#include "TradeManager.mqh"
#include "ChartPainter.mqh"

//--- Which volume series feeds the session VWAP -------------------------------
enum FpVolumeMode
  {
   FP_VOL_AUTO = 0, // Auto - real volume when the feed carries it, else tick volume
   FP_VOL_TICK = 1, // Tick volume
   FP_VOL_REAL = 2  // Real volume
  };

struct FpmrConfig
  {
   //--- 1 Sessions
   string            sessionTimeZoneId;
   bool              autoAdjustForUsDst;
   double            serverGmtOffsetHours;
   FpServerDst       serverDst;
   bool              session1Enabled;  string session1Window;
   bool              session2Enabled;  string session2Window;
   bool              session3Enabled;  string session3Window;

   //--- 2 Fair Price
   FpSource          fairPriceSource;
   int               fairPriceReferenceMinutes;
   double            zonePercent;
   double            band1Percent;
   double            band2Percent;

   //--- 3 Market structure
   int               pivotLeftBars;
   int               pivotRightBars;
   FpBreakConfirm    breakConfirmation;
   FpActiveLevelMode activeLevelMode;

   //--- 4 Trade management
   double            rewardRatio;
   int               maxTradesPerDay;
   int               maxTradesPerSession;
   int               setupValidityBars;
   bool              onlyOneOpenTrade;
   int               maxConcurrentEntriesPerDirection;
   bool              takeChochEntries;
   bool              takeBosEntries;
   double            stopBufferTicks;
   double            minStopTicks;
   bool              closeAtSessionEnd;
   int               minBarsBeforeFirstTrade;

   //--- 4c Fixed TP/SL for the NEAR band
   bool              useFixedNearBand;
   double            fixedStopLossPoints;
   double            fixedTakeProfitPoints;

   //--- 4d Trailing
   FpTrailMode       trailMode;

   //--- 5 Risk sizing (the manager owns the daily P&L limits; these size a trade)
   double            riskTargetUsd;
   double            riskToleranceUsd;
   double            riskHardCapUsd;
   double            maxLots;

   //--- 7 Filters
   bool              useEmaFilter;
   bool              useEma1;   int emaFastLength;
   bool              useEma2;   int emaSlowLength;
   bool              useVwapFilter;
   FpVolumeMode      volumeMode;

   //--- 11 Chart display
   FpPaintConfig     paint;

   //--- 10 Debug
   bool              verboseLogging;
  };

class CFpmrStrategy
  {
private:
   FpmrConfig        m_cfg;
   FpSymbolMeta      m_sym;
   CFpServerClock    m_clock;
   FpTzId            m_sessionTz;
   FpTzId            m_typedTz;

   CSessionManager   m_sessions;
   CStructureEngine  m_structure;
   CFairPriceEngine  m_fair;
   CSessionVwap      m_vwap;
   CChartPainter     m_painter;
   CTradeManager    *m_trades;

   int               m_emaFastHandle;
   int               m_emaSlowHandle;

   ENUM_TIMEFRAMES   m_refTf;
   int               m_refSeconds;
   int               m_primarySeconds;

   bool              m_configError;
   string            m_configErrorText;

   //--- Running state
   datetime          m_lastBarTime;
   datetime          m_fpCursorTime;
   double            m_prevRefSource;
   int               m_prevEffSession;
   int               m_tradingStartBar;
   bool              m_hadFairPrev;
   int               m_tradesDay;
   int               m_tradesSession;
   string            m_lastRejectText;

   double            m_lastSwingHigh;
   double            m_lastSwingLow;

   //--- The first session of a run reports where it lands on the CHART clock.
   //    Chart objects are anchored in server time, so this is both a sanity
   //    check on the conversion and the answer to "when do I look?".
   bool              m_saidChartWindow;

   //--- Cached per-bar values
   double            m_fairPrice, m_zoneUpper, m_zoneLower;
   bool              m_hasFair, m_insideZone;
   int               m_posState;
   bool              m_inTradingWindow;
   bool              m_warmupDone;
   int               m_effSession;
   int               m_barIndex;

   void              Fail(const string message)
     {
      m_configError=true;
      m_configErrorText=message;
      Print("FPMR CONFIGURATION ERROR: "+message+" The EA will not place orders.");
     }

   //--- Timeframe constant for a whole number of minutes.
   ENUM_TIMEFRAMES   MinutesToTimeframe(const int minutes)
     {
      switch(minutes)
        {
         case 1:  return(PERIOD_M1);
         case 2:  return(PERIOD_M2);
         case 3:  return(PERIOD_M3);
         case 4:  return(PERIOD_M4);
         case 5:  return(PERIOD_M5);
         case 6:  return(PERIOD_M6);
         case 10: return(PERIOD_M10);
         case 12: return(PERIOD_M12);
         case 15: return(PERIOD_M15);
         case 20: return(PERIOD_M20);
         case 30: return(PERIOD_M30);
         default: return(PERIOD_CURRENT);
        }
     }

public:
                     CFpmrStrategy(void)
     : m_trades(NULL), m_emaFastHandle(INVALID_HANDLE), m_emaSlowHandle(INVALID_HANDLE),
       m_configError(false), m_configErrorText(""), m_lastBarTime(0), m_fpCursorTime(0),
       m_prevRefSource(FPMR_NA), m_prevEffSession(0), m_tradingStartBar(-1),
       m_hadFairPrev(false), m_tradesDay(0), m_tradesSession(0), m_lastRejectText("-"),
       m_lastSwingHigh(FPMR_NA), m_lastSwingLow(FPMR_NA), m_barIndex(-1),
       m_saidChartWindow(false) {}

                    ~CFpmrStrategy(void)
     {
      if(m_emaFastHandle!=INVALID_HANDLE) IndicatorRelease(m_emaFastHandle);
      if(m_emaSlowHandle!=INVALID_HANDLE) IndicatorRelease(m_emaSlowHandle);
     }

   void              OnDeinitEvent(void) { m_painter.OnDeinit(); }

   bool              ConfigError(void)     const { return(m_configError); }
   string            ConfigErrorText(void) const { return(m_configErrorText); }

   //+---------------------------------------------------------------+
   //| Init                                                           |
   //+---------------------------------------------------------------+
   bool              Init(const FpmrConfig &cfg,const FpSymbolMeta &sym,CTradeManager *trades)
     {
      m_cfg    = cfg;
      m_sym    = sym;
      m_trades = trades;

      m_configError=false;
      m_configErrorText="";

      // The three setup percentages must be strictly ordered so the zone and the
      // two bands never overlap. Fixed TP/SL, when on, needs positive distances.
      if(cfg.band1Percent<=cfg.zonePercent)
         Fail(StringFormat("Band 1 %% (%.4f) must be greater than the non-tradeable zone %% (%.4f).",
                           cfg.band1Percent,cfg.zonePercent));
      else if(cfg.band2Percent>0.0 && cfg.band2Percent<=cfg.band1Percent)
         Fail(StringFormat("Band 2 %% (%.4f) must be greater than Band 1 %% (%.4f), or zero to disable "
                           "the outer limit entirely.",cfg.band2Percent,cfg.band1Percent));

      if(cfg.useFixedNearBand)
        {
         if(cfg.fixedStopLossPoints<=0.0)
            Fail("Fixed stop loss (points) must be greater than zero when the near band uses fixed TP/SL.");
         else if(cfg.fixedTakeProfitPoints<=0.0)
            Fail("Fixed take profit (points) must be greater than zero when the near band uses fixed TP/SL.");
        }

      m_typedTz=FpTzResolve(cfg.sessionTimeZoneId);
      if(m_typedTz==FP_TZ_INVALID)
         Fail("Session timezone '"+cfg.sessionTimeZoneId+"' is not one this build knows. "
              "See FpmrTimeZone.mqh for the supported ids.");

      m_clock.Configure(cfg.serverGmtOffsetHours,cfg.serverDst);

      m_refTf=MinutesToTimeframe(cfg.fairPriceReferenceMinutes);
      if(m_refTf==PERIOD_CURRENT)
         Fail(StringFormat("Reference candle minutes (%d) is not a timeframe MT5 offers. "
                           "Use 1, 2, 3, 4, 5, 6, 10, 12, 15, 20 or 30.",cfg.fairPriceReferenceMinutes));

      m_refSeconds     = cfg.fairPriceReferenceMinutes*60;
      m_primarySeconds = PeriodSeconds(_Period);

      // Rebase the typed SUMMER windows onto the DST anchor zone, where the same
      // market hours have ONE fixed clock time all year.
      FpTzId evaluationTz=m_typedTz;
      int    summerShift =0;

      string w1=cfg.session1Window, w2=cfg.session2Window, w3=cfg.session3Window;

      if(cfg.autoAdjustForUsDst && m_typedTz!=FP_TZ_INVALID && m_typedTz!=FPMR_ANCHOR_TZ)
        {
         summerShift  = FpAnchorSummerShift(m_typedTz,FPMR_ANCHOR_TZ);
         evaluationTz = FPMR_ANCHOR_TZ;

         w1=FpShiftWindow(w1,-summerShift);
         w2=FpShiftWindow(w2,-summerShift);
         w3=FpShiftWindow(w3,-summerShift);
        }

      m_sessionTz=evaluationTz;

      m_sessions.Init(m_sessionTz,
                      cfg.session1Enabled,w1,
                      cfg.session2Enabled,w2,
                      cfg.session3Enabled,w3);

      PrintSessionPlan(summerShift);

      string windowError=m_sessions.FirstConfigError();
      if(windowError!="")
         Fail(windowError);

      // The manager dates realised money in the SAME zone the sessions are
      // evaluated in, which after the DST rebase is the anchor, not what was typed.
      if(m_trades!=NULL)
         m_trades.SetDayClock(cfg.serverGmtOffsetHours,cfg.serverDst,m_sessionTz);

      m_painter.Init(cfg.paint,sym.digits);

      m_structure.Init(cfg.activeLevelMode);
      m_fair.Init(true);   // news Fair Price expiry - inert until phase 6
      m_vwap.Reset();

      // Only build what the filter will actually read - an unused EMA is pure
      // per-bar cost, and an invalid handle is what tells the gate it is disabled.
      m_emaFastHandle=(cfg.useEmaFilter && cfg.useEma1)
                      ? iMA(_Symbol,_Period,cfg.emaFastLength,0,MODE_EMA,PRICE_CLOSE)
                      : INVALID_HANDLE;
      m_emaSlowHandle=(cfg.useEmaFilter && cfg.useEma2)
                      ? iMA(_Symbol,_Period,cfg.emaSlowLength,0,MODE_EMA,PRICE_CLOSE)
                      : INVALID_HANDLE;

      if(cfg.useEmaFilter && cfg.useEma1 && m_emaFastHandle==INVALID_HANDLE)
         Fail("EMA 1 handle could not be created.");
      if(cfg.useEmaFilter && cfg.useEma2 && m_emaSlowHandle==INVALID_HANDLE)
         Fail("EMA 2 handle could not be created.");

      m_lastBarTime     = 0;
      m_fpCursorTime    = 0;
      m_prevRefSource   = FPMR_NA;
      m_prevEffSession  = 0;
      m_tradingStartBar = -1;
      m_hadFairPrev     = false;
      m_tradesDay       = 0;
      m_tradesSession   = 0;
      m_lastSwingHigh   = FPMR_NA;
      m_lastSwingLow    = FPMR_NA;
      m_lastRejectText  = "-";
      m_barIndex        = -1;
      m_saidChartWindow = false;

      Print(StringFormat("FPMR loaded: %s | session tz %s (windows evaluated there) | typed in %s | "
                         "server clock %s | reference candle %d min | chart %s%s",
                         FpSymbolDescribe(m_sym),
                         FpTzName(m_sessionTz), FpTzName(m_typedTz),
                         m_clock.Describe(),
                         cfg.fairPriceReferenceMinutes,
                         EnumToString(_Period),
                         m_configError ? " | CONFIG ERROR: "+m_configErrorText : ""));

      return(!m_configError);
     }

   //--- Prints what each window resolves to in summer AND winter, so the shift is
   //    visible before a bar is processed rather than inferred from fills.
   void              PrintSessionPlan(const int summerShift)
     {
      if(!m_cfg.autoAdjustForUsDst || summerShift==0)
        {
         Print("FPMR sessions: DST auto-adjust OFF - windows are taken literally in "
               +m_cfg.sessionTimeZoneId+" and never move.");
         return;
        }

      int extra=FpAnchorWinterExtra(m_typedTz,FPMR_ANCHOR_TZ);

      Print(StringFormat("FPMR sessions: typed in %s as SUMMER values, pinned to %s so winter follows automatically.\n"
                         "  Session 1 %s: %s summer / %s winter  (= %s %s)\n"
                         "  Session 2 %s: %s summer / %s winter  (= %s %s)\n"
                         "  Session 3 %s: %s summer / %s winter  (= %s %s)",
                         m_cfg.sessionTimeZoneId, FPMR_ANCHOR_TZ_ID,
                         m_cfg.session1Enabled ? "ON " : "off", m_cfg.session1Window,
                         FpShiftWindow(m_cfg.session1Window,extra),
                         FpShiftWindow(m_cfg.session1Window,-summerShift), FPMR_ANCHOR_TZ_ID,
                         m_cfg.session2Enabled ? "ON " : "off", m_cfg.session2Window,
                         FpShiftWindow(m_cfg.session2Window,extra),
                         FpShiftWindow(m_cfg.session2Window,-summerShift), FPMR_ANCHOR_TZ_ID,
                         m_cfg.session3Enabled ? "ON " : "off", m_cfg.session3Window,
                         FpShiftWindow(m_cfg.session3Window,extra),
                         FpShiftWindow(m_cfg.session3Window,-summerShift), FPMR_ANCHOR_TZ_ID));
     }

   //+---------------------------------------------------------------+
   //| Per tick - all work happens on the first tick of a new bar     |
   //+---------------------------------------------------------------+
   void              OnTickEvent(void)
     {
      if(m_configError || m_trades==NULL)
         return;

      datetime barTime=iTime(_Symbol,_Period,0);
      if(barTime==0 || barTime==m_lastBarTime)
         return;

      m_lastBarTime=barTime;

      OnClosedBar();
     }

   void              OnClosedBar(void);

private:
   //--- The bar being processed: shift 1, the one that just closed. Held as
   //    members so the per-bar helpers read the same snapshot rather than
   //    re-fetching series data that could move under them.
   double            m_barOpen, m_barHigh, m_barLow, m_barClose;
   double            m_barVolume;
   datetime          m_barOpenTime;

   //--- AS-SERIES windows over the closed bars, index 0 = the bar above.
   double            m_highs[];
   double            m_lows[];

   bool              LoadBarWindow(const int needed);
   void              PumpFairPriceSeries(const datetime primaryCloseTime);
   void              ProcessReferenceBar(const MqlRates &r);
   void              ResolveTradingWindow(const SessionEvaluation &ev);
   void              DetectAndFeedPivots(const int barsAvailable);
   void              EvaluateEntry(const StructureBreak &brk);
   bool              SideAllowed(const int dir);
   bool              EmaOk(const bool isLong);
   bool              VwapOkLong(void);
   bool              VwapOkShort(void);
   void              PaintChart(const SessionEvaluation &ev,const bool isSessionStart);
   void              RecordRejection(const int dir,const FpReject reason,const SizingResult &sizing);
   void              SubmitEntry(const int dir,const FpBreakEvent evt,const double entry,
                                 const double stop,const double risk,const SizingResult &sizing,
                                 const FpSetupBand band,const bool useFixed);

public:
   string            LastRejectText(void) const { return(m_lastRejectText); }
   int               TradesDay(void)      const { return(m_tradesDay); }
   int               TradesSession(void)  const { return(m_tradesSession); }
   double            CurrentFairPrice(void) const { return(m_fairPrice); }
   bool              HasFairPrice(void)   const { return(m_hasFair); }
   int               EffectiveSession(void) const { return(m_effSession); }
  };

//--- The per-bar bodies live in their own file purely to keep both under the
//    500-line ceiling this repository works to.
#include "FpmrStrategyImpl.mqh"
#include "FpmrStrategyFilters.mqh"

#endif // FPMR_STRATEGY_MQH
//+------------------------------------------------------------------+
