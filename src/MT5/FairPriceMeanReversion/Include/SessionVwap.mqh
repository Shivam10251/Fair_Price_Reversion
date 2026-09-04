//+------------------------------------------------------------------+
//| FPMR · Core · SessionVwap                                        |
//|                                                                  |
//| Session-anchored VWAP, computed here rather than borrowed from an |
//| indicator so the anchor matches the Pine version exactly: it      |
//| resets on session start and on a new calendar day, both evaluated |
//| in the SESSION time zone.                                         |
//|                                                                  |
//| VOLUME ON MT5. Most FX and CFD feeds publish no real volume, only |
//| tick counts. The EA passes whichever the symbol actually carries  |
//| (see the volume mode input); on a tick-count feed this is a       |
//| tick-weighted average price, not a true VWAP, which is the        |
//| honest best available and is stated in the startup log. A bar     |
//| with no volume contributes nothing, and a session that never sees |
//| volume leaves the value absent - the filter then passes, the same |
//| behaviour as Pine's na(vwapVal) guard.                            |
//+------------------------------------------------------------------+
#ifndef FPMR_SESSION_VWAP_MQH
#define FPMR_SESSION_VWAP_MQH

#include "FpmrEnums.mqh"

class CSessionVwap
  {
private:
   double            m_cumulativePriceVolume;
   double            m_cumulativeVolume;
   double            m_value;

public:
                     CSessionVwap(void) { Reset(); }

   double            Value(void)    const { return(m_value); }
   bool              HasValue(void) const { return(!FpIsNa(m_value)); }

   void              Reset(void)
     {
      m_cumulativePriceVolume = 0.0;
      m_cumulativeVolume      = 0.0;
      m_value                 = FPMR_NA;
     }

   //--- Call once per primary bar. `anchor` restarts the accumulation.
   void              OnBar(const double high,const double low,const double close,
                           const double volume,const bool anchor)
     {
      if(anchor)
        {
         m_cumulativePriceVolume = 0.0;
         m_cumulativeVolume      = 0.0;
        }

      double typical=(high+low+close)/3.0;

      if(volume>0.0)
        {
         m_cumulativePriceVolume += typical*volume;
         m_cumulativeVolume      += volume;
        }

      m_value=(m_cumulativeVolume>0.0 ? m_cumulativePriceVolume/m_cumulativeVolume : FPMR_NA);
     }
  };

#endif // FPMR_SESSION_VWAP_MQH
//+------------------------------------------------------------------+
