//+------------------------------------------------------------------+
//| FPMR · Core · TimeZoneRegistry (MT5)                             |
//|                                                                  |
//| WHY THIS EXISTS                                                  |
//| The NinjaTrader build leaned on .NET TimeZoneInfo, which reads    |
//| the OS timezone database and handles DST for free. MQL5 has no    |
//| equivalent: it knows the broker's server time, the terminal's     |
//| local time and GMT, and nothing about named zones. So the DST     |
//| rules the strategy depends on are implemented here, for the same  |
//| zone list the NT8 TimeZoneRegistry offers, and the same string    |
//| ids are accepted so one configuration text drives both platforms. |
//|                                                                  |
//| TIME MODEL                                                       |
//| A "zone time" is a naive wall-clock encoded as a datetime, i.e.   |
//| utc + utcOffset(zone, utc). That is exactly what the C# build did |
//| with DateTimeKind.Unspecified, so the arithmetic downstream is    |
//| unchanged. Convert() round-trips through UTC.                     |
//|                                                                  |
//| SERVER TIME                                                      |
//| Bar timestamps are in BROKER SERVER time, which is itself a zone  |
//| with its own DST rule and no name. It is therefore configured by  |
//| the user (base GMT offset + which DST calendar the server         |
//| follows) rather than guessed, because a backtest has to convert   |
//| bars from years ago and TimeGMT()-TimeCurrent() only describes    |
//| the offset RIGHT NOW.                                            |
//+------------------------------------------------------------------+
#ifndef FPMR_TIMEZONE_MQH
#define FPMR_TIMEZONE_MQH

#include "FpmrEnums.mqh"

//--- Which seasonal-clock calendar a zone follows -----------------------------
enum FpDstRule
  {
   FP_DST_NONE = 0, // No DST - the offset is fixed all year
   FP_DST_US   = 1, // US - 2nd Sun Mar 02:00 local to 1st Sun Nov 02:00 local
   FP_DST_EU   = 2, // EU - last Sun Mar 01:00 UTC to last Sun Oct 01:00 UTC
   FP_DST_AU   = 3, // Australia (SE) - 1st Sun Oct to 1st Sun Apr
   FP_DST_NZ   = 4  // New Zealand - last Sun Sep to 1st Sun Apr
  };

//--- The zone list offered in the inputs, mirroring TimeZoneRegistry.Options --
enum FpTzId
  {
   FP_TZ_UTC = 0,           // UTC
   FP_TZ_NEW_YORK,          // America/New_York
   FP_TZ_CHICAGO,           // America/Chicago
   FP_TZ_DENVER,            // America/Denver
   FP_TZ_LOS_ANGELES,       // America/Los_Angeles
   FP_TZ_LONDON,            // Europe/London
   FP_TZ_BERLIN,            // Europe/Berlin
   FP_TZ_PARIS,             // Europe/Paris
   FP_TZ_ZURICH,            // Europe/Zurich
   FP_TZ_KOLKATA,           // Asia/Kolkata (IST)
   FP_TZ_DUBAI,             // Asia/Dubai
   FP_TZ_SINGAPORE,         // Asia/Singapore
   FP_TZ_HONG_KONG,         // Asia/Hong_Kong
   FP_TZ_SHANGHAI,          // Asia/Shanghai
   FP_TZ_TOKYO,             // Asia/Tokyo
   FP_TZ_SYDNEY,            // Australia/Sydney
   FP_TZ_AUCKLAND,          // Pacific/Auckland
   FP_TZ_INVALID = -1       // unresolvable
  };

//--- The DST calendar a broker's server clock follows -------------------------
enum FpServerDst
  {
   FP_SRVDST_NONE = 0, // Server clock never shifts (fixed GMT offset)
   FP_SRVDST_US   = 1, // Server follows the US DST calendar (most FX brokers)
   FP_SRVDST_EU   = 2  // Server follows the EU DST calendar
  };

