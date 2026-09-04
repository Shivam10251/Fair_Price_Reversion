//+------------------------------------------------------------------+
//| FPMR · Core · SetupBands                                         |
//|                                                                  |
//| The distance-band setup model.                                    |
//|                                                                  |
//| An entry's distance from Fair Price, measured as a PERCENTAGE of  |
//| Fair Price, decides both whether the trade is taken and how its   |
//| take-profit is chosen:                                            |
//|                                                                  |
//|     |entry - FairPrice| / FairPrice * 100  =  dist%              |
//|                                                                  |
//|        dist% <= zone%           -> None   : inside the zone      |
//|        zone% <  dist% <= band1% -> Near   : TP at the R:R multiple|
//|        band1% < dist% <= band2% -> Far    : TP at Fair Price      |
//|        dist% >  band2%          -> Beyond : too far, no trade     |
//|                                                                  |
//| The three percentages are validated at load time to be strictly   |
//| ordered (zone% < band1% < band2%), so the four cases never        |
//| overlap.                                                          |
//+------------------------------------------------------------------+
#ifndef FPMR_SETUP_BANDS_MQH
#define FPMR_SETUP_BANDS_MQH

#include "FpmrEnums.mqh"

//+------------------------------------------------------------------+
//| Classifies an entry price against Fair Price using the three      |
//| percentage thresholds. Returns FP_BAND_NONE when Fair Price is    |
//| not available or the percentages are unusable, so no band-based   |
//| trade is taken.                                                   |
//+------------------------------------------------------------------+
FpSetupBand SetupBandsClassify(const double entry,const double fairPrice,const bool hasFairPrice,
                               const double zonePercent,const double band1Percent,const double band2Percent)
  {
   if(!hasFairPrice || FpIsNa(fairPrice) || MathAbs(fairPrice)<=DBL_EPSILON)
      return(FP_BAND_NONE);

   double distPercent=MathAbs(entry-fairPrice)/MathAbs(fairPrice)*100.0;

   if(distPercent<=zonePercent)  return(FP_BAND_NONE);
   if(distPercent<=band1Percent) return(FP_BAND_NEAR);
   if(distPercent<=band2Percent) return(FP_BAND_FAR);

   return(FP_BAND_BEYOND);
  }

#endif // FPMR_SETUP_BANDS_MQH
//+------------------------------------------------------------------+
