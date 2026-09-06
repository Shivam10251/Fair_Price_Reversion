//+------------------------------------------------------------------+
//| FPMR · Core · DstAnchor                                          |
//|                                                                  |
//| THE PROBLEM                                                      |
//| IST (Asia/Kolkata) is UTC+5:30 all year - India does not observe  |
//| DST. The US markets do. So a US market event that happens at ONE  |
//| fixed Eastern time lands at TWO different IST times across the    |
//| year:                                                             |
//|                                                                  |
//|     US summer (EDT, UTC-4)   IST = ET + 9h30   09:30 ET = 19:00   |
//|     US winter (EST, UTC-5)   IST = ET + 10h30  09:30 ET = 20:00   |
//|                                                                  |
//| A window typed in IST and left alone would therefore drift an     |
//| hour off the market twice a year, and a backtest spanning a       |
//| contract rollover crosses both transitions.                       |
//|                                                                  |
//| THE FIX                                                          |
//| The user types SUMMER times in their own zone. Rebasing those     |
//| once onto the anchor zone gives a window that is FIXED there all  |
//| year, so evaluating membership in the anchor zone makes the       |
//| winter shift automatic.                                           |
//|                                                                  |
//| Shifting the IST window by +1h in winter and evaluating a fixed   |
//| Eastern window are arithmetically identical, but the second form  |
//| asks the DST rules for the transition dates rather than           |
//| hardcoding them - so it keeps working if the rules change, and it |
//| resolves the ambiguous and invalid wall-clock hours around a      |
//| transition the same way all year.                                 |
//+------------------------------------------------------------------+
#ifndef FPMR_DST_ANCHOR_MQH
#define FPMR_DST_ANCHOR_MQH

#include "FpmrTimeZone.mqh"

//+------------------------------------------------------------------+
//| DstAnchor                                                        |
//|                                                                  |
//| The user types SUMMER times in their own zone. Rebasing those     |
//| once onto the anchor zone gives a window that is FIXED there all  |
//| year, so evaluating membership in the anchor zone makes the       |
//| winter shift automatic. Same contract as FPMR/Core/DstAnchor.cs.   |
//+------------------------------------------------------------------+

//--- How far the user's zone runs ahead of the anchor DURING the anchor's summer
int FpAnchorSummerShift(const FpTzId userZone,const FpTzId anchorZone)
  {
   if(userZone==FP_TZ_INVALID || anchorZone==FP_TZ_INVALID)
      return(0);

   datetime summerReference=FpMakeUtc(2025,7,15,12,0,0);
   return(FpTzOffsetAtUtc(userZone,summerReference)-FpTzOffsetAtUtc(anchorZone,summerReference));
  }

//--- The extra offset winter adds, in the user's clock. Logging only.
int FpAnchorWinterExtra(const FpTzId userZone,const FpTzId anchorZone)
  {
   if(userZone==FP_TZ_INVALID || anchorZone==FP_TZ_INVALID)
      return(0);

   datetime winter=FpMakeUtc(2025,1,15,12,0,0);
   int winterDelta=FpTzOffsetAtUtc(userZone,winter)-FpTzOffsetAtUtc(anchorZone,winter);

   return(winterDelta-FpAnchorSummerShift(userZone,anchorZone));
  }

//--- Wraps a minute-of-day back into [0, 1440)
int FpNormaliseMinutes(const int minutes)
  {
   int m=minutes%1440;
   return(m<0 ? m+1440 : m);
  }

//--- Minutes from midnight for an "HHMM" token
bool FpTryParseClock(const string token,int &minutes)
  {
   minutes=0;

   string t=token;
   StringTrimLeft(t);
   StringTrimRight(t);
   StringReplace(t,":","");

   if(StringLen(t)!=4)
      return(false);

   for(int i=0;i<4;i++)
     {
      ushort c=StringGetCharacter(t,i);
      if(c<'0' || c>'9')
         return(false);
     }

   int hhmm=(int)StringToInteger(t);
   int hh=hhmm/100;
   int mm=hhmm%100;

   if(hh<0 || hh>23 || mm<0 || mm>59)
      return(false);

   minutes=hh*60+mm;
   return(true);
  }

//--- "HHMM" label for a minute-of-day
string FpClockLabel(const int minutes)
  {
   int m=FpNormaliseMinutes(minutes);
   return(StringFormat("%02d%02d",m/60,m%60));
  }

//--- Shifts a "HHMM-HHMM" window by deltaSeconds; malformed input is returned
//    unchanged so the normal parser still produces its own error message.
string FpShiftWindow(const string window,const int deltaSeconds)
  {
   string w=window;
   StringTrimLeft(w);
   StringTrimRight(w);

   if(w=="")
      return(window);

   string parts[];
   if(StringSplit(w,'-',parts)!=2)
      return(window);

   int start,end;
   if(!FpTryParseClock(parts[0],start) || !FpTryParseClock(parts[1],end))
      return(window);

   int d=deltaSeconds/60;

   return(FpClockLabel(FpNormaliseMinutes(start+d))+"-"+FpClockLabel(FpNormaliseMinutes(end+d)));
  }

#endif // FPMR_DST_ANCHOR_MQH
//+------------------------------------------------------------------+