//--- The DST calendar every session window is pinned to (DstAnchor) -----------
#define FPMR_ANCHOR_TZ_ID   "America/New_York"
#define FPMR_ANCHOR_TZ      FP_TZ_NEW_YORK

//+------------------------------------------------------------------+
//| Calendar helpers                                                 |
//+------------------------------------------------------------------+
//--- Midnight of the day a naive zone datetime falls on. Zone times are naive
//    wall clocks encoded as epochs, so flooring to 86400 is the day boundary.
datetime FpDateOnly(const datetime t)
  {
   return((datetime)(((long)t/86400)*86400));
  }

//--- Day key (yyyymmdd) of a naive zone datetime, for day-change detection.
int FpDayKey(const datetime t)
  {
   MqlDateTime s;
   TimeToStruct(t,s);
   return(s.year*10000+s.mon*100+s.day);
  }


//--- UTC instant for a naive Y/M/D H:M:S ---------------------------------------
datetime FpMakeUtc(const int year,const int month,const int day,
                   const int hour=0,const int minute=0,const int second=0)
  {
   MqlDateTime s;
   s.year=year; s.mon=month; s.day=day;
   s.hour=hour; s.min=minute; s.sec=second;
   s.day_of_week=0; s.day_of_year=0;
   return(StructToTime(s));
  }

//--- Day of week (0=Sunday) for a naive date ----------------------------------
int FpDayOfWeek(const int year,const int month,const int day)
  {
   MqlDateTime s;
   TimeToStruct(FpMakeUtc(year,month,day),s);
   return(s.day_of_week);
  }

//--- Date of the nth given weekday in a month (nth is 1-based) ----------------
int FpNthWeekdayDay(const int year,const int month,const int weekday,const int nth)
  {
   int firstDow=FpDayOfWeek(year,month,1);
   int offset=(weekday-firstDow+7)%7;
   return(1+offset+(nth-1)*7);
  }

//--- Date of the LAST given weekday in a month --------------------------------
int FpLastWeekdayDay(const int year,const int month,const int weekday)
  {
   static int len[13]={0,31,28,31,30,31,30,31,31,30,31,30,31};
   int days=len[month];
   if(month==2 && ((year%4==0 && year%100!=0) || year%400==0))
      days=29;

   int lastDow=FpDayOfWeek(year,month,days);
   return(days-((lastDow-weekday+7)%7));
  }

//+------------------------------------------------------------------+
//| Zone table                                                       |
//+------------------------------------------------------------------+

//--- Standard (winter) offset from UTC, in seconds ----------------------------
int FpTzBaseOffset(const FpTzId z)
  {
   switch(z)
     {
      case FP_TZ_UTC:          return(0);
      case FP_TZ_NEW_YORK:     return(-5*3600);
      case FP_TZ_CHICAGO:      return(-6*3600);
      case FP_TZ_DENVER:       return(-7*3600);
      case FP_TZ_LOS_ANGELES:  return(-8*3600);
      case FP_TZ_LONDON:       return(0);
      case FP_TZ_BERLIN:       return(1*3600);
      case FP_TZ_PARIS:        return(1*3600);
      case FP_TZ_ZURICH:       return(1*3600);
      case FP_TZ_KOLKATA:      return(5*3600+1800);
      case FP_TZ_DUBAI:        return(4*3600);
      case FP_TZ_SINGAPORE:    return(8*3600);
      case FP_TZ_HONG_KONG:    return(8*3600);
      case FP_TZ_SHANGHAI:     return(8*3600);
      case FP_TZ_TOKYO:        return(9*3600);
      case FP_TZ_SYDNEY:       return(10*3600);
      case FP_TZ_AUCKLAND:     return(12*3600);
      default:                 return(0);
     }
  }

