//+------------------------------------------------------------------+
//| FPMR · Trading · TradeManager · method bodies                    |
//|                                                                  |
//| Included from TradeManager.mqh, never on its own. Split out only  |
//| for file length; the declarations and the contract they belong to |
//| are in TradeManager.mqh.                                          |
//+------------------------------------------------------------------+
#ifndef FPMR_TRADE_MANAGER_IMPL_MQH
#define FPMR_TRADE_MANAGER_IMPL_MQH

//+------------------------------------------------------------------+
void CTradeManager::ReconcileExisting(const int currentBarIndex)
  {
   int adopted=0;
   int foreign=0;

   for(int i=PositionsTotal()-1;i>=0;i--)
     {
      ulong ticket=PositionGetTicket(i);
      if(ticket==0 || !PositionSelectByTicket(ticket))
         continue;

      if(PositionGetString(POSITION_SYMBOL)!=m_sym.name)
         continue;

      if((ulong)PositionGetInteger(POSITION_MAGIC)!=m_cfg.magic)
        {
         foreign++;
         continue;
        }

      TradeRecord t;
      TradeRecordClear(t);

      t.ticket           = ticket;
      t.positionId       = PositionGetInteger(POSITION_IDENTIFIER);
      t.direction        = (PositionGetInteger(POSITION_TYPE)==POSITION_TYPE_BUY ? 1 : -1);
      t.lots             = PositionGetDouble(POSITION_VOLUME);
      t.fillPrice        = PositionGetDouble(POSITION_PRICE_OPEN);
      t.signalPrice      = t.fillPrice;
      t.stopPrice        = PositionGetDouble(POSITION_SL);
      t.initialStopPrice = t.stopPrice;
      t.targetPrice      = PositionGetDouble(POSITION_TP);
      t.isFilled         = true;
      t.entryBarIndex    = currentBarIndex;
      t.entryBarTime     = (datetime)PositionGetInteger(POSITION_TIME);
      t.trail            = FP_TRAIL_OFF;   // an adopted trade keeps the stop it has
      t.exitReason       = "OPEN (adopted)";

      if(t.stopPrice<=0.0)   t.stopPrice=FPMR_NA;
      if(t.targetPrice<=0.0) t.targetPrice=FPMR_NA;
      t.initialStopPrice=t.stopPrice;

      int n=ArraySize(m_records);
      ArrayResize(m_records,n+1);
      m_records[n]=t;
      adopted++;
     }

   if(adopted>0)
      Say(StringFormat("FPMR: adopted %d existing position(s) carrying magic %s. Their stops are left "
                       "where they are; trailing does not take over a trade this run did not open.",
                       adopted,IntegerToString((long)m_cfg.magic)));

   if(foreign>0)
     {
      m_unreconciled=true;
      Say(StringFormat("FPMR: found %d position(s) on %s that this EA has no record of. New entries are "
                       "BLOCKED until the symbol is flat. Close them manually or clear them and restart.",
                       foreign,m_sym.name));
     }
  }

//+------------------------------------------------------------------+
void CTradeManager::RefreshReconciliation(void)
  {
   if(!m_unreconciled)
      return;

   for(int i=PositionsTotal()-1;i>=0;i--)
     {
      ulong ticket=PositionGetTicket(i);
      if(ticket==0 || !PositionSelectByTicket(ticket))
         continue;
      if(PositionGetString(POSITION_SYMBOL)!=m_sym.name)
         continue;
      if((ulong)PositionGetInteger(POSITION_MAGIC)==m_cfg.magic)
         continue;

      return;  // a foreign position is still open
     }

   m_unreconciled=false;
   Say("FPMR: the unrecognised position(s) are gone and the symbol is reconciled. Entries are enabled again.");
  }

