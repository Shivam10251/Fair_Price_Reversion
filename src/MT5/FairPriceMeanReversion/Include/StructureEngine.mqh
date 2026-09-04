//+------------------------------------------------------------------+
//| FPMR · Core · StructureEngine                                    |
//|                                                                  |
//| Direct port of the Pine market-structure state machine (sections  |
//| 5 and 6). Exactly ONE active high and ONE active low exist at any |
//| moment. A newer confirmed pivot replaces the older one; a broken  |
//| level is consumed so the same level can never fire twice.         |
//|                                                                  |
//| The per-bar call order below is load-bearing and mirrors the Pine |
//| script:                                                           |
//|    1. BeginBar   - reset / seed / regime tracking                |
//|    2. OnPivotHigh / OnPivotLow  - newly CONFIRMED swings         |
//|    3. ExpireSetups                                               |
//|    4. DetectBreak                                                |
//| Changing that order changes the signals.                          |
//+------------------------------------------------------------------+
#ifndef FPMR_STRUCTURE_ENGINE_MQH
#define FPMR_STRUCTURE_ENGINE_MQH

#include "FpmrEnums.mqh"

struct StructureBreak
  {
   int           direction;      // -1 bearish, +1 bullish, 0 none
   FpBreakEvent  event;
   double        brokenLevel;
   int           brokenBarIndex; // absolute bar index of the swing that made the level
   bool          occurred;
  };

struct PivotAccepted
  {
   bool          isNew;
   FpSwingRole   role;
   double        price;
   int           pivotBarIndex;
   bool          becameActiveLevel;
  };