FpDstRule FpTzDstRule(const FpTzId z)
  {
   switch(z)
     {
      case FP_TZ_NEW_YORK:
      case FP_TZ_CHICAGO:
      case FP_TZ_DENVER:
      case FP_TZ_LOS_ANGELES:  return(FP_DST_US);

      case FP_TZ_LONDON:
      case FP_TZ_BERLIN:
      case FP_TZ_PARIS:
      case FP_TZ_ZURICH:       return(FP_DST_EU);

      case FP_TZ_SYDNEY:       return(FP_DST_AU);
      case FP_TZ_AUCKLAND:     return(FP_DST_NZ);

      default:                 return(FP_DST_NONE);
     }
  }

string FpTzName(const FpTzId z)
  {
   switch(z)
     {
      case FP_TZ_UTC:          return("UTC");
      case FP_TZ_NEW_YORK:     return("America/New_York");
      case FP_TZ_CHICAGO:      return("America/Chicago");
      case FP_TZ_DENVER:       return("America/Denver");
      case FP_TZ_LOS_ANGELES:  return("America/Los_Angeles");
      case FP_TZ_LONDON:       return("Europe/London");
      case FP_TZ_BERLIN:       return("Europe/Berlin");
      case FP_TZ_PARIS:        return("Europe/Paris");
      case FP_TZ_ZURICH:       return("Europe/Zurich");
      case FP_TZ_KOLKATA:      return("Asia/Kolkata");
      case FP_TZ_DUBAI:        return("Asia/Dubai");
      case FP_TZ_SINGAPORE:    return("Asia/Singapore");
      case FP_TZ_HONG_KONG:    return("Asia/Hong_Kong");
      case FP_TZ_SHANGHAI:     return("Asia/Shanghai");
      case FP_TZ_TOKYO:        return("Asia/Tokyo");
      case FP_TZ_SYDNEY:       return("Australia/Sydney");
      case FP_TZ_AUCKLAND:     return("Pacific/Auckland");
      default:                 return("<invalid>");
     }
  }

//--- Accepts the same id strings the NT8 registry does ------------------------
FpTzId FpTzResolve(const string rawId)
  {
   string id=rawId;
   StringTrimLeft(id);
   StringTrimRight(id);
   StringToUpper(id);

   if(id=="" )                                           return(FP_TZ_INVALID);
   if(id=="UTC" || id=="ETC/UTC" || id=="GMT")           return(FP_TZ_UTC);
   if(id=="AMERICA/NEW_YORK" || id=="EASTERN STANDARD TIME" || id=="EST" || id=="ET")
      return(FP_TZ_NEW_YORK);
   if(id=="AMERICA/CHICAGO" || id=="CENTRAL STANDARD TIME" || id=="CST")
      return(FP_TZ_CHICAGO);
   if(id=="AMERICA/DENVER" || id=="MOUNTAIN STANDARD TIME")
      return(FP_TZ_DENVER);
   if(id=="AMERICA/LOS_ANGELES" || id=="PACIFIC STANDARD TIME" || id=="PST")
      return(FP_TZ_LOS_ANGELES);
   if(id=="EUROPE/LONDON" || id=="GMT STANDARD TIME")    return(FP_TZ_LONDON);
   if(id=="EUROPE/BERLIN" || id=="W. EUROPE STANDARD TIME")
      return(FP_TZ_BERLIN);
   if(id=="EUROPE/PARIS" || id=="ROMANCE STANDARD TIME") return(FP_TZ_PARIS);
   if(id=="EUROPE/ZURICH")                              return(FP_TZ_ZURICH);
   if(id=="ASIA/KOLKATA" || id=="ASIA/CALCUTTA" || id=="IST" || id=="INDIA STANDARD TIME")
      return(FP_TZ_KOLKATA);
   if(id=="ASIA/DUBAI" || id=="ARABIAN STANDARD TIME")   return(FP_TZ_DUBAI);
   if(id=="ASIA/SINGAPORE" || id=="SINGAPORE STANDARD TIME")
      return(FP_TZ_SINGAPORE);
   if(id=="ASIA/HONG_KONG")                             return(FP_TZ_HONG_KONG);
   if(id=="ASIA/SHANGHAI" || id=="CHINA STANDARD TIME") return(FP_TZ_SHANGHAI);
   if(id=="ASIA/TOKYO" || id=="TOKYO STANDARD TIME")    return(FP_TZ_TOKYO);
   if(id=="AUSTRALIA/SYDNEY" || id=="AUS EASTERN STANDARD TIME")
      return(FP_TZ_SYDNEY);
   if(id=="PACIFIC/AUCKLAND" || id=="NEW ZEALAND STANDARD TIME")
      return(FP_TZ_AUCKLAND);

   return(FP_TZ_INVALID);
  }

