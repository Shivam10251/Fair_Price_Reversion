//+------------------------------------------------------------------+
//| FPMR · Core · RiskSizer                                          |
//|                                                                  |
//| Sizes each trade so the dollar risk lands near RiskTargetUSD      |
//| without ever breaching RiskHardCapUSD.                            |
//|                                                                  |
//| CONTRACTS BECOME LOTS. NinjaTrader sized in whole contracts with  |
//| a fixed PointValue per contract. MT5 sizes in lots, which are     |
//| fractional and broker-constrained, and the money value of a price |
//| move comes from the symbol rather than from a constant:           |
//|                                                                  |
//|   moneyPerPricePerLot = SYMBOL_TRADE_TICK_VALUE                  |
//|                         / SYMBOL_TRADE_TICK_SIZE                 |
//|                                                                  |
//| so the algorithm is the same shape, stepping by SYMBOL_VOLUME_STEP|
//| where the C# build stepped by one contract:                       |
//|                                                                  |
//|   riskPoints  = |entry - stop|                                    |
//|   riskPerLot  = riskPoints * moneyPerPricePerLot                  |
//|   lots        = round(RiskTargetUSD / riskPerLot) to volume step  |
//|   lots        = clamp(lots, volumeMin, min(volumeMax, maxLots))   |
//|   while (lots * riskPerLot > hardCap && lots > volumeMin) step--  |
//|   if (lots * riskPerLot > hardCap) -> skip the trade              |
//|                                                                  |
//| Nothing here assumes a symbol: every constraint is read from      |
//| SymbolInfoDouble at run time.                                     |
//+------------------------------------------------------------------+
#ifndef FPMR_RISK_SIZER_MQH
#define FPMR_RISK_SIZER_MQH

#include "FpmrEnums.mqh"

struct SizingResult
  {
   bool     accepted;
   double   lots;
   double   riskPoints;
   double   riskPerLot;
   double   resultingRisk;
   bool     withinTolerance;
   bool     steppedDown;    // the hard cap forced the size below the target size
   bool     clampedToMax;   // the max-lots ceiling capped it
   string   skipReason;
  };

void SizingResultClear(SizingResult &r)
  {
   r.accepted        = false;
   r.lots            = 0.0;
   r.riskPoints      = 0.0;
   r.riskPerLot      = 0.0;
   r.resultingRisk   = 0.0;
   r.withinTolerance = false;
   r.steppedDown     = false;
   r.clampedToMax    = false;
   r.skipReason      = "";
  }

//--- Decimal places implied by a volume step, so lots print and normalise cleanly.
int FpLotDigits(const double lotStep)
  {
   if(lotStep<=0.0)
      return(2);

   int digits=0;
   double s=lotStep;

   while(digits<8 && MathAbs(s-MathRound(s))>1e-9)
     {
      s*=10.0;
      digits++;
     }

   return(digits);
  }

double FpNormaliseLots(const double lots,const double lotStep,const double lotMin,const double lotMax)
  {
   if(lotStep<=0.0)
      return(lots);

   double snapped=MathRound(lots/lotStep)*lotStep;
   snapped=NormalizeDouble(snapped,FpLotDigits(lotStep));

   if(snapped<lotMin) snapped=lotMin;
   if(lotMax>0.0 && snapped>lotMax) snapped=lotMax;

   return(NormalizeDouble(snapped,FpLotDigits(lotStep)));
  }

string SizingDescribe(const SizingResult &r,const double target,const double tolerance,
                      const double hardCap,const double lotStep)
  {
   int    lotDigits=FpLotDigits(lotStep);
   string lots=(r.accepted ? DoubleToString(r.lots,lotDigits) : "0");

   string verdict;
   if(!r.accepted)                 verdict="SKIPPED";
   else if(r.withinTolerance)      verdict="ON TARGET";
   else                            verdict="OUT OF BAND";

   return(StringFormat("stop %.2f pts | %.2f/lot | lots %s | risk %.2f | target %.0f +/- %.0f -> %s%s%s%s",
                       r.riskPoints, r.riskPerLot, lots, r.resultingRisk, target, tolerance, verdict,
                       r.steppedDown  ? StringFormat(" | stepped down for %.0f cap",hardCap) : "",
                       r.clampedToMax ? " | clamped to max lots" : "",
                       r.accepted     ? "" : " | "+r.skipReason));
  }

//+------------------------------------------------------------------+
//| Sizes one trade.                                                 |
//|                                                                  |
//| moneyPerPricePerLot  account-currency value of a 1.0 price move   |
//|                      on one lot (tickValue / tickSize)            |
//+------------------------------------------------------------------+
void RiskSizerSize(const double entryPrice,const double stopPrice,
                   const double moneyPerPricePerLot,
                   const double riskTargetUsd,const double riskToleranceUsd,
                   const double riskHardCapUsd,
                   const double lotMin,const double lotMax,const double lotStep,
                   const double maxLotsInput,
                   SizingResult &r)
  {
   SizingResultClear(r);

   r.riskPoints=MathAbs(entryPrice-stopPrice);

   if(r.riskPoints<=0.0 || !MathIsValidNumber(r.riskPoints))
     {
      r.skipReason="stop distance is zero";
      return;
     }

   if(moneyPerPricePerLot<=0.0)
     {
      r.skipReason="symbol tick value / tick size is not positive";
      return;
     }

   r.riskPerLot=r.riskPoints*moneyPerPricePerLot;

   double ceiling=lotMax;
   if(maxLotsInput>0.0 && (ceiling<=0.0 || maxLotsInput<ceiling))
      ceiling=maxLotsInput;

   // Nearest whole volume step to the target, then bounded.
   double raw=riskTargetUsd/r.riskPerLot;
   double lots=FpNormaliseLots(raw,lotStep,lotMin,ceiling);

   if(ceiling>0.0 && lots>=ceiling-lotStep*0.5 && raw>ceiling)
      r.clampedToMax=true;

   // Step down until the hard cap is respected.
   int guard=0;
   while(lots*r.riskPerLot>riskHardCapUsd && lots>lotMin && guard<10000)
     {
      lots=NormalizeDouble(lots-lotStep,FpLotDigits(lotStep));
      r.steppedDown=true;
      guard++;
     }

   if(lots<lotMin)
      lots=lotMin;

   r.lots          = lots;
   r.resultingRisk = lots*r.riskPerLot;

   // Even the minimum tradable size breaches the cap -> no trade.
   if(r.resultingRisk>riskHardCapUsd)
     {
      r.accepted=false;
      r.skipReason=StringFormat("%.2f lots (the broker minimum) risks %.2f which exceeds the %.2f hard cap (stop %.2f pts)",
                                lots, r.resultingRisk, riskHardCapUsd, r.riskPoints);
      return;
     }

   r.accepted        = true;
   r.withinTolerance = (MathAbs(r.resultingRisk-riskTargetUsd)<=riskToleranceUsd);
  }

#endif // FPMR_RISK_SIZER_MQH
//+------------------------------------------------------------------+
