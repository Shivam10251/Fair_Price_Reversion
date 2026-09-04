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
