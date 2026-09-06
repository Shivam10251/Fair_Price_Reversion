//+------------------------------------------------------------------+
//| FPMR · News · Shared types                                       |
//|                                                                  |
//| The news system itself (calendar loading, surprise classification |
//| and the post-news consolidation detector) is phase 6 of the port  |
//| and is not implemented yet. These types exist now because the     |
//| Fair Price engine's decision tree is shaped by them: leaving them |
//| out would mean rewriting that engine later instead of filling in  |
//| a resolver behind an interface that already fits.                 |
//|                                                                  |
//| Structs carry an explicit `valid` flag because MQL5 passes them   |
//| by value and has no null reference to test, where the C# build    |
//| used a null NewsFairPrice to mean "nothing captured".             |
//+------------------------------------------------------------------+
#ifndef FPMR_NEWS_TYPES_MQH
#define FPMR_NEWS_TYPES_MQH

#include "FpmrEnums.mqh"

//--- One release, as parsed from the calendar --------------------------------
struct NewsEvent
  {
   bool          valid;
   datetime      timeTz;        // release instant, in the session zone
   string        title;
   string        currency;
   FpNewsImpact  impact;
   bool          hasForecast;
   double        forecast;
   bool          hasActual;
   double        actual;
  };

void NewsEventClear(NewsEvent &e)
  {
   e.valid       = false;
   e.timeTz      = 0;
   e.title       = "";
   e.currency    = "";
   e.impact      = FP_IMPACT_UNKNOWN;
   e.hasForecast = false;
   e.forecast    = 0.0;
   e.hasActual   = false;
   e.actual      = 0.0;
  }

string NewsEventText(const NewsEvent &e)
  {
   if(!e.valid)
      return("-");

   return(StringFormat("%s %s (%s)",
                       TimeToString(e.timeTz,TIME_DATE|TIME_MINUTES),
                       e.title,
                       e.currency));
  }

//--- A Fair Price the news branch wants used, and for which session ----------
struct NewsFairPrice
  {
   bool        valid;
   double      fairPrice;
   int         sessionIndex;
   datetime    sessionOpenTz;
   FpNewsBias  bias;
   NewsEvent   source;
  };

void NewsFairPriceClear(NewsFairPrice &c)
  {
   c.valid         = false;
   c.fairPrice     = FPMR_NA;
   c.sessionIndex  = 0;
   c.sessionOpenTz = 0;
   c.bias          = FP_BIAS_REVERSION;
   NewsEventClear(c.source);
  }

#endif // FPMR_NEWS_TYPES_MQH
//+------------------------------------------------------------------+
