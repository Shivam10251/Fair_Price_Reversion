//+------------------------------------------------------------------+
//| FPMR · Trading · TradeManager                                    |
//|                                                                  |
//| ORDER APPROACH (the biggest deliberate deviation from NT8)        |
//| NinjaTrader owned the OCO bracket: SetStopLoss / SetProfitTarget  |
//| registered against a per-trade signal name, with the platform     |
//| linking exits back to their entry. MT5 has no managed bracket and |
//| no signal names. Instead every entry is a market order whose stop |
//| and target are attached to the POSITION and held on the broker's  |
//| server, which gives the same two properties that made the NT8     |
//| choice right: the bracket survives a disconnect, and concurrent   |
//| trades never share exit orders.                                   |
//|                                                                  |
//| CONCURRENT ENTRIES need a HEDGING account, where each entry is    |
//| its own position with its own ticket, stop and target. On a       |
//| netting account MT5 merges everything into one net position, so   |
//| per-trade stops cannot exist; the EA detects this at init and     |
//| forces single-trade behaviour rather than placing orders whose    |
//| brackets would overwrite each other.                              |
//|                                                                  |
//| MONEY. Realised P&L is read back from the deal history rather     |
//| than recomputed from prices, so swap, commission and any partial  |
//| broker close are all included exactly as the account sees them.   |
//+------------------------------------------------------------------+
#ifndef FPMR_TRADE_MANAGER_MQH
#define FPMR_TRADE_MANAGER_MQH

#include <Trade\Trade.mqh>

#include "FpmrEnums.mqh"
#include "FpmrTimeZone.mqh"
#include "FpmrSymbol.mqh"
#include "TradeRecord.mqh"

struct FpTradeConfig
  {
   ulong             magic;
   ulong             slippagePoints;
   double            riskTargetUsd;
   double            riskToleranceUsd;
   double            riskHardCapUsd;
   bool              useDailyPnlLimits;
   double            dailyLossLimitUsd;
   double            dailyProfitLimitUsd;
   bool              flattenOnDailyLimit;
   FpSameBarPriority sameBarPriority;
   bool              verboseLogging;
  };

//--- Everything the trailing rules need from the current bar.
struct FpTrailContext
  {
   double   high;
   double   low;
   double   close;
   double   lastSwingHigh;   // FPMR_NA when none confirmed this session
   double   lastSwingLow;
   double   stopBuffer;      // index points, already in price units
  };

