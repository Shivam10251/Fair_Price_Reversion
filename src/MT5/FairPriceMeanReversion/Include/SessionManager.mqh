//+------------------------------------------------------------------+
//| FPMR · Core · SessionManager                                     |
//|                                                                  |
//| Owns the three windows, the session time zone, and the bar-to-bar |
//| transition detection (session start / session end / new day).     |
//|                                                                  |
//| TIME MODEL - this is the part that must not be got wrong.        |
//| * MT5 stamps a bar with its OPEN time (unlike NinjaTrader, which  |
//|   stamps the close), and TradingView evaluates a session against  |
//|   the bar's OPEN. So the MT5 bar time goes in unmodified where    |
//|   the NT8 build had to subtract the bar length first.             |
//| * The comparison happens in the user's session zone, never in     |
//|   terminal local time and never in broker server time.            |
//+------------------------------------------------------------------+
#ifndef FPMR_SESSION_MANAGER_MQH
#define FPMR_SESSION_MANAGER_MQH

#include "SessionWindow.mqh"

struct SessionEvaluation
  {
   int      index;          // 1..3, or 0 when outside every enabled window
   bool     inSession;
   bool     isSessionStart;
   bool     isSessionEnd;
   bool     isNewDay;
   datetime tzBarOpen;
   datetime sessionOpenTz; // open instant of the active session, 0 when flat
  };

class CSessionManager
  {
private:
   CSessionWindow    m_windows[3];
   FpTzId            m_sessionTz;

   int               m_prevIndex;
   int               m_prevDay;
   bool              m_seenAnyBar;

public:
                     CSessionManager(void)
     : m_sessionTz(FP_TZ_UTC), m_prevIndex(0), m_prevDay(INT_MIN), m_seenAnyBar(false) {}

   void              Init(const FpTzId sessionTz,
                          const bool s1On,const string s1Raw,
                          const bool s2On,const string s2Raw,
                          const bool s3On,const string s3Raw)
     {
      m_sessionTz=sessionTz;
      m_windows[0].Init(1,s1On,s1Raw);
      m_windows[1].Init(2,s2On,s2Raw);
      m_windows[2].Init(3,s3On,s3Raw);
      Reset();
     }

   FpTzId            SessionTimeZone(void) const { return(m_sessionTz); }

   //--- Window access by 1-based session index.
   CSessionWindow   *Window(const int sessionIndex)
     {
      if(sessionIndex<1 || sessionIndex>3)
         return(NULL);
      return(GetPointer(m_windows[sessionIndex-1]));
     }

   //--- The open of a session at or before tzFrom, without exposing the array.
   datetime          PreviousOpenOf(const int sessionIndex,const datetime tzFrom)
     {
      if(sessionIndex<1 || sessionIndex>3)
         return(0);
      return(m_windows[sessionIndex-1].PreviousOpen(tzFrom));
     }

   //--- Close instant of a session, given the open the evaluator reported.
   datetime          CloseOfSession(const int sessionIndex,const datetime openTz)
     {
      if(sessionIndex<1 || sessionIndex>3)
         return(openTz);
      return(openTz+m_windows[sessionIndex-1].DurationMinutes()*60);
     }

   //--- Any window that failed to parse, so the EA can refuse to run.
   string            FirstConfigError(void)
     {
      for(int i=0;i<3;i++)
         if(m_windows[i].Enabled() && !m_windows[i].IsValid())
            return(m_windows[i].ParseError());
      return("");
     }

   //--- Overlapping windows resolve to the lowest-numbered enabled session (Pine behaviour).
   int               IndexAt(const datetime tzBarOpen)
     {
      for(int i=0;i<3;i++)
         if(m_windows[i].Contains(tzBarOpen))
            return(m_windows[i].Index());
      return(0);
     }

   //--- Advances the transition state machine by one bar.
   //    tzBarOpen is the bar's OPEN timestamp already converted to the session zone.
   void              Advance(const datetime tzBarOpen,SessionEvaluation &ev)
     {
      int idx=IndexAt(tzBarOpen);
      int dayKey=FpDayKey(tzBarOpen);

      ev.index          = idx;
      ev.inSession      = (idx!=0);
      ev.tzBarOpen      = tzBarOpen;
      ev.isNewDay       = (m_seenAnyBar && dayKey!=m_prevDay);
      ev.isSessionStart = (idx!=0 && idx!=m_prevIndex);
      ev.isSessionEnd   = (idx==0 && m_prevIndex!=0);
      ev.sessionOpenTz  = (idx==0 ? (datetime)0 : m_windows[idx-1].PreviousOpen(tzBarOpen));

      m_prevIndex  = idx;
      m_prevDay    = dayKey;
      m_seenAnyBar = true;
     }

   //--- The next open of any ENABLED window at or after tzFrom.
   //    The session index is returned so a news override can be scoped to one session.
   bool              TryGetNextSessionOpen(const datetime tzFrom,datetime &openTz,int &sessionIndex)
     {
      openTz       = (datetime)LONG_MAX;
      sessionIndex = 0;

      for(int i=0;i<3;i++)
        {
         if(!m_windows[i].IsActive())
            continue;

         datetime candidate=m_windows[i].NextOpen(tzFrom);
         if(candidate<openTz)
           {
            openTz       = candidate;
            sessionIndex = m_windows[i].Index();
           }
        }

      return(sessionIndex!=0);
     }

   //--- Resets the transition state - used when the EA re-initialises.
   void              Reset(void)
     {
      m_prevIndex  = 0;
      m_prevDay    = INT_MIN;
      m_seenAnyBar = false;
     }
  };

#endif // FPMR_SESSION_MANAGER_MQH
//+------------------------------------------------------------------+
