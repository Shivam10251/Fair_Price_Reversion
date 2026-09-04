//+------------------------------------------------------------------+
//| FPMR · Core · FairPriceEngine                                    |
//|                                                                  |
//| Runs on the REFERENCE timeframe (1 minute by default), not        |
//| necessarily the chart timeframe, exactly like the Pine            |
//| request.security() version:                                       |
//|                                                                  |
//|    session bar 1 -> arm                                          |
//|    session bar 2 -> FairPrice := source OF BAR 1, i.e. after it   |
//|                     closed                                        |
//|    session end   -> FairPrice := NA                              |
//|                                                                  |
//| Fair Price is therefore never read from an unfinished candle.     |
//|                                                                  |
//| NEWS OVERRIDE                                                    |
//| When a qualifying release lands inside the lookback window before |
//| a session opens, that session's Fair Price is the OPEN of the     |
//| news candle and the first-candle rule is never armed for it. The  |
//| value goes live on the news candle itself - before the session    |
//| opens - because the AfterNewsCandle start mode has to be able to  |
//| trade from there. The session-open value is discarded, not        |
//| averaged and not used as a fallback.                              |
//|                                                                  |
//| The news branches are wired but inert until phase 6 supplies a    |
//| resolver: with no captures the engine is the pure session         |
//| first-candle rule.                                                |
//+------------------------------------------------------------------+
#ifndef FPMR_FAIR_PRICE_ENGINE_MQH
#define FPMR_FAIR_PRICE_ENGINE_MQH

#include "FpmrEnums.mqh"
#include "NewsTypes.mqh"

class CFairPriceEngine
  {
private:
   double      m_fairPrice;
   bool        m_isNewsFairPrice;
   bool        m_changedThisBar;
   int         m_newsSessionIndex;
   datetime    m_newsSessionOpenTz;
   NewsEvent   m_newsEventUsed;
   FpNewsBias  m_newsBias;

   int         m_currentSessionIndex;
   bool        m_armed;

   //--- When true a news Fair Price is discarded at the session open, handing
   //    the session over to its own first-candle rule.
   bool        m_newsExpiresAtSessionOpen;

   void              ClearNews(void)
     {
      m_isNewsFairPrice   = false;
      m_newsSessionIndex  = 0;
      m_newsSessionOpenTz = 0;
      m_newsBias          = FP_BIAS_REVERSION;
      NewsEventClear(m_newsEventUsed);
     }

   void              AdoptNews(const NewsFairPrice &c)
     {
      m_fairPrice         = c.fairPrice;
      m_isNewsFairPrice   = true;
      m_newsSessionIndex  = c.sessionIndex;
      m_newsSessionOpenTz = c.sessionOpenTz;
      m_newsEventUsed     = c.source;
      m_newsBias          = c.bias;
      m_armed             = false;
     }

public:
                     CFairPriceEngine(void) : m_newsExpiresAtSessionOpen(true) { Reset(); }

   void              Init(const bool newsExpiresAtSessionOpen)
     {
      m_newsExpiresAtSessionOpen=newsExpiresAtSessionOpen;
      Reset();
     }

   double            FairPrice(void)         const { return(m_fairPrice); }
   bool              HasFairPrice(void)      const { return(!FpIsNa(m_fairPrice)); }
   bool              IsNewsFairPrice(void)   const { return(m_isNewsFairPrice); }
   bool              ChangedThisBar(void)    const { return(m_changedThisBar); }
   int               NewsSessionIndex(void)  const { return(m_newsSessionIndex); }
   datetime          NewsSessionOpenTz(void) const { return(m_newsSessionOpenTz); }
   FpNewsBias        NewsBias(void)          const { return(m_newsBias); }

   void              NewsEventUsed(NewsEvent &out) const { out=m_newsEventUsed; }

   void              Reset(void)
     {
      m_fairPrice           = FPMR_NA;
      m_changedThisBar      = false;
      m_currentSessionIndex = 0;
      m_armed               = false;
      ClearNews();
     }

   //+---------------------------------------------------------------+
   //| Advances the engine by one REFERENCE bar.                     |
   //|                                                               |
   //| sessionIndex          session index of this reference bar      |
   //|                       (0 = outside every window)              |
   //| sessionOpenTz         open instant of that session, ignored    |
   //|                       when sessionIndex is 0                   |
   //| sourceOfPreviousBar   configured Fair Price source of the      |
   //|                       PREVIOUS reference bar                   |
   //| captureThisBar        a news capture made on this very bar     |
   //| pendingForThisSession capture waiting for the session that     |
   //|                       just started                             |
   //| tzNow                 this bar's open time in the session      |
   //|                       zone, used to expire a held news price   |
   //+---------------------------------------------------------------+
   void              OnReferenceBar(const int sessionIndex,
                                    const datetime sessionOpenTz,
                                    const double sourceOfPreviousBar,
                                    const NewsFairPrice &captureThisBar,
                                    const NewsFairPrice &pendingForThisSession,
                                    const datetime tzNow)
     {
      m_changedThisBar=false;
      double before=m_fairPrice;

      // 1. A news candle just printed: Fair Price goes live immediately, even
      //    though the session it belongs to has not opened yet.
      if(captureThisBar.valid)
         AdoptNews(captureThisBar);

      if(sessionIndex==0)
        {
         m_currentSessionIndex=0;

         // Hold a news Fair Price across the gap between the news candle and
         // the session it belongs to; drop everything else at session end.
         bool holdingNews=(m_isNewsFairPrice && m_newsSessionIndex!=0 && tzNow<m_newsSessionOpenTz);

         if(!holdingNews)
           {
            m_fairPrice=FPMR_NA;
            m_armed=false;
            ClearNews();
           }
        }
      else if(sessionIndex!=m_currentSessionIndex)
        {
         m_currentSessionIndex=sessionIndex;

         if(pendingForThisSession.valid && !m_newsExpiresAtSessionOpen)
           {
            // News override wins outright for the whole session: the first-candle
            // rule is never armed. Only taken when the news price does NOT expire
            // at the open.
            AdoptNews(pendingForThisSession);
           }
         else
           {
            m_fairPrice=FPMR_NA;
            m_armed=true;
            ClearNews();
           }
        }
      else if(m_armed)
        {
         m_fairPrice       = sourceOfPreviousBar;
         m_isNewsFairPrice = false;
         m_newsBias        = FP_BIAS_REVERSION;
         m_armed           = false;
        }

      bool wasNa=FpIsNa(before);
      bool isNa =FpIsNa(m_fairPrice);
      m_changedThisBar=(!isNa && (wasNa || MathAbs(m_fairPrice-before)>DBL_EPSILON));
     }

   //--- Selects the configured source price from a bar's OHLC.
   static double     SelectSource(const FpSource source,
                                  const double open,const double high,
                                  const double low,const double close)
     {
      switch(source)
        {
         case FP_SRC_OPEN: return(open);
         case FP_SRC_HL2:  return((high+low)/2.0);
         case FP_SRC_HLC3: return((high+low+close)/3.0);
         default:          return(close);
        }
     }
  };

#endif // FPMR_FAIR_PRICE_ENGINE_MQH
//+------------------------------------------------------------------+
