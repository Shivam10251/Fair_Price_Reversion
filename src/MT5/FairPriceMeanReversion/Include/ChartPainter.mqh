//+------------------------------------------------------------------+
//| FPMR · Chart painter                                             |
//|                                                                  |
//| Draws what the strategy is thinking, so a chart can be read       |
//| instead of a log:                                                 |
//|                                                                  |
//|   * a translucent box over each session, open to close           |
//|   * the Fair Price line for that session, spanning the same width |
//|   * a red stop zone and a green target zone for every trade,      |
//|     from the entry bar to the bar the trade closed on             |
//|                                                                  |
//| POSITIONING IS IN SERVER TIME. MT5 stamps bars, and therefore     |
//| anchors chart objects, in broker server time, while the strategy  |
//| reasons in the session zone. Every time value handed to this      |
//| class has already been converted back by the caller - getting     |
//| that wrong would draw boxes hours away from the candles they      |
//| describe.                                                         |
//|                                                                  |
//| Objects are drawn BEHIND the candles (OBJPROP_BACK), which is how |
//| MT5 gives the "transparent" look - rectangles have no alpha       |
//| channel, so a filled box in front would hide the price action.    |
//|                                                                  |
//| A non-visual backtest draws nothing at all. Nobody can see it,    |
//| and creating tens of thousands of objects would slow the run down |
//| for no benefit, so the painter disables itself there.             |
//+------------------------------------------------------------------+
#ifndef FPMR_CHART_PAINTER_MQH
#define FPMR_CHART_PAINTER_MQH

#include "FpmrEnums.mqh"
#include "TradeRecord.mqh"

struct FpPaintConfig
  {
   bool   enabled;
   color  sessionBox;
   color  fairPrice;
   color  stopZone;
   color  targetZone;
   bool   keepOnExit;
  };