//+------------------------------------------------------------------+
//| DST evaluation                                                   |
//|                                                                  |
//| Answered against a UTC instant, so the ambiguous and invalid      |
//| wall-clock hours around a transition never have to be resolved.   |
//+------------------------------------------------------------------+
bool FpDstActive(const FpDstRule rule,const int baseOffset,const datetime utc)
  {
   if(rule==FP_DST_NONE)
      return(false);

   MqlDateTime s;
   TimeToStruct(utc,s);
   int y=s.year;

   if(rule==FP_DST_US)
     {
      // 02:00 local standard on the 2nd Sunday of March,
      // to 02:00 local daylight (01:00 standard) on the 1st Sunday of November.
      datetime start=FpMakeUtc(y,3,FpNthWeekdayDay(y,3,0,2),2,0,0)-baseOffset;
      datetime end  =FpMakeUtc(y,11,FpNthWeekdayDay(y,11,0,1),2,0,0)-baseOffset-3600;
      return(utc>=start && utc<end);
     }

   if(rule==FP_DST_EU)
     {
      // 01:00 UTC on the last Sunday of March, to 01:00 UTC on the last Sunday
      // of October - the EU rule is stated in UTC for every member state.
      datetime start=FpMakeUtc(y,3,FpLastWeekdayDay(y,3,0),1,0,0);
      datetime end  =FpMakeUtc(y,10,FpLastWeekdayDay(y,10,0),1,0,0);
      return(utc>=start && utc<end);
     }

   if(rule==FP_DST_AU)
     {
      // Southern hemisphere: DST spans the new year, so the test is inverted.
      // 02:00 local standard, 1st Sunday of October, to 03:00 local daylight
      // (02:00 standard), 1st Sunday of April.
      datetime start=FpMakeUtc(y,10,FpNthWeekdayDay(y,10,0,1),2,0,0)-baseOffset;
      datetime end  =FpMakeUtc(y,4,FpNthWeekdayDay(y,4,0,1),2,0,0)-baseOffset;
      return(utc>=start || utc<end);
     }

   if(rule==FP_DST_NZ)
     {
      // 02:00 local standard, last Sunday of September, to 03:00 local daylight
      // (02:00 standard), 1st Sunday of April.
      datetime start=FpMakeUtc(y,9,FpLastWeekdayDay(y,9,0),2,0,0)-baseOffset;
      datetime end  =FpMakeUtc(y,4,FpNthWeekdayDay(y,4,0,1),2,0,0)-baseOffset;
      return(utc>=start || utc<end);
     }

   return(false);
  }

//--- Offset from UTC, in seconds, for a zone at a UTC instant -----------------
int FpTzOffsetAtUtc(const FpTzId z,const datetime utc)
  {
   int base=FpTzBaseOffset(z);
   return(FpDstActive(FpTzDstRule(z),base,utc) ? base+3600 : base);
  }

//--- Offset from UTC, in seconds, for an explicit rule + base -----------------
int FpRuleOffsetAtUtc(const FpDstRule rule,const int baseOffset,const datetime utc)
  {
   return(FpDstActive(rule,baseOffset,utc) ? baseOffset+3600 : baseOffset);
  }

//--- UTC -> zone wall clock ---------------------------------------------------
datetime FpTzFromUtc(const FpTzId z,const datetime utc)
  {
   return(utc+FpTzOffsetAtUtc(z,utc));
  }

