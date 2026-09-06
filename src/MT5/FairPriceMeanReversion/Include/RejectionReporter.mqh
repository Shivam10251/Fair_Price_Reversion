//+------------------------------------------------------------------+
//| FPMR · Core · RejectionReporter                                  |
//|                                                                  |
//| Every structure break is a displacement candle. If it did not     |
//| become a trade, exactly one gate stopped it. This reports the     |
//| FIRST gate that failed, in the same order the entry logic         |
//| evaluates them, so "why was there no entry here?" always has a    |
//| single answer.                                                    |
//|                                                                  |
//| Order (session and time gates first, then side, then budget, then |
//| filters, then the risk gates, then the broker gate that only      |
//| exists in the MT5 build):                                         |
//|    SESSION -> NO FP -> NEWS WAIT -> WARMUP -> IN ZONE -> TOO FAR  |
//|    -> SIDE -> DAY LOSS -> DAY PROFIT -> DAY CAP -> SESS CAP       |
//|    -> IN TRADE -> CONCURRENCY -> EVT OFF -> EMA -> VWAP           |
//|    -> RISK -> RISK CAP -> MARGIN                                  |
//|                                                                  |
//| The two daily P&L gates sit with the other budget gates and ahead |
//| of the filters: once the day's realised loss or profit limit is   |
//| reached, WHY a particular filter would also have blocked the      |
//| trade is no longer interesting.                                   |
//+------------------------------------------------------------------+
#ifndef FPMR_REJECTION_REPORTER_MQH
#define FPMR_REJECTION_REPORTER_MQH

#include "FpmrEnums.mqh"

//--- Snapshot of every gate for one displacement candle.
struct GateState
  {
   bool reconciled;    // EA is not stuck on an unmatched restart position
   bool inSession;
   bool hasFairPrice;
   bool newsReady;     // no news surprise is still waiting for its consolidation
   bool warmupDone;
   bool inZone;
   bool distanceOk;    // entry is not beyond Band 2 (too far from Fair Price)
   bool sideOk;
   bool dailyLossOk;   // realised loss for the day is inside the limit
   bool dailyProfitOk; // realised profit for the day is inside the limit
   bool dayCapOk;
   bool sessionCapOk;
   bool flatOk;        // "one at a time" is satisfied
   bool concurrencyOk; // room left under the per-direction concurrency ceiling
   bool eventOk;
   bool emaOk;
   bool vwapOk;
   bool riskOk;        // risk > 0 and >= minimum stop distance
   bool riskCapOk;     // sizing produced at least one lot within the hard cap
   bool marginOk;      // the account can actually carry the sized order
  };

void GateStateClear(GateState &g)
  {
   g.reconciled    = true;
   g.inSession     = false;
   g.hasFairPrice  = false;
   g.newsReady     = true;
   g.warmupDone    = false;
   g.inZone        = false;
   g.distanceOk    = false;
   g.sideOk        = false;
   g.dailyLossOk   = true;
   g.dailyProfitOk = true;
   g.dayCapOk      = true;
   g.sessionCapOk  = true;
   g.flatOk        = true;
   g.concurrencyOk = true;
   g.eventOk       = true;
   g.emaOk         = true;
   g.vwapOk        = true;
   g.riskOk        = false;
   g.riskCapOk     = false;
   g.marginOk      = true;
  }

FpReject RejectionFirstFailure(const GateState &g)
  {
   if(!g.reconciled)    return(FP_REJ_UNRECONCILED);
   if(!g.inSession)     return(FP_REJ_SESSION);
   if(!g.hasFairPrice)  return(FP_REJ_NO_FAIR_PRICE);
   if(!g.newsReady)     return(FP_REJ_NEWS_WAIT);
   if(!g.warmupDone)    return(FP_REJ_WARMUP);
   if(g.inZone)         return(FP_REJ_IN_ZONE);
   if(!g.distanceOk)    return(FP_REJ_TOO_FAR);
   if(!g.sideOk)        return(FP_REJ_SIDE);
   if(!g.dailyLossOk)   return(FP_REJ_DAILY_LOSS);
   if(!g.dailyProfitOk) return(FP_REJ_DAILY_PROFIT);
   if(!g.dayCapOk)      return(FP_REJ_DAY_CAP);
   if(!g.sessionCapOk)  return(FP_REJ_SESSION_CAP);
   if(!g.flatOk)        return(FP_REJ_IN_TRADE);
   if(!g.concurrencyOk) return(FP_REJ_CONCURRENCY);
   if(!g.eventOk)       return(FP_REJ_EVENT_OFF);
   if(!g.emaOk)         return(FP_REJ_EMA);
   if(!g.vwapOk)        return(FP_REJ_VWAP);
   if(!g.riskOk)        return(FP_REJ_RISK);
   if(!g.riskCapOk)     return(FP_REJ_RISK_CAP);
   if(!g.marginOk)      return(FP_REJ_MARGIN);

   return(FP_REJ_NONE);
  }

//--- Short label used in the log and on the chart comment.
string RejectionLabel(const FpReject reason)
  {
   switch(reason)
     {
      case FP_REJ_SESSION:       return("SESSION");
      case FP_REJ_NO_FAIR_PRICE: return("NO FP");
      case FP_REJ_NEWS_WAIT:     return("NEWS WAIT");
      case FP_REJ_WARMUP:        return("WARMUP");
      case FP_REJ_IN_ZONE:       return("IN ZONE");
      case FP_REJ_TOO_FAR:       return("TOO FAR");
      case FP_REJ_SIDE:          return("SIDE");
      case FP_REJ_DAILY_LOSS:    return("DAY LOSS");
      case FP_REJ_DAILY_PROFIT:  return("DAY PROFIT");
      case FP_REJ_DAY_CAP:       return("DAY CAP");
      case FP_REJ_SESSION_CAP:   return("SESS CAP");
      case FP_REJ_IN_TRADE:      return("IN TRADE");
      case FP_REJ_CONCURRENCY:   return("MAX OPEN");
      case FP_REJ_EVENT_OFF:     return("EVT OFF");
      case FP_REJ_EMA:           return("EMA");
      case FP_REJ_VWAP:          return("VWAP");
      case FP_REJ_RISK:          return("RISK");
      case FP_REJ_RISK_CAP:      return("RISK CAP");
      case FP_REJ_UNRECONCILED:  return("UNRECONCILED");
      case FP_REJ_MARGIN:        return("MARGIN");
      default:                   return("-");
     }
  }

#endif // FPMR_REJECTION_REPORTER_MQH
//+------------------------------------------------------------------+