//+------------------------------------------------------------------+
bool CTradeManager::Submit(const int dir,const FpBreakEvent event, const double signalPrice,const double stop,const double target, const bool targetIsFairPrice,const FpTrailMode trail, const SizingResult &sizing, const int barIndex,const datetime barTime,const int sessionIndex, string &error)
  {
   error="";

   double price=(dir>0 ? SymbolInfoDouble(m_sym.name,SYMBOL_ASK)
                       : SymbolInfoDouble(m_sym.name,SYMBOL_BID));

   if(price<=0.0)
     {
      error="no current quote for "+m_sym.name;
      return(false);
     }

   if(!BracketIsPlaceable(dir,price,stop,target))
     {
      error=StringFormat("bracket is inside the broker's %d point minimum stop distance "
                         "(entry %s, SL %s, TP %s)",
                         m_sym.stopsLevelPoints,
                         DoubleToString(price,m_sym.digits),
                         DoubleToString(stop,m_sym.digits),
                         DoubleToString(target,m_sym.digits));
      return(false);
     }

   m_tradeSeq++;
   string comment=StringFormat("FPMR%d %s",m_tradeSeq,FpBreakEventText(event));

   bool ok=(dir>0)
           ? m_trade.Buy(sizing.lots,m_sym.name,0.0,stop,target,comment)
           : m_trade.Sell(sizing.lots,m_sym.name,0.0,stop,target,comment);

   if(!ok)
     {
      error=StringFormat("retcode %d (%s)",m_trade.ResultRetcode(),m_trade.ResultRetcodeDescription());
      m_tradeSeq--;   // never counted as a trade that happened
      return(false);
     }

   TradeRecord t;
   TradeRecordClear(t);

   t.sequence          = m_tradeSeq;
   t.direction         = dir;
   t.event             = event;
   t.signalPrice       = signalPrice;
   t.stopPrice         = stop;
   t.initialStopPrice  = stop;
   t.targetPrice       = target;
   t.targetIsFairPrice = targetIsFairPrice;
   t.trail             = trail;
   t.lots              = sizing.lots;
   t.sizing            = sizing;
   t.entryBarIndex     = barIndex;
   t.entryBarTime      = barTime;
   t.sessionIndex      = sessionIndex;
   t.isFilled          = true;
   t.fillPrice         = m_trade.ResultPrice();
   t.ticket            = m_trade.ResultOrder();
   t.positionId        = (long)m_trade.ResultOrder();

   // Prefer the executed deal for the fill price, ticket and entry commission:
   // ResultPrice is the requested price on some brokers, and the deal is what
   // the account was actually charged.
   ulong dealTicket=m_trade.ResultDeal();
   if(dealTicket>0)
     {
      HistorySelect(TimeCurrent()-86400,TimeCurrent()+60);
      if(HistoryDealSelect(dealTicket))
        {
         t.positionId = HistoryDealGetInteger(dealTicket,DEAL_POSITION_ID);
         t.ticket     = (ulong)t.positionId;
         t.fillPrice  = HistoryDealGetDouble(dealTicket,DEAL_PRICE);
         t.lots       = HistoryDealGetDouble(dealTicket,DEAL_VOLUME);
         t.commission = HistoryDealGetDouble(dealTicket,DEAL_COMMISSION);
        }
     }

   int n=ArraySize(m_records);
   ArrayResize(m_records,n+1);
   m_records[n]=t;

   // Entry commission is part of the day's net result the moment it is charged.
   if(t.commission!=0.0)
     {
      m_records[n].booked=-t.commission;
      BookMoney(-t.commission,m_pnlDay);
     }

   return(true);
  }

//+------------------------------------------------------------------+
void CTradeManager::SyncClosures(const int currentBarIndex,const datetime tradingDay)
  {
   for(int i=0;i<ArraySize(m_records);i++)
     {
      if(m_records[i].isClosed || !m_records[i].isFilled)
         continue;

      if(PositionSelectByTicket(m_records[i].ticket))
        {
         // Still open - keep the local stop in step with the server's, which the
         // broker may have moved (a partial close, or a stop-out adjustment).
         double serverSl=PositionGetDouble(POSITION_SL);
         if(serverSl>0.0)
            m_records[i].stopPrice=serverSl;
         m_records[i].lots=PositionGetDouble(POSITION_VOLUME);
         continue;
        }

      CloseRecordFromHistory(i,currentBarIndex,tradingDay);
     }
  }

