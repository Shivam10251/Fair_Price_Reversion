//+------------------------------------------------------------------+
//| FPMR · Core · TradeRecord                                        |
//|                                                                  |
//| Everything the strategy needs to know about one trade, from       |
//| signal to exit.                                                   |
//|                                                                  |
//| KEYED BY POSITION TICKET. The NinjaTrader build keyed trades by a |
//| unique entry SIGNAL NAME, because NT8 owns the bracket and links  |
//| exits back to the signal that opened them. MT5 has no such link:  |
//| the position itself is the identity, its stop and target live on  |
//| the broker's side of the connection, and both survive a terminal  |
//| restart. So the ticket is the key, and it is also what makes      |
//| several concurrent trades on one symbol safe on a hedging         |
//| account - each has its own ticket, its own stop and its own       |
//| target, with no shared net position to reason about.              |
//+------------------------------------------------------------------+
#ifndef FPMR_TRADE_RECORD_MQH
#define FPMR_TRADE_RECORD_MQH

#include "FpmrEnums.mqh"
#include "RiskSizer.mqh"

struct TradeRecord
  {
   ulong          ticket;            // MT5 position ticket - the identity
   long           positionId;        // POSITION_IDENTIFIER, for history lookups
   int            sequence;
   int            direction;         // +1 long, -1 short
   FpBreakEvent   event;

   double         signalPrice;       // displacement candle close
   double         stopPrice;         // current stop - moves as the trail tightens it
   double         initialStopPrice;  // stop at entry, the reference for the R ratchet
   double         targetPrice;

   FpTrailMode    trail;             // trailing behaviour resolved for this trade at entry
   double         maxFavorablePoints;// best favourable excursion since entry (R-step ratchet)

   double         lots;
   bool           targetIsFairPrice; // take-profit is Fair Price itself (a Band 2 "far" setup)

   int            entryBarIndex;
   datetime       entryBarTime;
   int            sessionIndex;

   SizingResult   sizing;

   bool           isFilled;
   double         fillPrice;

   bool           isClosed;
   string         exitReason;
   double         exitPrice;
   int            exitBarIndex;
   datetime       exitTime;

   double         realisedPnl;       // net of commission and swap, booked on close
   double         commission;
   //--- Money already added to the day's accumulator for this trade. The entry
   //    commission is booked the moment it is charged and the rest on close, so
   //    this is what stops the close from double-counting it.
   double         booked;

   bool           ambiguousBarSeen;  // a single bar contained both the stop and the target
  };

void TradeRecordClear(TradeRecord &t)
  {
   t.ticket             = 0;
   t.positionId         = 0;
   t.sequence           = 0;
   t.direction          = 0;
   t.event              = FP_EVENT_NONE;
   t.signalPrice        = FPMR_NA;
   t.stopPrice          = FPMR_NA;
   t.initialStopPrice   = FPMR_NA;
   t.targetPrice        = FPMR_NA;
   t.trail              = FP_TRAIL_OFF;
   t.maxFavorablePoints = 0.0;
   t.lots               = 0.0;
   t.targetIsFairPrice  = false;
   t.entryBarIndex      = -1;
   t.entryBarTime       = 0;
   t.sessionIndex       = 0;
   t.isFilled           = false;
   t.fillPrice          = FPMR_NA;
   t.isClosed           = false;
   t.exitReason         = "OPEN";
   t.exitPrice          = FPMR_NA;
   t.exitBarIndex       = -1;
   t.exitTime           = 0;
   t.realisedPnl        = 0.0;
   t.commission         = 0.0;
   t.booked             = 0.0;
   t.ambiguousBarSeen   = false;
   SizingResultClear(t.sizing);
  }

//+------------------------------------------------------------------+
//| Dollar loss still at risk if this trade hits its stop.            |
//|                                                                  |
//| Directional, and floored at zero: once the trail has moved the    |
//| stop to breakeven or into profit there is no dollar loss left at  |
//| risk, and the daily-loss projection must not pretend otherwise.   |
//+------------------------------------------------------------------+
double TradeOpenRiskUsd(const TradeRecord &t,const double moneyPerPricePerLot)
  {
   if(!t.isFilled || t.isClosed || FpIsNa(t.fillPrice) || FpIsNa(t.stopPrice))
      return(0.0);

   double lossPoints=(t.direction>0 ? t.fillPrice-t.stopPrice : t.stopPrice-t.fillPrice);
   if(lossPoints<=0.0)
      return(0.0);

   return(lossPoints*t.lots*moneyPerPricePerLot);
  }

//--- Bars ago of this trade's entry bar, relative to currentBarIndex.
int TradeBarsAgoFrom(const TradeRecord &t,const int currentBarIndex)
  {
   return((int)MathMax(0,currentBarIndex-t.entryBarIndex));
  }

string TradeDescribe(const TradeRecord &t,const int priceDigits,const int lotDigits)
  {
   return(StringFormat("%s %s #%d %s lots @ %s | SL %s | TP %s%s",
                       t.direction>0 ? "LONG" : "SHORT",
                       FpBreakEventText(t.event),
                       t.sequence,
                       DoubleToString(t.lots,lotDigits),
                       DoubleToString(t.signalPrice,priceDigits),
                       DoubleToString(t.stopPrice,priceDigits),
                       DoubleToString(t.targetPrice,priceDigits),
                       t.targetIsFairPrice ? " | FP-target" : ""));
  }

#endif // FPMR_TRADE_RECORD_MQH
//+------------------------------------------------------------------+
