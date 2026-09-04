//+------------------------------------------------------------------+
//| FPMR · Core · PivotDetector                                      |
//|                                                                  |
//| Confirmed swing detection, equivalent to Pine's ta.pivothigh /    |
//| ta.pivotlow. A pivot is only ever reported RightBars after the    |
//| bar that formed it, which is the whole reason the strategy cannot |
//| repaint.                                                          |
//|                                                                  |
//| The C# build took bars-ago accessor delegates; MQL5 has no        |
//| closures, so the caller passes AS-SERIES arrays instead - index 0 |
//| is the current bar, which is the same indexing NinjaTrader's      |
//| High[i] used.                                                     |
//|                                                                  |
//| TIE HANDLING: the pivot must be a STRICT extreme on both sides.   |
//| An equal high inside the window disqualifies it. This matches     |
//| Pine, where a flat double top produces no pivot until one side is |
//| exceeded.                                                         |
//+------------------------------------------------------------------+
#ifndef FPMR_PIVOT_DETECTOR_MQH
#define FPMR_PIVOT_DETECTOR_MQH

#include "FpmrEnums.mqh"

struct PivotResult
  {
   bool     found;
   double   price;
   int      barsAgo;   // bars ago of the bar that formed the pivot
  };

void PivotResultClear(PivotResult &r)
  {
   r.found   = false;
   r.price   = FPMR_NA;
   r.barsAgo = 0;
  }

//--- Bars of history required before any pivot can be evaluated.
int PivotRequiredBars(const int leftBars,const int rightBars)
  {
   return(leftBars+rightBars+1);
  }

//+------------------------------------------------------------------+
//| Tests whether the bar sitting rightBars back is a pivot high.     |
//| highs[] must be AS-SERIES (index 0 = current bar).                |
//+------------------------------------------------------------------+
void PivotHigh(const double &highs[],const int leftBars,const int rightBars,
               const int barsAvailable,PivotResult &out)
  {
   PivotResultClear(out);

   if(leftBars<1 || rightBars<1)
      return;
   if(barsAvailable<PivotRequiredBars(leftBars,rightBars))
      return;
   if(ArraySize(highs)<PivotRequiredBars(leftBars,rightBars))
      return;

   double candidate=highs[rightBars];
   if(!MathIsValidNumber(candidate))
      return;

   for(int i=1;i<=leftBars;i++)
      if(highs[rightBars+i]>=candidate)
         return;

   for(int i=1;i<=rightBars;i++)
      if(highs[rightBars-i]>=candidate)
         return;

   out.found   = true;
   out.price   = candidate;
   out.barsAgo = rightBars;
  }

//+------------------------------------------------------------------+
//| Mirror of PivotHigh for swing lows.                              |
//+------------------------------------------------------------------+
void PivotLow(const double &lows[],const int leftBars,const int rightBars,
              const int barsAvailable,PivotResult &out)
  {
   PivotResultClear(out);

   if(leftBars<1 || rightBars<1)
      return;
   if(barsAvailable<PivotRequiredBars(leftBars,rightBars))
      return;
   if(ArraySize(lows)<PivotRequiredBars(leftBars,rightBars))
      return;

   double candidate=lows[rightBars];
   if(!MathIsValidNumber(candidate))
      return;

   for(int i=1;i<=leftBars;i++)
      if(lows[rightBars+i]<=candidate)
         return;

   for(int i=1;i<=rightBars;i++)
      if(lows[rightBars-i]<=candidate)
         return;

   out.found   = true;
   out.price   = candidate;
   out.barsAgo = rightBars;
  }

#endif // FPMR_PIVOT_DETECTOR_MQH
//+------------------------------------------------------------------+