//+------------------------------------------------------------------+
void CTradeManager::CloseRecordFromHistory(const int i,const int currentBarIndex,const datetime tradingDay)
  {
   double net=0.0;
   double commission=0.0;
   double exitPrice=FPMR_NA;
   datetime exitTime=0;
   string reason="EXIT";

   if(HistorySelectByPosition(m_records[i].positionId))
     {
      int deals=HistoryDealsTotal();
      for(int d=0;d<deals;d++)
        {
         ulong dt=HistoryDealGetTicket(d);
         if(dt==0)
            continue;

         net        += HistoryDealGetDouble(dt,DEAL_PROFIT)
                     + HistoryDealGetDouble(dt,DEAL_COMMISSION)
                     + HistoryDealGetDouble(dt,DEAL_SWAP);
         commission += HistoryDealGetDouble(dt,DEAL_COMMISSION);

         ENUM_DEAL_ENTRY entry=(ENUM_DEAL_ENTRY)HistoryDealGetInteger(dt,DEAL_ENTRY);
         if(entry==DEAL_ENTRY_OUT || entry==DEAL_ENTRY_OUT_BY || entry==DEAL_ENTRY_INOUT)
           {
            exitPrice=HistoryDealGetDouble(dt,DEAL_PRICE);
            exitTime =(datetime)HistoryDealGetInteger(dt,DEAL_TIME);
            reason   =ClassifyExit((ENUM_DEAL_REASON)HistoryDealGetInteger(dt,DEAL_REASON),i);
           }
        }
     }

   m_records[i].isClosed     = true;
   m_records[i].exitReason   = reason;
   m_records[i].exitPrice    = exitPrice;
   m_records[i].exitTime     = exitTime;
   m_records[i].exitBarIndex = currentBarIndex;
   m_records[i].realisedPnl  = net;
   m_records[i].commission   = commission;

   double toBook=net-m_records[i].booked;
   m_records[i].booked=net;

   // The exit's own day wins; the caller's day is only the fallback for a
   // closure the deal history could not date.
   BookMoney(toBook, exitTime>0 ? TradingDayOf(exitTime) : tradingDay);

   m_lastExitText=reason+" @ "+DoubleToString(exitPrice,m_sym.digits);

   if(m_cfg.verboseLogging)
      Say(StringFormat("%s  EXIT #%d %s at %s | net %.2f%s",
                       TimeToString(exitTime>0 ? exitTime : TimeCurrent(),TIME_DATE|TIME_SECONDS),
                       m_records[i].sequence, reason,
                       DoubleToString(exitPrice,m_sym.digits), net,
                       m_records[i].ambiguousBarSeen
                         ? "  (same-candle TP/SL)"
                         : ""));
  }

//+------------------------------------------------------------------+
string CTradeManager::ClassifyExit(const ENUM_DEAL_REASON dealReason,const int i)
  {
   if(dealReason==DEAL_REASON_SL) return("SL");
   if(dealReason==DEAL_REASON_TP) return("TP");

   if(!m_records[i].ambiguousBarSeen)
      return("EXIT");

   switch(m_cfg.sameBarPriority)
     {
      case FP_SAMEBAR_SL_FIRST: return("SL");
      case FP_SAMEBAR_TP_FIRST: return("TP");
      default:                  return("EXIT");
     }
  }

//+------------------------------------------------------------------+
void CTradeManager::ScanAmbiguity(const double high,const double low)
  {
   for(int i=0;i<ArraySize(m_records);i++)
     {
      if(m_records[i].isClosed || !m_records[i].isFilled)
         continue;
      if(FpIsNa(m_records[i].stopPrice) || FpIsNa(m_records[i].targetPrice))
         continue;

      bool ambiguous=(m_records[i].direction<0)
                     ? (high>=m_records[i].stopPrice && low<=m_records[i].targetPrice)
                     : (low<=m_records[i].stopPrice && high>=m_records[i].targetPrice);

      if(ambiguous && !m_records[i].ambiguousBarSeen)
        {
         m_records[i].ambiguousBarSeen=true;
         m_ambiguousCount++;
        }
     }
  }

//+------------------------------------------------------------------+
void CTradeManager::UpdateTrailingStops(const FpTrailContext &ctx)
  {
   for(int i=0;i<ArraySize(m_records);i++)
     {
      if(m_records[i].trail==FP_TRAIL_OFF || m_records[i].isClosed || !m_records[i].isFilled)
         continue;
      if(FpIsNa(m_records[i].fillPrice) || FpIsNa(m_records[i].stopPrice))
         continue;

      double newStop=(m_records[i].trail==FP_TRAIL_RSTEP)
                     ? RStepTrailStop(i,ctx)
                     : StructureTrailStop(i,ctx);

      if(FpIsNa(newStop))
         continue;

      // Tighten only: a long's stop may only rise, a short's may only fall.
      bool tighter=(m_records[i].direction>0)
                   ? (newStop>m_records[i].stopPrice)
                   : (newStop<m_records[i].stopPrice);
      if(!tighter)
         continue;

      if(!PositionSelectByTicket(m_records[i].ticket))
         continue;

      // The broker refuses a stop inside its minimum distance, and refuses ANY
      // modification inside the freeze distance. Skipping is correct: the stop
      // that is already there stays, so risk never widens.
      double market=(m_records[i].direction>0)
                    ? SymbolInfoDouble(m_sym.name,SYMBOL_BID)
                    : SymbolInfoDouble(m_sym.name,SYMBOL_ASK);

      double minDist=MinStopDistance();
      double gap=(m_records[i].direction>0 ? market-newStop : newStop-market);
      if(minDist>0.0 && gap<minDist)
         continue;

      double freeze=m_sym.freezeLevelPoints*m_sym.point;
      if(freeze>0.0 && MathAbs(market-m_records[i].stopPrice)<freeze)
         continue;

      double tp=(FpIsNa(m_records[i].targetPrice) ? 0.0 : m_records[i].targetPrice);

      if(m_trade.PositionModify(m_records[i].ticket,newStop,tp))
        {
         m_records[i].stopPrice=newStop;

         if(m_cfg.verboseLogging)
            Say(StringFormat("%s  TRAIL #%d SL -> %s (%s)",
                             TimeToString(TimeCurrent(),TIME_DATE|TIME_SECONDS),
                             m_records[i].sequence,
                             DoubleToString(newStop,m_sym.digits),
                             FpTrailModeText(m_records[i].trail)));
        }
      else if(m_cfg.verboseLogging)
        {
         Say(StringFormat("FPMR: could not move #%d stop to %s - retcode %d (%s)",
                          m_records[i].sequence,DoubleToString(newStop,m_sym.digits),
                          m_trade.ResultRetcode(),m_trade.ResultRetcodeDescription()));
        }
     }
  }