class CChartPainter
  {
private:
   FpPaintConfig     m_cfg;
   bool              m_active;      // enabled AND somebody can actually see it
   string            m_prefix;
   int               m_digits;

   //--- Sequence numbers whose zones have already been given their final right
   //    edge. Without this the painter would re-anchor every closed trade on
   //    every bar, which is pure waste once a trade can no longer change.
   bool              m_finalised[];

   //--- Session box extent, rebuilt as the session prints.
   string            m_boxName;
   string            m_lineName;
   datetime          m_boxFrom, m_boxTo;
   double            m_boxHigh, m_boxLow;

   void              Style(const string name,const color clr,const bool fill)
     {
      ObjectSetInteger(0,name,OBJPROP_COLOR,clr);
      ObjectSetInteger(0,name,OBJPROP_BACK,true);
      ObjectSetInteger(0,name,OBJPROP_FILL,fill);
      ObjectSetInteger(0,name,OBJPROP_SELECTABLE,false);
      ObjectSetInteger(0,name,OBJPROP_SELECTED,false);
      ObjectSetInteger(0,name,OBJPROP_HIDDEN,true);
      ObjectSetInteger(0,name,OBJPROP_ZORDER,0);
     }

   //--- Creates the object if it is missing, otherwise just moves both anchors.
   void              Box(const string name,const datetime t1,const double p1,
                         const datetime t2,const double p2,const color clr)
     {
      if(ObjectFind(0,name)<0)
        {
         if(!ObjectCreate(0,name,OBJ_RECTANGLE,0,t1,p1,t2,p2))
            return;
         Style(name,clr,true);
        }
      else
        {
         ObjectMove(0,name,0,t1,p1);
         ObjectMove(0,name,1,t2,p2);
        }
     }

   void              Segment(const string name,const datetime t1,const double p1,
                             const datetime t2,const double p2,const color clr,const int width)
     {
      if(ObjectFind(0,name)<0)
        {
         if(!ObjectCreate(0,name,OBJ_TREND,0,t1,p1,t2,p2))
            return;
         Style(name,clr,false);
         ObjectSetInteger(0,name,OBJPROP_WIDTH,width);
         ObjectSetInteger(0,name,OBJPROP_RAY_LEFT,false);
         ObjectSetInteger(0,name,OBJPROP_RAY_RIGHT,false);
        }
      else
        {
         ObjectMove(0,name,0,t1,p1);
         ObjectMove(0,name,1,t2,p2);
        }
     }

public:
                     CChartPainter(void)
     : m_active(false), m_prefix("FPMR_"), m_digits(2),
       m_boxName(""), m_lineName(""), m_boxFrom(0), m_boxTo(0),
       m_boxHigh(FPMR_NA), m_boxLow(FPMR_NA) {}

   bool              Active(void) const { return(m_active); }

   void              Init(const FpPaintConfig &cfg,const int digits)
     {
      m_cfg    = cfg;
      m_digits = digits;

      // A backtest with no visual window has no audience for any of this.
      bool blindTest = (bool)MQLInfoInteger(MQL_TESTER)
                       && !(bool)MQLInfoInteger(MQL_VISUAL_MODE);

      m_active = (cfg.enabled && !blindTest);

      ArrayFree(m_finalised);
      m_boxName  = "";
      m_lineName = "";

      if(m_active)
         Clear();
     }

   //--- Removes every object this EA owns, leaving other drawings alone.
   void              Clear(void)
     {
      ObjectsDeleteAll(0,m_prefix);
      ChartRedraw(0);
     }

   void              OnDeinit(void)
     {
      if(m_active && !m_cfg.keepOnExit)
         Clear();
     }

   //+---------------------------------------------------------------+
   //| Session box and Fair Price line                                |
   //|                                                                |
   //| Called once per bar while a session is running. The box spans   |
   //| the whole session from its first bar - the right edge is the    |
   //| session CLOSE, not the current bar - so the window being traded |
   //| is visible before the session has finished printing. Its height |
   //| grows with the session's own high and low.                      |
   //+---------------------------------------------------------------+
   void              SessionBar(const int sessionIndex,
                                const datetime openServer,const datetime closeServer,
                                const double barHigh,const double barLow,
                                const bool isSessionStart)
     {
      if(!m_active || sessionIndex==0)
         return;

      if(isSessionStart || m_boxName=="")
        {
         string stamp=IntegerToString((long)openServer);
         m_boxName  = m_prefix+"S"+IntegerToString(sessionIndex)+"_box_"+stamp;
         m_lineName = m_prefix+"S"+IntegerToString(sessionIndex)+"_fp_"+stamp;
         m_boxFrom  = openServer;
         m_boxTo    = closeServer;
         m_boxHigh  = barHigh;
         m_boxLow   = barLow;
        }
      else
        {
         if(barHigh>m_boxHigh) m_boxHigh=barHigh;
         if(barLow <m_boxLow ) m_boxLow =barLow;
        }

      Box(m_boxName,m_boxFrom,m_boxHigh,m_boxTo,m_boxLow,m_cfg.sessionBox);
     }

   //--- The Fair Price line runs the full width of the session box, so it reads
   //    as "this is the level the whole session is measured against".
   void              FairPriceBar(const double fairPrice,const bool hasFairPrice)
     {
      if(!m_active || m_lineName=="" || !hasFairPrice || FpIsNa(fairPrice))
         return;

      Segment(m_lineName,m_boxFrom,fairPrice,m_boxTo,fairPrice,m_cfg.fairPrice,2);
      ObjectSetString(0,m_lineName,OBJPROP_TOOLTIP,
                      "Fair Price "+DoubleToString(fairPrice,m_digits));
     }

   //+---------------------------------------------------------------+
   //| Trade stop and target zones                                    |
   //|                                                                |
   //| Two boxes per trade, both anchored on the entry price: one down |
   //| to the stop, one up to the target (inverted for a short). While |
   //| the trade is open the right edge tracks the current bar; once   |
   //| it closes the edge is pinned to the exit and never touched      |
   //| again.                                                          |
   //+---------------------------------------------------------------+
   void              TradeZones(const TradeRecord &t,const datetime nowServer)
     {
      if(!m_active || t.sequence<=0 || !t.isFilled)
         return;

      int seq=t.sequence;
      if(ArraySize(m_finalised)<seq)
        {
         int was=ArraySize(m_finalised);
         ArrayResize(m_finalised,seq);
         for(int i=was;i<seq;i++)
            m_finalised[i]=false;
        }

      if(m_finalised[seq-1])
         return;

      double anchor=(FpIsNa(t.fillPrice) ? t.signalPrice : t.fillPrice);
      if(FpIsNa(anchor))
         return;

      datetime from=t.entryBarTime;
      datetime to  =(t.isClosed && t.exitTime>0) ? t.exitTime : nowServer;

      // A trade that opens and closes inside one bar would otherwise be a
      // zero-width box, which MT5 draws as nothing at all.
      if(to<=from)
         to=from+PeriodSeconds(_Period);

      string tag=m_prefix+"T"+IntegerToString(seq);

      if(!FpIsNa(t.stopPrice))
         Box(tag+"_sl",from,anchor,to,t.stopPrice,m_cfg.stopZone);

      if(!FpIsNa(t.targetPrice))
         Box(tag+"_tp",from,anchor,to,t.targetPrice,m_cfg.targetZone);

      string tip=StringFormat("#%d %s %s  entry %s  SL %s  TP %s%s",
                              seq,
                              t.direction>0 ? "LONG" : "SHORT",
                              FpBreakEventText(t.event),
                              DoubleToString(anchor,m_digits),
                              DoubleToString(t.stopPrice,m_digits),
                              DoubleToString(t.targetPrice,m_digits),
                              t.isClosed ? "  -> "+t.exitReason : "");

      ObjectSetString(0,tag+"_sl",OBJPROP_TOOLTIP,tip);
      ObjectSetString(0,tag+"_tp",OBJPROP_TOOLTIP,tip);

      if(t.isClosed)
         m_finalised[seq-1]=true;
     }

   void              Flush(void)
     {
      if(m_active)
         ChartRedraw(0);
     }
  };

#endif // FPMR_CHART_PAINTER_MQH
//+------------------------------------------------------------------+
