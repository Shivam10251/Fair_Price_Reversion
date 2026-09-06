//+------------------------------------------------------------------+
//| FPMR · Strategy engine · chart drawing                           |
//|                                                                  |
//| Included from FpmrStrategy.mqh, never on its own. Split out from  |
//| FpmrStrategyImpl.mqh for file length. The drawing itself lives in |
//| ChartPainter.mqh; this is only the per-bar hand-off, which is     |
//| where the session zone times are converted back to the SERVER     |
//| clock that chart objects are anchored to.                         |
//+------------------------------------------------------------------+
#ifndef FPMR_STRATEGY_PAINT_MQH
#define FPMR_STRATEGY_PAINT_MQH

//+------------------------------------------------------------------+
//| Chart drawing                                                    |
//|                                                                  |
//| Runs last, so the boxes reflect the state the bar ended in. Every |
//| time value is converted back to SERVER time first, because that   |
//| is the clock MT5 anchors chart objects to.                       |
//+------------------------------------------------------------------+
void CFpmrStrategy::PaintChart(const SessionEvaluation &ev,const bool isSessionStart)
  {
   if(!m_painter.Active())
      return;

   if(m_effSession!=0 && ev.sessionOpenTz>0)
     {
      datetime closeTz     = m_sessions.CloseOfSession(m_effSession,ev.sessionOpenTz);
      datetime openServer  = m_clock.FromZone(ev.sessionOpenTz,m_sessionTz);
      datetime closeServer = m_clock.FromZone(closeTz,m_sessionTz);

      m_painter.SessionBar(m_effSession,openServer,closeServer,m_barHigh,m_barLow,isSessionStart);
      m_painter.FairPriceBar(m_fairPrice,m_hasFair);
     }

   int n=m_trades.RecordCount();
   for(int i=0;i<n;i++)
     {
      TradeRecord t;
      m_trades.GetRecord(i,t);
      m_painter.TradeZones(t,m_barOpenTime+m_primarySeconds);
     }

   m_painter.Flush();
  }

#endif // FPMR_STRATEGY_PAINT_MQH
//+------------------------------------------------------------------+