//+------------------------------------------------------------------+
double CTradeManager::RStepTrailStop(const int i,const FpTrailContext &ctx)
  {
   if(FpIsNa(m_records[i].initialStopPrice))
      return(FPMR_NA);

   double r=MathAbs(m_records[i].fillPrice-m_records[i].initialStopPrice);
   if(r<=0.0)
      return(FPMR_NA);

   double favor=(m_records[i].direction>0)
                ? ctx.high-m_records[i].fillPrice
                : m_records[i].fillPrice-ctx.low;

   if(favor>m_records[i].maxFavorablePoints)
      m_records[i].maxFavorablePoints=favor;

   int steps=(int)MathFloor(m_records[i].maxFavorablePoints/r);
   if(steps<1)
      return(FPMR_NA);

   double stop=m_records[i].fillPrice+m_records[i].direction*(steps-1)*r;
   return(FpRoundToTick(stop,m_sym));
  }

//+------------------------------------------------------------------+
double CTradeManager::StructureTrailStop(const int i,const FpTrailContext &ctx)
  {
   if(m_records[i].direction<0)
     {
      if(FpIsNa(ctx.lastSwingHigh))
         return(FPMR_NA);

      double stop=FpRoundToTick(ctx.lastSwingHigh+ctx.stopBuffer,m_sym);
      return(stop>ctx.close ? stop : FPMR_NA);
     }

   if(FpIsNa(ctx.lastSwingLow))
      return(FPMR_NA);

   double stopLong=FpRoundToTick(ctx.lastSwingLow-ctx.stopBuffer,m_sym);
   return(stopLong<ctx.close ? stopLong : FPMR_NA);
  }

//+------------------------------------------------------------------+
void CTradeManager::CloseAllOpen(const string reason)
  {
   for(int i=0;i<ArraySize(m_records);i++)
     {
      if(m_records[i].isClosed || !m_records[i].isFilled)
         continue;
      if(!PositionSelectByTicket(m_records[i].ticket))
         continue;

      if(!m_trade.PositionClose(m_records[i].ticket,m_cfg.slippagePoints))
         Say(StringFormat("FPMR: %s close of #%d failed - retcode %d (%s)",
                          reason,m_records[i].sequence,
                          m_trade.ResultRetcode(),m_trade.ResultRetcodeDescription()));
     }
  }

//+------------------------------------------------------------------+
void CTradeManager::ApplyDailyLimitFlatten(void)
  {
   if(!m_flattenRequested)
      return;

   if(OpenCount()==0)
     {
      m_flattenRequested=false;
      return;
     }

   CloseAllOpen("daily limit");
  }

//+------------------------------------------------------------------+
string CTradeManager::RunSummary(void) const
  {
   double pct=(m_tradeSeq>0 ? 100.0*m_ambiguousCount/m_tradeSeq : 0.0);

   return(StringFormat("FPMR run summary: %d trades placed | %d contained BOTH stop and target in one candle (%.1f%%) | "
                       "exits resolved by the broker's own SL/TP orders",
                       m_tradeSeq,m_ambiguousCount,pct));
  }

#endif // FPMR_TRADE_MANAGER_IMPL_MQH
//+------------------------------------------------------------------+
