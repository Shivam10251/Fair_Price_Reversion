//+------------------------------------------------------------------+
//| FPMR · Strategy engine · filters and diagnostics                 |
//|                                                                  |
//| Included from FpmrStrategy.mqh, never on its own. Split out from  |
//| FpmrStrategyImpl.mqh purely for file length; these are the        |
//| optional gates that sit last in the rejection order, plus the     |
//| reporting that says which gate stopped a displacement candle.     |
//+------------------------------------------------------------------+
#ifndef FPMR_STRATEGY_FILTERS_MQH
#define FPMR_STRATEGY_FILTERS_MQH

//+------------------------------------------------------------------+
//| Take-profit selection, decided by the band the entry landed in:  |
//|   NEAR + fixed on -> a fixed number of points from the entry.    |
//|   FAR             -> Fair Price itself (the full reversion).     |
//|   NEAR, fixed off -> the Band 1 risk/reward multiple.            |
//+------------------------------------------------------------------+
double CFpmrStrategy::ComputeTarget(const int dir,const double entry,const double risk,
                                    const FpSetupBand band,const bool useFixed,bool &targetIsFair)
  {
   double raw;

   if(useFixed)
     {
      raw=(dir<0 ? entry-m_cfg.fixedTakeProfitPoints : entry+m_cfg.fixedTakeProfitPoints);
      targetIsFair=false;
     }
   else if(band==FP_BAND_FAR)
     {
      raw=m_fairPrice;
      targetIsFair=true;
     }
   else
     {
      raw=(dir<0 ? entry-risk*m_cfg.rewardRatio : entry+risk*m_cfg.rewardRatio);
      targetIsFair=false;
     }

   return(FpRoundToTick(raw,m_sym));
  }


//+------------------------------------------------------------------+
//| Which side the current regime allows.                            |
//|                                                                  |
//| REVERSION: price above the Fair Price zone only permits shorts,  |
//| below it only longs - the trade is always back toward Fair Price.|
//| CONTINUATION inverts that, so the trade goes WITH the move away  |
//| from Fair Price. Two independent things ask for continuation:    |
//|   - the BOS-continuation ENTRY MODEL, which is the user choosing |
//|     it for every session; and                                    |
//|   - a large news surprise (phase 6), which treats the move as    |
//|     legitimate repricing rather than something to fade.          |
//| Either one is enough, so under the BOS-continuation model the    |
//| news bias can no longer flip the direction back.                 |
//+------------------------------------------------------------------+
bool CFpmrStrategy::SideAllowed(const int dir)
  {
   bool continuation=(m_cfg.entryModel==FP_ENTRY_BOS_CONTINUATION
                      || m_fair.NewsBias()==FP_BIAS_CONTINUATION);

   if(continuation)
      return(dir<0 ? m_posState==-1 : m_posState==1);

   return(dir<0 ? m_posState==1 : m_posState==-1);
  }

//+------------------------------------------------------------------+
//| Which break EVENTS may become a trade. The BOS-continuation model|
//| is BOS-only by definition, so a CHoCH is refused there whatever  |
//| the CHoCH switch says.                                           |
//+------------------------------------------------------------------+
bool CFpmrStrategy::EventAllowed(const FpBreakEvent event)
  {
   if(event==FP_EVENT_CHOCH)
      return(m_cfg.entryModel!=FP_ENTRY_BOS_CONTINUATION && m_cfg.takeChochEntries);

   return(m_cfg.takeBosEntries);
  }

//+------------------------------------------------------------------+
//| Filters and diagnostics                                          |
//+------------------------------------------------------------------+


//--- EMA gate. Each leg is independent:
//      both legs on  -> crossover rule, long needs EMA1 above EMA2;
//      one leg on    -> price-vs-EMA rule, long needs the close above that EMA;
//      no leg on     -> no constraint, same as the master switch being off.
//    A disabled leg has an invalid handle, so the shape of the test follows what
//    was built. A handle that cannot be read yet passes rather than blocks, the
//    same way an unfilled NinjaTrader indicator series would have.
bool CFpmrStrategy::EmaOk(const bool isLong)
  {
   if(!m_cfg.useEmaFilter)
      return(true);

   bool one=(m_emaFastHandle!=INVALID_HANDLE);
   bool two=(m_emaSlowHandle!=INVALID_HANDLE);

   double f[1], s[1];

   if(one && CopyBuffer(m_emaFastHandle,0,1,1,f)<1) return(true);
   if(two && CopyBuffer(m_emaSlowHandle,0,1,1,s)<1) return(true);

   if(one && two)
      return(isLong ? f[0]>s[0] : f[0]<s[0]);

   if(one)
      return(isLong ? m_barClose>f[0] : m_barClose<f[0]);

   if(two)
      return(isLong ? m_barClose>s[0] : m_barClose<s[0]);

   return(true);
  }

bool CFpmrStrategy::VwapOkLong(void)
  {
   return(!m_cfg.useVwapFilter || !m_vwap.HasValue() || m_barClose>m_vwap.Value());
  }

bool CFpmrStrategy::VwapOkShort(void)
  {
   return(!m_cfg.useVwapFilter || !m_vwap.HasValue() || m_barClose<m_vwap.Value());
  }

void CFpmrStrategy::RecordRejection(const int dir,const FpReject reason,const SizingResult &sizing)
  {
   m_lastRejectText=RejectionLabel(reason);

   if(!m_cfg.verboseLogging)
      return;

   string extra="";
   if(reason==FP_REJ_RISK_CAP || reason==FP_REJ_RISK)
      extra="  "+SizingDescribe(sizing,m_cfg.riskTargetUsd,m_cfg.riskToleranceUsd,
                                m_cfg.riskHardCapUsd,m_sym.lotStep);

   Print(StringFormat("%s  REJECT %s (%s displacement)%s",
                      TimeToString(m_barOpenTime,TIME_DATE|TIME_SECONDS),
                      m_lastRejectText,
                      dir<0 ? "bearish" : "bullish",
                      extra));
  }

#endif // FPMR_STRATEGY_FILTERS_MQH
//+------------------------------------------------------------------+