class CTradeManager
  {
private:
   CTrade            m_trade;
   FpSymbolMeta      m_sym;
   FpTradeConfig     m_cfg;

   TradeRecord       m_records[];   // every trade this run, open and closed
   int               m_tradeSeq;
   int               m_ambiguousCount;
   bool              m_unreconciled;

   //--- Daily realised P&L budget -----------------------------------------
   double            m_realisedPnlDay;
   bool              m_dayLossHit;
   bool              m_dayProfitHit;
   bool              m_flattenRequested;
   datetime          m_pnlDay;

   string            m_lastExitText;

   //--- The manager books money against the day the exit actually happened, not
   //    the day the bar loop noticed it. A stop that filled at 23:58 and is only
   //    seen on the first bar of the next day belongs to the day it filled on,
   //    which is what the NT8 build got for free from its execution callbacks.
   CFpServerClock    m_dayClock;
   FpTzId            m_dayTz;

   void              Say(const string text) const { Print(text); }

   int               IndexOfTicket(const ulong ticket) const
     {
      for(int i=0;i<ArraySize(m_records);i++)
         if(m_records[i].ticket==ticket)
            return(i);
      return(-1);
     }

public:
                     CTradeManager(void)
     : m_tradeSeq(0), m_ambiguousCount(0), m_unreconciled(false),
       m_realisedPnlDay(0.0), m_dayLossHit(false), m_dayProfitHit(false),
       m_flattenRequested(false), m_pnlDay(0), m_lastExitText("-"), m_dayTz(FP_TZ_UTC) {}

   //+---------------------------------------------------------------+
   //| Setup                                                          |
   //+---------------------------------------------------------------+
   void              Init(const FpSymbolMeta &sym,const FpTradeConfig &cfg)
     {
      m_sym=sym;
      m_cfg=cfg;

      m_trade.SetExpertMagicNumber(cfg.magic);
      m_trade.SetDeviationInPoints(cfg.slippagePoints);
      m_trade.SetTypeFillingBySymbol(sym.name);
      m_trade.SetAsyncMode(false);
      m_trade.LogLevel(cfg.verboseLogging ? LOG_LEVEL_ERRORS : LOG_LEVEL_NO);

      ArrayFree(m_records);
      m_tradeSeq         = 0;
      m_ambiguousCount   = 0;
      m_unreconciled     = false;
      m_realisedPnlDay   = 0.0;
      m_dayLossHit       = false;
      m_dayProfitHit     = false;
      m_flattenRequested = false;
      m_pnlDay           = 0;
      m_lastExitText     = "-";
     }

   //--- Supplied by the strategy once the session zone has been resolved, because
   //    the DST anchor may have replaced the zone the user typed.
   void              SetDayClock(const double serverGmtOffsetHours,const FpServerDst serverDst,
                                 const FpTzId sessionTz)
     {
      m_dayClock.Configure(serverGmtOffsetHours,serverDst);
      m_dayTz=sessionTz;
     }

   //--- Trading day a server-time instant belongs to, in the session zone.
   datetime          TradingDayOf(const datetime serverTime) const
     {
      return(FpDateOnly(m_dayClock.ToZone(serverTime,m_dayTz)));
     }

   //+---------------------------------------------------------------+
   //| Counters and state                                             |
   //+---------------------------------------------------------------+
   int               TradeSequence(void)  const { return(m_tradeSeq); }
   int               AmbiguousCount(void) const { return(m_ambiguousCount); }
   bool              Unreconciled(void)   const { return(m_unreconciled); }
   double            RealisedPnlDay(void) const { return(m_realisedPnlDay); }
   bool              DayLossHit(void)     const { return(m_dayLossHit); }
   bool              DayProfitHit(void)   const { return(m_dayProfitHit); }
   string            LastExitText(void)   const { return(m_lastExitText); }

   //--- Read-only access for the chart painter. Trades stay owned here; the
   //    painter only ever reads a copy.
   int               RecordCount(void) const { return(ArraySize(m_records)); }

   void              GetRecord(const int i,TradeRecord &out) const
     {
      if(i>=0 && i<ArraySize(m_records))
         out=m_records[i];
     }

   int               OpenCount(void) const
     {
      int n=0;
      for(int i=0;i<ArraySize(m_records);i++)
         if(m_records[i].isFilled && !m_records[i].isClosed)
            n++;
      return(n);
     }

   int               OpenCountDirection(const int dir) const
     {
      int n=0;
      for(int i=0;i<ArraySize(m_records);i++)
         if(m_records[i].isFilled && !m_records[i].isClosed && m_records[i].direction==dir)
            n++;
      return(n);
     }

   //--- Dollar loss still at risk across every open trade if each one stops out.
   double            OpenRiskUsd(void) const
     {
      double risk=0.0;
      for(int i=0;i<ArraySize(m_records);i++)
         if(!m_records[i].isClosed)
            risk+=TradeOpenRiskUsd(m_records[i],m_sym.moneyPerPricePerLot);
      return(risk);
     }

   //+---------------------------------------------------------------+
   //| Daily realised P&L budget                                      |
   //|                                                                |
   //| The limits are a bound on the day's NET result, not a switch    |
   //| that trips after the fact. Two things are therefore needed:     |
   //|                                                                |
   //|  1. A PREVENTIVE gate. Blocking new entries only once the limit |
   //|     is already breached lets the day overshoot by the whole     |
   //|     risk of the trade that broke it: sitting at -350 against a  |
   //|     400 limit, one more trade risking 100 ends the day at -450. |
   //|     LossHeadroomFor refuses any trade whose worst case, added   |
   //|     to what is already realised AND to what the open trades     |
   //|     still have at risk, would push the day past the limit.      |
   //|                                                                |
   //|  2. A REACTIVE latch, kept as a backstop. A stop can fill worse |
   //|     than its level on a gap, so the projection is a bound on    |
   //|     intent, not a guarantee.                                    |
   //+---------------------------------------------------------------+
   bool              LossHeadroomFor(const double candidateRiskUsd) const
     {
      if(!m_cfg.useDailyPnlLimits || m_cfg.dailyLossLimitUsd<=0.0)
         return(true);

      double worstCase=m_realisedPnlDay-OpenRiskUsd()-MathMax(0.0,candidateRiskUsd);
      return(worstCase>=-m_cfg.dailyLossLimitUsd);
     }

   //--- Moves the accumulator onto a new trading day. Keyed on the day value
   //    itself so the bar loop and a close detected on the first bar of a new
   //    day cannot disagree about which day the money belongs to.
   void              RollPnlDay(const datetime tradingDay)
     {
      if(tradingDay==m_pnlDay)
         return;

      m_pnlDay           = tradingDay;
      m_realisedPnlDay   = 0.0;
      m_dayLossHit       = false;
      m_dayProfitHit     = false;
      m_flattenRequested = false;
     }

   //--- Adds money to the day and latches the limits if it crossed one.
   void              BookMoney(const double amount,const datetime tradingDay)
     {
      RollPnlDay(tradingDay);

      m_realisedPnlDay+=amount;

      if(!m_cfg.useDailyPnlLimits)
         return;

      bool wasStopped=(m_dayLossHit || m_dayProfitHit);

      if(m_cfg.dailyLossLimitUsd>0.0 && m_realisedPnlDay<=-m_cfg.dailyLossLimitUsd)
         m_dayLossHit=true;

      if(m_cfg.dailyProfitLimitUsd>0.0 && m_realisedPnlDay>=m_cfg.dailyProfitLimitUsd)
         m_dayProfitHit=true;

      if(!wasStopped && (m_dayLossHit || m_dayProfitHit))
        {
         Say(StringFormat("%s  DAILY %s LIMIT HIT - net %.2f realised for %s. No further entries today.%s",
                          TimeToString(TimeCurrent(),TIME_DATE|TIME_SECONDS),
                          m_dayLossHit ? "LOSS" : "PROFIT",
                          m_realisedPnlDay,
                          TimeToString(m_pnlDay,TIME_DATE),
                          m_cfg.flattenOnDailyLimit
                            ? " Flattening the open positions."
                            : " The open trades are left to reach their own stop or target."));

         if(m_cfg.flattenOnDailyLimit)
            m_flattenRequested=true;
        }
     }

   //+---------------------------------------------------------------+
   //| Restart reconciliation                                         |
   //|                                                                |
   //| Do not assume the tester's or a previous session's record       |
   //| matches the live account. A position on this symbol carrying    |
   //| our magic number is adopted so its stop and target keep being   |
   //| managed; anything else on the symbol blocks new entries until   |
   //| the account is flat, exactly as the NT8 build did.              |
   //+---------------------------------------------------------------+
   void              ReconcileExisting(const int currentBarIndex);

   //--- Re-checks the block once the foreign positions are gone.
   void              RefreshReconciliation(void);

   //+---------------------------------------------------------------+
   //| Entry                                                          |
   //+---------------------------------------------------------------+

   //--- Broker minimum distance for a stop or target, in price units.
   double            MinStopDistance(void) const
     {
      return(m_sym.stopsLevelPoints*m_sym.point);
     }

   //--- True when the bracket sits far enough from the market to be accepted.
   bool              BracketIsPlaceable(const int dir,const double price,
                                        const double stop,const double target) const
     {
      double minDist=MinStopDistance();
      if(minDist<=0.0)
         return(true);

      if(dir>0)
         return(price-stop>=minDist && target-price>=minDist);

      return(stop-price>=minDist && price-target>=minDist);
     }

   //+---------------------------------------------------------------+
   //| Submits one entry with its stop and target attached.           |
   //| Returns false and fills `error` when the broker refused it.    |
   //+---------------------------------------------------------------+
   bool              Submit(const int dir,const FpBreakEvent event,
                            const double signalPrice,const double stop,const double target,
                            const bool targetIsFairPrice,const FpTrailMode trail,
                            const SizingResult &sizing,
                            const int barIndex,const datetime barTime,const int sessionIndex,
                            string &error);

   //+---------------------------------------------------------------+
   //| Closure detection and booking                                  |
   //|                                                                |
   //| Called once per bar. A position that is no longer on the books  |
   //| was closed by its own stop or target, by the flatten path, or   |
   //| by hand; the deal history says which, and says what it was      |
   //| worth. Money already booked at entry is subtracted so the       |
   //| commission is never counted twice.                              |
   //+---------------------------------------------------------------+
   void              SyncClosures(const int currentBarIndex,const datetime tradingDay);

   void              CloseRecordFromHistory(const int i,const int currentBarIndex,const datetime tradingDay);

   //--- MT5 states the closing reason on the deal, so the same-candle preference
   //    only ever changes the REPORTED reason when the broker did not say.
   string            ClassifyExit(const ENUM_DEAL_REASON dealReason,const int i);

   //+---------------------------------------------------------------+
   //| Same-candle TP/SL detection - reporting only. The fill itself   |
   //| comes from the broker's own stop and target orders.             |
   //+---------------------------------------------------------------+
   void              ScanAmbiguity(const double high,const double low);

   //+---------------------------------------------------------------+
   //| Trailing stops                                                 |
   //|                                                                |
   //| Runs once per bar for every open band trade whose trail mode is |
   //| not Off. The stop is only ever moved in the tightening          |
   //| direction - a level that would loosen it is ignored - so the    |
   //| trail can never increase risk. Fixed TP/SL trades carry         |
   //| Trail = Off and are never touched here.                         |
   //+---------------------------------------------------------------+
   void              UpdateTrailingStops(const FpTrailContext &ctx);

   //--- Whole-R ratchet. R is the entry-to-initial-stop distance. Once price has
   //    reached +nR the stop sits at +(n-1)R from the entry - breakeven at n = 1.
   //    Uses the best favourable excursion so far, so a pullback never loosens it.
   double            RStepTrailStop(const int i,const FpTrailContext &ctx);

   //--- Trails the latest CONFIRMED swing plus the SL buffer: the swing high for
   //    a short, the swing low for a long. The tighten-only rule in the caller is
   //    what turns "the latest swing" into "each lower high / higher low". A level
   //    that would sit on the wrong side of the current close is refused.
   double            StructureTrailStop(const int i,const FpTrailContext &ctx);

   //+---------------------------------------------------------------+
   //| Bulk exits                                                     |
   //+---------------------------------------------------------------+
   void              CloseAllOpen(const string reason);

   //--- Closes anything still open once a limit has been breached and the user
   //    asked for that. Runs from the bar loop, never from inside a callback.
   void              ApplyDailyLimitFlatten(void);

   //+---------------------------------------------------------------+
   //| Run summary                                                    |
   //+---------------------------------------------------------------+
   string            RunSummary(void) const;
  };

//--- The larger bodies live in their own file purely to keep both under the
//    500-line ceiling this repository works to.
#include "TradeManagerImpl.mqh"

#endif // FPMR_TRADE_MANAGER_MQH
//+------------------------------------------------------------------+