//--- Zone wall clock -> UTC ---------------------------------------------------
//  The offset depends on the UTC instant we are solving for, so the standard
//  offset seeds a guess and one refinement settles it. Inside the one hour
//  that a fall-back repeats, the FIRST (daylight) reading is returned, which is
//  the same choice .NET makes for an Unspecified DateTime.
datetime FpTzToUtc(const FpTzId z,const datetime zoneTime)
  {
   int guess=FpTzBaseOffset(z);
   datetime utc=zoneTime-guess;

   for(int i=0;i<2;i++)
     {
      int refined=FpTzOffsetAtUtc(z,utc);
      datetime next=zoneTime-refined;
      if(next==utc)
         break;
      utc=next;
     }

   return(utc);
  }

//--- Zone -> zone, the direct equivalent of TimeZoneRegistry.Convert ----------
datetime FpTzConvert(const datetime t,const FpTzId from,const FpTzId to)
  {
   if(from==to || from==FP_TZ_INVALID || to==FP_TZ_INVALID)
      return(t);

   return(FpTzFromUtc(to,FpTzToUtc(from,t)));
  }

//+------------------------------------------------------------------+
//| Broker server clock                                              |
//|                                                                  |
//| Bar timestamps arrive in server time. The server is treated as an |
//| unnamed zone: a base GMT offset plus whichever DST calendar the   |
//| broker follows, both supplied by the user. FpServerToUtc is the   |
//| single door every bar timestamp passes through.                   |
//+------------------------------------------------------------------+
class CFpServerClock
  {
private:
   int         m_baseOffset;   // standard (winter) offset from UTC, seconds
   FpDstRule   m_rule;

public:
                     CFpServerClock(void) : m_baseOffset(0), m_rule(FP_DST_NONE) {}

   void              Configure(const double baseOffsetHours,const FpServerDst dst)
     {
      m_baseOffset=(int)MathRound(baseOffsetHours*3600.0);

      if(dst==FP_SRVDST_US)      m_rule=FP_DST_US;
      else if(dst==FP_SRVDST_EU) m_rule=FP_DST_EU;
      else                       m_rule=FP_DST_NONE;
     }

   int               BaseOffset(void) const { return(m_baseOffset); }

   //--- Server wall clock -> UTC, resolved the same way FpTzToUtc does.
   datetime          ToUtc(const datetime serverTime) const
     {
      datetime utc=serverTime-m_baseOffset;

      for(int i=0;i<2;i++)
        {
         int refined=FpRuleOffsetAtUtc(m_rule,m_baseOffset,utc);
         datetime next=serverTime-refined;
         if(next==utc)
            break;
         utc=next;
        }

      return(utc);
     }

   datetime          FromUtc(const datetime utc) const
     {
      return(utc+FpRuleOffsetAtUtc(m_rule,m_baseOffset,utc));
     }

   //--- The conversion the whole strategy runs on: a bar time in the target zone.
   datetime          ToZone(const datetime serverTime,const FpTzId z) const
     {
      return(FpTzFromUtc(z,ToUtc(serverTime)));
     }

   //--- The inverse. Chart objects are positioned in SERVER time, because that is
   //    what MT5 stamps bars with, but the strategy reasons in the session zone -
   //    so a session's open and close have to come back the other way.
   datetime          FromZone(const datetime zoneTime,const FpTzId z) const
     {
      return(FromUtc(FpTzToUtc(z,zoneTime)));
     }

   //--- Offset from UTC right now, for the configuration log.
   string            Describe(void) const
     {
      int now=FpRuleOffsetAtUtc(m_rule,m_baseOffset,TimeGMT());
      string dst=(m_rule==FP_DST_US ? "US DST" : (m_rule==FP_DST_EU ? "EU DST" : "no DST"));
      return(StringFormat("GMT%+.1f standard, %s, currently GMT%+.1f",
                          m_baseOffset/3600.0, dst, now/3600.0));
     }
  };

#endif // FPMR_TIMEZONE_MQH
//+------------------------------------------------------------------+
