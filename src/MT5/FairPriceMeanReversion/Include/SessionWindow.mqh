//+------------------------------------------------------------------+
//| FPMR · Core · SessionWindow                                      |
//|                                                                  |
//| One "HHMM-HHMM" trading window, evaluated purely on time-of-day   |
//| in the configured session time zone. Deliberately independent of  |
//| the broker's own trading hours - the user's windows are the       |
//| user's windows.                                                   |
//+------------------------------------------------------------------+
#ifndef FPMR_SESSION_WINDOW_MQH
#define FPMR_SESSION_WINDOW_MQH

#include "DstAnchor.mqh"   // FpTryParseClock, and the window shifting the anchor needs

class CSessionWindow
  {
private:
   int      m_index;
   bool     m_enabled;
   string   m_raw;
   bool     m_isValid;
   string   m_parseError;

   int      m_startMinute;  // minutes from midnight, inclusive
   int      m_endMinute;    // minutes from midnight, EXCLUSIVE - matches TradingView
   bool     m_wraps;        // true when the window crosses midnight (end <= start)

public:
                     CSessionWindow(void)
     : m_index(0), m_enabled(false), m_raw(""), m_isValid(false), m_parseError(""),
       m_startMinute(0), m_endMinute(0), m_wraps(false) {}

   void              Init(const int index,const bool enabled,const string raw)
     {
      m_index   = index;
      m_enabled = enabled;
      m_raw     = raw;

      int s,e;
      if(TryParse(raw,s,e))
        {
         m_startMinute = s;
         m_endMinute   = e;
         m_wraps       = (e<=s);
         m_isValid     = true;
         m_parseError  = "";
        }
      else
        {
         m_parseError = "Session "+IntegerToString(index)+" window '"+raw+"' is not in HHMM-HHMM form.";
         m_isValid    = false;
        }
     }

   int               Index(void)       const { return(m_index); }
   bool              Enabled(void)     const { return(m_enabled); }
   string            Raw(void)         const { return(m_raw); }
   bool              IsValid(void)     const { return(m_isValid); }
   string            ParseError(void)  const { return(m_parseError); }
   int               StartMinute(void) const { return(m_startMinute); }
   int               EndMinute(void)   const { return(m_endMinute); }
   bool              Wraps(void)       const { return(m_wraps); }

   bool              IsActive(void)    const { return(m_enabled && m_isValid); }

   //--- Length of the window in minutes. A window that wraps midnight, and one
   //    typed with an end equal to its start, both mean "through to that time
   //    tomorrow" rather than zero.
   int               DurationMinutes(void) const
     {
      int d=(m_endMinute-m_startMinute+1440)%1440;
      return(d==0 ? 1440 : d);
     }

   //--- Membership test for a bar OPEN timestamp already converted to the session zone.
   bool              Contains(const datetime tzBarOpen) const
     {
      if(!IsActive())
         return(false);

      MqlDateTime s;
      TimeToStruct(tzBarOpen,s);
      int m=s.hour*60+s.min;

      return(m_wraps ? (m>=m_startMinute || m<m_endMinute)
                     : (m>=m_startMinute && m<m_endMinute));
     }

   //--- The next moment this window opens at or after tzFrom.
   datetime          NextOpen(const datetime tzFrom) const
     {
      datetime today=FpDateOnly(tzFrom)+m_startMinute*60;
      return(today>=tzFrom ? today : today+86400);
     }

   //--- The most recent open of this window at or before tzFrom.
   datetime          PreviousOpen(const datetime tzFrom) const
     {
      datetime today=FpDateOnly(tzFrom)+m_startMinute*60;
      return(today<=tzFrom ? today : today-86400);
     }

   //--- Tolerates "0930-1000", "09:30-10:00" and surrounding whitespace.
   static bool       TryParse(const string raw,int &startMinute,int &endMinute)
     {
      startMinute=0;
      endMinute=0;

      string cleaned=raw;
      StringTrimLeft(cleaned);
      StringTrimRight(cleaned);
      if(cleaned=="")
         return(false);

      StringReplace(cleaned,":","");
      StringReplace(cleaned," ","");

      string parts[];
      if(StringSplit(cleaned,'-',parts)!=2)
         return(false);

      return(FpTryParseClock(parts[0],startMinute) && FpTryParseClock(parts[1],endMinute));
     }

   string            ToString(void) const
     {
      return("S"+IntegerToString(m_index)+" "+m_raw+(m_enabled ? "" : " (off)"));
     }
  };

#endif // FPMR_SESSION_WINDOW_MQH
//+------------------------------------------------------------------+