class CStructureEngine
  {
private:
   FpActiveLevelMode m_mode;

   FpStructState     m_state;

   double            m_activeHigh;
   FpSwingRole       m_activeHighRole;
   int               m_activeHighConf;
   int               m_activeHighBar;

   double            m_activeLow;
   FpSwingRole       m_activeLowRole;
   int               m_activeLowConf;
   int               m_activeLowBar;

   string            m_lastChoch;
   string            m_lastBos;

   double            m_prevPivotHigh;
   double            m_prevPivotLow;
   int               m_lastPos;

   void              HardReset(const int posState)
     {
      m_state          = (FpStructState)posState;
      m_activeHigh     = FPMR_NA;
      m_activeLow      = FPMR_NA;
      m_activeHighRole = FP_ROLE_NONE;
      m_activeLowRole  = FP_ROLE_NONE;
      m_activeHighConf = -1;
      m_activeLowConf  = -1;
      m_activeHighBar  = -1;
      m_activeLowBar   = -1;
      m_prevPivotHigh  = FPMR_NA;
      m_prevPivotLow   = FPMR_NA;
      m_lastPos        = 0;
     }

public:
                     CStructureEngine(void)
     : m_mode(FP_LEVEL_LATEST_SWING), m_lastChoch("-"), m_lastBos("-") { HardReset(0); }

   void              Init(const FpActiveLevelMode mode)
     {
      m_mode      = mode;
      m_lastChoch = "-";
      m_lastBos   = "-";
      HardReset(0);
     }

   FpStructState     State(void)          const { return(m_state); }
   double            ActiveHigh(void)     const { return(m_activeHigh); }
   FpSwingRole       ActiveHighRole(void) const { return(m_activeHighRole); }
   double            ActiveLow(void)      const { return(m_activeLow); }
   FpSwingRole       ActiveLowRole(void)  const { return(m_activeLowRole); }
   string            LastChoch(void)      const { return(m_lastChoch); }
   string            LastBos(void)        const { return(m_lastBos); }

   bool              HasActiveHigh(void)  const { return(!FpIsNa(m_activeHigh)); }
   bool              HasActiveLow(void)   const { return(!FpIsNa(m_activeLow)); }

   //--- Age in bars of the newest active level, or -1 when there is none.
   int               ActiveLevelAge(const int barIndex) const
     {
      int newest=-1;
      if(HasActiveHigh()) newest=MathMax(newest,m_activeHighConf);
      if(HasActiveLow())  newest=MathMax(newest,m_activeLowConf);
      return(newest<0 ? -1 : barIndex-newest);
     }

   //+---------------------------------------------------------------+
   //| Step 1 of the bar. Returns true when a reset fired.            |
   //| posState is +1 above the zone, -1 below it, 0 inside / no FP.  |
   //+---------------------------------------------------------------+
   bool              BeginBar(const int posState,const bool sessionStart,const bool fairPriceLost)
     {
      bool regimeFlip=(posState!=0 && posState!=m_lastPos);
      bool doReset=(sessionStart || regimeFlip || fairPriceLost);

      if(doReset)
         HardReset(posState);

      // Seed the state the first time price is clearly on one side of the zone.
      if(m_state==FP_STRUCT_UNDEFINED && posState!=0)
         m_state=(FpStructState)posState;

      if(posState!=0)
         m_lastPos=posState;

      return(doReset);
     }

   //--- Full clear used on EA restart. Keeps no memory of the previous run.
   void              ResetAll(void)
     {
      HardReset(0);
      m_lastChoch="-";
      m_lastBos="-";
     }

   //--- Step 2a. Feed a newly CONFIRMED pivot high.
   void              OnPivotHigh(const double price,const int pivotBarIndex,
                                 const int confirmBarIndex,PivotAccepted &out)
     {
      FpSwingRole role;

      if(FpIsNa(m_prevPivotHigh))
         role=(m_state==FP_STRUCT_BEARISH ? FP_ROLE_LH : FP_ROLE_HH);
      else
         role=(price>m_prevPivotHigh ? FP_ROLE_HH : FP_ROLE_LH);

      bool roleOk=(m_mode==FP_LEVEL_LATEST_SWING)
                  || (m_state==FP_STRUCT_BULLISH && role==FP_ROLE_HH)
                  || (m_state==FP_STRUCT_BEARISH && role==FP_ROLE_LH)
                  || !HasActiveHigh();

      if(roleOk)
        {
         m_activeHigh     = price;
         m_activeHighRole = role;
         m_activeHighConf = confirmBarIndex;
         m_activeHighBar  = pivotBarIndex;
        }

      m_prevPivotHigh=price;

      out.isNew             = true;
      out.role              = role;
      out.price             = price;
      out.pivotBarIndex     = pivotBarIndex;
      out.becameActiveLevel = roleOk;
     }

   //--- Step 2b. Feed a newly CONFIRMED pivot low.
   void              OnPivotLow(const double price,const int pivotBarIndex,
                                const int confirmBarIndex,PivotAccepted &out)
     {
      FpSwingRole role;

      if(FpIsNa(m_prevPivotLow))
         role=(m_state==FP_STRUCT_BULLISH ? FP_ROLE_HL : FP_ROLE_LL);
      else
         role=(price>m_prevPivotLow ? FP_ROLE_HL : FP_ROLE_LL);

      bool roleOk=(m_mode==FP_LEVEL_LATEST_SWING)
                  || (m_state==FP_STRUCT_BULLISH && role==FP_ROLE_HL)
                  || (m_state==FP_STRUCT_BEARISH && role==FP_ROLE_LL)
                  || !HasActiveLow();

      if(roleOk)
        {
         m_activeLow     = price;
         m_activeLowRole = role;
         m_activeLowConf = confirmBarIndex;
         m_activeLowBar  = pivotBarIndex;
        }

      m_prevPivotLow=price;

      out.isNew             = true;
      out.role              = role;
      out.price             = price;
      out.pivotBarIndex     = pivotBarIndex;
      out.becameActiveLevel = roleOk;
     }

   //+---------------------------------------------------------------+
   //| Step 3. Setup validity, measured in bars from the CONFIRMATION |
   //| bar of the level. setupBars == 0 means levels never expire.    |
   //+---------------------------------------------------------------+
   void              ExpireSetups(const int barIndex,const int setupBars)
     {
      if(setupBars<=0)
         return;

      if(HasActiveHigh() && m_activeHighConf>=0 && barIndex-m_activeHighConf>setupBars)
        {
         m_activeHigh     = FPMR_NA;
         m_activeHighRole = FP_ROLE_NONE;
        }

      if(HasActiveLow() && m_activeLowConf>=0 && barIndex-m_activeLowConf>setupBars)
        {
         m_activeLow     = FPMR_NA;
         m_activeLowRole = FP_ROLE_NONE;
        }
     }

   //+---------------------------------------------------------------+
   //| Step 4. The candle that closes (or wicks) through the active   |
   //| level IS the displacement candle. A single candle can never    |
   //| break structure both ways - the bearish break is evaluated     |
   //| first, exactly as in Pine.                                     |
   //+---------------------------------------------------------------+
   void              DetectBreak(const double breakUpPrice,const double breakDownPrice,
                                 const int barIndex,StructureBreak &result)
     {
      result.direction      = 0;
      result.event          = FP_EVENT_NONE;
      result.brokenLevel    = FPMR_NA;
      result.brokenBarIndex = -1;
      result.occurred       = false;

      bool bearBreak=HasActiveLow()  && m_activeLowConf >=0 && barIndex>=m_activeLowConf  && breakDownPrice<m_activeLow;
      bool bullBreak=HasActiveHigh() && m_activeHighConf>=0 && barIndex>=m_activeHighConf && breakUpPrice  >m_activeHigh;

      if(bearBreak)
         bullBreak=false;

      if(bearBreak)
        {
         result.direction      = -1;
         result.event          = (m_state==FP_STRUCT_BULLISH ? FP_EVENT_CHOCH : FP_EVENT_BOS);
         result.brokenLevel    = m_activeLow;
         result.brokenBarIndex = m_activeLowBar;
         result.occurred       = true;

         m_state         = FP_STRUCT_BEARISH;
         m_activeLow     = FPMR_NA;   // level consumed
         m_activeLowRole = FP_ROLE_NONE;

         string txt="Bearish @ "+DoubleToString(result.brokenLevel,_Digits);
         if(result.event==FP_EVENT_CHOCH) m_lastChoch=txt; else m_lastBos=txt;
        }
      else if(bullBreak)
        {
         result.direction      = 1;
         result.event          = (m_state==FP_STRUCT_BEARISH ? FP_EVENT_CHOCH : FP_EVENT_BOS);
         result.brokenLevel    = m_activeHigh;
         result.brokenBarIndex = m_activeHighBar;
         result.occurred       = true;

         m_state          = FP_STRUCT_BULLISH;
         m_activeHigh     = FPMR_NA;
         m_activeHighRole = FP_ROLE_NONE;

         string txt="Bullish @ "+DoubleToString(result.brokenLevel,_Digits);
         if(result.event==FP_EVENT_CHOCH) m_lastChoch=txt; else m_lastBos=txt;
        }
     }
  };

#endif // FPMR_STRUCTURE_ENGINE_MQH
//+------------------------------------------------------------------+
