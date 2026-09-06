//+------------------------------------------------------------------+
//| FPMR · Core · Symbol metadata                                    |
//|                                                                  |
//| Everything the strategy needs to know about the instrument, read  |
//| once at init. The NinjaTrader build took tick size and point      |
//| value from Instrument.MasterInstrument; the MT5 equivalents are   |
//| spread across SymbolInfoDouble/Integer, and two of them - the     |
//| broker's minimum stop distance and its freeze distance - have no  |
//| NinjaTrader counterpart at all but will silently reject orders if |
//| ignored.                                                          |
//|                                                                  |
//| Nothing here is hardcoded to a symbol.                            |
//+------------------------------------------------------------------+
#ifndef FPMR_SYMBOL_MQH
#define FPMR_SYMBOL_MQH

#include "FpmrEnums.mqh"
#include "RiskSizer.mqh"   // FpLotDigits - the volume step is what makes lots printable

struct FpSymbolMeta
  {
   string   name;
   int      digits;
   double   point;
   double   tickSize;
   double   tickValue;
   double   moneyPerPricePerLot; // account-currency value of a 1.0 price move on 1 lot
   double   lotMin;
   double   lotMax;
   double   lotStep;
   int      lotDigits;
   int      stopsLevelPoints;    // broker minimum SL/TP distance from market, in points
   int      freezeLevelPoints;   // distance inside which SL/TP may not be modified
   bool     hedgingAccount;
   string   error;
  };

bool FpLoadSymbolMeta(const string symbol,FpSymbolMeta &m)
  {
   m.name  = symbol;
   m.error = "";

   m.digits    = (int)SymbolInfoInteger(symbol,SYMBOL_DIGITS);
   m.point     = SymbolInfoDouble(symbol,SYMBOL_POINT);
   m.tickSize  = SymbolInfoDouble(symbol,SYMBOL_TRADE_TICK_SIZE);
   m.tickValue = SymbolInfoDouble(symbol,SYMBOL_TRADE_TICK_VALUE);

   m.lotMin  = SymbolInfoDouble(symbol,SYMBOL_VOLUME_MIN);
   m.lotMax  = SymbolInfoDouble(symbol,SYMBOL_VOLUME_MAX);
   m.lotStep = SymbolInfoDouble(symbol,SYMBOL_VOLUME_STEP);

   m.stopsLevelPoints  = (int)SymbolInfoInteger(symbol,SYMBOL_TRADE_STOPS_LEVEL);
   m.freezeLevelPoints = (int)SymbolInfoInteger(symbol,SYMBOL_TRADE_FREEZE_LEVEL);

   m.hedgingAccount = (ENUM_ACCOUNT_MARGIN_MODE)AccountInfoInteger(ACCOUNT_MARGIN_MODE)
                      == ACCOUNT_MARGIN_MODE_RETAIL_HEDGING;

   if(m.tickSize<=0.0)
     {
      // Some symbols report a zero tick size and mean "one point".
      m.tickSize=m.point;
     }

   if(m.tickSize<=0.0)
     {
      m.error="symbol tick size and point are both zero - the symbol is not tradable from this terminal";
      return(false);
     }

   if(m.tickValue<=0.0)
     {
      m.error="symbol tick value is zero - position sizing cannot convert points to money";
      return(false);
     }

   if(m.lotStep<=0.0)
      m.lotStep=0.01;

   m.lotDigits           = FpLotDigits(m.lotStep);
   m.moneyPerPricePerLot = m.tickValue/m.tickSize;

   return(true);
  }

//--- Rounds a price to the symbol's tradable increment.
//    The equivalent of Instrument.MasterInstrument.RoundToTickSize.
double FpRoundToTick(const double price,const FpSymbolMeta &m)
  {
   if(m.tickSize<=0.0)
      return(NormalizeDouble(price,m.digits));

   return(NormalizeDouble(MathRound(price/m.tickSize)*m.tickSize,m.digits));
  }

string FpSymbolDescribe(const FpSymbolMeta &m)
  {
   return(StringFormat("%s | digits %d | tick %s | tick value %.5f | 1.0 price move = %.2f per lot | "
                       "lots %s..%s step %s | stops level %d pts | freeze %d pts | %s account",
                       m.name, m.digits,
                       DoubleToString(m.tickSize,m.digits),
                       m.tickValue, m.moneyPerPricePerLot,
                       DoubleToString(m.lotMin,m.lotDigits),
                       DoubleToString(m.lotMax,m.lotDigits),
                       DoubleToString(m.lotStep,m.lotDigits),
                       m.stopsLevelPoints, m.freezeLevelPoints,
                       m.hedgingAccount ? "HEDGING" : "NETTING"));
  }

#endif // FPMR_SYMBOL_MQH
//+------------------------------------------------------------------+
