//+------------------------------------------------------------------+
//| FPMR · Core · Enums                                              |
//|                                                                  |
//| Every user-selectable mode in the strategy, ported 1:1 from the   |
//| NinjaTrader 8 build (FPMR/Core/FpmrEnums.cs). Kept in one file so |
//| the option sets can be audited against the Pine spec at a glance. |
//|                                                                  |
//| MQL5 shows the trailing comment on each member in the inputs      |
//| dialog, so the labels double as the user-facing option text.      |
//+------------------------------------------------------------------+
#ifndef FPMR_ENUMS_MQH
#define FPMR_ENUMS_MQH

//--- "not available" sentinel -------------------------------------------------
// The C# build leaned on double.NaN. MQL5 compares NaN inconsistently across
// builds and the strategy tester, so a sentinel plus an explicit predicate is
// used instead. EVERY comparison against a possibly-absent price must be
// guarded by FpIsNa() or by the caller's own "has value" flag.
#define FPMR_NA  (DBL_MAX)

bool FpIsNa(const double v)
  {
   return(v == FPMR_NA || !MathIsValidNumber(v));
  }

//--- Which price of the session's first reference candle becomes Fair Price ---
enum FpSource
  {
   FP_SRC_CLOSE = 0,  // Close
   FP_SRC_OPEN  = 1,  // Open
   FP_SRC_HL2   = 2,  // HL2
   FP_SRC_HLC3  = 3   // HLC3
  };

//--- Whether a structure level must be broken by a close or by a wick ---------
enum FpBreakConfirm
  {
   FP_BREAK_CLOSE = 0, // Close through the level
   FP_BREAK_WICK  = 1  // Wick through the level
  };

//--- How the single active level is replaced ---------------------------------
//  LatestSwing  : the newest confirmed pivot always becomes the active level.
//  RoleValidOnly: a replacement is accepted only when it carries the role that
//                 matches the current structure state (HL in bull / LL in bear).
enum FpActiveLevelMode
  {
   FP_LEVEL_LATEST_SWING   = 0, // LatestSwing - newest confirmed pivot always wins
   FP_LEVEL_ROLE_VALID_ONLY= 1  // RoleValidOnly - only a role-correct swing may replace it
  };

//--- Distance band from Fair Price -------------------------------------------
//  None   : the entry sits inside the non-tradeable zone (no trade).
//  Near   : between the zone edge and Band 1 - take profit at the R:R multiple.
//  Far    : between Band 1 and Band 2 - take profit at Fair Price itself.
//  Beyond : past Band 2 - too far from Fair Price, no trade.
enum FpSetupBand
  {
   FP_BAND_NONE   = 0,
   FP_BAND_NEAR   = 1,
   FP_BAND_FAR    = 2,
   FP_BAND_BEYOND = 3
  };

//--- Trailing-stop behaviour for percentage-band setups (Near and Far) --------
//  Off       : the stop stays where it was placed at entry.
//  RStep     : whole-R ratchet - at +1R the stop moves to breakeven, at +2R to
//              +1R, at +3R to +2R, and so on. Only ever tightens.
//  Structure : the stop trails confirmed swings - down to each lower swing high
//              for shorts, up to each higher swing low for longs. Only tightens.
enum FpTrailMode
  {
   FP_TRAIL_OFF       = 0, // Off - stop stays at entry
   FP_TRAIL_RSTEP     = 1, // R-step - whole-R ratchet
   FP_TRAIL_STRUCTURE = 2  // Structure - trails confirmed swings
  };

//--- How, if at all, a setup is inverted before it reaches the broker ---------
//  Off    : trade the setup as signalled.
//  Mirror : opposite side, stop and target MIRRORED about the entry, so both
//           distances - and therefore the sized risk and the R multiple - are
//           unchanged. The reversed trade still has a near stop and a far
//           target, so it can lose the same setup the original lost.
//  Swap   : opposite side with the stop and target LEVELS exchanged. This is
//           the true P&L inverse: the reversed trade loses exactly when the
//           original would have won, so the two win rates sum to 100%. Note
//           the risk distance changes, so the position is re-sized on it -
//           which means the MONEY does not mirror, only the outcomes.
//  SwapKeepSize : as Swap, but the position keeps the size the ORIGINAL setup
//           would have taken, so the P&L mirrors in dollars too. This is the
//           only mode that reproduces the inverse equity curve, and it does so
//           by DELIBERATELY BREACHING the risk cap: the stop is now the old
//           target distance while the lots were sized for the old stop, so the
//           money at risk per trade is multiplied by target/stop. Diagnostic
//           tool, not a risk policy.
enum FpReverseMode
  {
   FP_REVERSE_OFF    = 0, // Off - trade the setup as signalled
   FP_REVERSE_MIRROR = 1, // Mirror bracket - opposite side, same SL and TP distances
   FP_REVERSE_SWAP   = 2, // Swap bracket - opposite side, SL and TP exchanged (true inverse)
   FP_REVERSE_SWAP_KEEPSIZE = 3 // Swap bracket, ORIGINAL size - true P&L mirror, IGNORES the risk cap
  };

//--- Impact rating as parsed from the calendar file ---------------------------
enum FpNewsImpact
  {
   FP_IMPACT_UNKNOWN = 0,
   FP_IMPACT_LOW     = 1,
   FP_IMPACT_MEDIUM  = 2,
   FP_IMPACT_HIGH    = 3
  };

//--- Which impact ratings qualify a news event for the Fair Price override ----
enum FpNewsImpactFilter
  {
   FP_NEWSIMP_HIGH_ONLY   = 0, // High only
   FP_NEWSIMP_MEDIUM_ONLY = 1, // Medium only
   FP_NEWSIMP_BOTH        = 2  // High and Medium
  };

//--- Which event wins when several qualify inside the lookback window ---------
enum FpNewsMultipleEventRule
  {
   FP_NEWSMULTI_FIRST          = 0, // First
   FP_NEWSMULTI_LAST           = 1, // Last
   FP_NEWSMULTI_HIGHEST_IMPACT = 2  // Highest impact
  };

//--- When trading may begin on a session whose Fair Price came from news ------
enum FpNewsTradingStart
  {
   FP_NEWSSTART_AFTER_NEWS_CANDLE = 0, // AfterNewsCandle - may begin before the session opens
   FP_NEWSSTART_AFTER_SESSION_OPEN= 1  // AfterSessionOpen - normal session gating
  };

//--- How far the ACTUAL release differed from the FORECAST --------------------
enum FpNewsSurprise
  {
   FP_SURPRISE_UNKNOWN              = 0,
   FP_SURPRISE_EXPECTED             = 1,
   FP_SURPRISE_PARTIALLY_UNEXPECTED = 2,
   FP_SURPRISE_UNEXPECTED           = 3
  };

//--- What the news branch wants the strategy to do ----------------------------
//  Reversion    : trade back toward Fair Price (the strategy's normal behaviour).
//  Continuation : trade WITH the displacement, away from the pre-news price.
//  Wait         : no entries until a new Fair Price has been established.
enum FpNewsBias
  {
   FP_BIAS_REVERSION    = 0,
   FP_BIAS_CONTINUATION = 1,
   FP_BIAS_WAIT         = 2
  };

//--- What to do with a release whose forecast or actual is missing ------------
enum FpNewsUnknownRule
  {
   FP_UNKNOWN_TREAT_AS_EXPECTED   = 0, // TreatAsExpected
   FP_UNKNOWN_TREAT_AS_UNEXPECTED = 1, // TreatAsUnexpected
   FP_UNKNOWN_SKIP_EVENT          = 2  // SkipEvent
  };

//--- Reporting-only preference for the candle containing both TP and SL -------
enum FpSameBarPriority
  {
   FP_SAMEBAR_SL_FIRST      = 0, // SlFirst
   FP_SAMEBAR_TP_FIRST      = 1, // TpFirst
   FP_SAMEBAR_BAR_DIRECTION = 2, // BarDirection
   FP_SAMEBAR_TICK_SEQUENCE = 3  // TickSequence - trust the fill engine
  };

//--- Structure direction. Mirrors Pine's structState (1 / -1 / 0) -------------
enum FpStructState
  {
   FP_STRUCT_BEARISH   = -1,
   FP_STRUCT_UNDEFINED = 0,
   FP_STRUCT_BULLISH   = 1
  };

//--- Role assigned to a confirmed pivot ---------------------------------------
enum FpSwingRole
  {
   FP_ROLE_NONE = 0,
   FP_ROLE_HH   = 1,
   FP_ROLE_HL   = 2,
   FP_ROLE_LH   = 3,
   FP_ROLE_LL   = 4
  };

//--- Classification of a structure break --------------------------------------
enum FpBreakEvent
  {
   FP_EVENT_NONE  = 0,
   FP_EVENT_CHOCH = 1,
   FP_EVENT_BOS   = 2
  };

//--- The first gate that blocked a displacement candle from becoming a trade --
// Evaluated in the declared order - see RejectionReporter.mqh.
enum FpReject
  {
   FP_REJ_NONE = 0,
   FP_REJ_SESSION,      // outside every enabled session window
   FP_REJ_NO_FAIR_PRICE,// Fair Price not established for this session yet
   FP_REJ_NEWS_WAIT,    // a news surprise is waiting for its post-news consolidation
   FP_REJ_WARMUP,       // MinBarsBeforeFirstTrade has not elapsed
   FP_REJ_IN_ZONE,      // close sits inside the Fair Price zone
   FP_REJ_TOO_FAR,      // close sits beyond Band 2 - too far from Fair Price to trade
   FP_REJ_SIDE,         // break direction disagrees with the Fair Price side
   FP_REJ_DAILY_LOSS,   // realised loss for the trading day reached the limit
   FP_REJ_DAILY_PROFIT, // realised profit for the trading day reached the limit
   FP_REJ_DAY_CAP,      // max trades per day reached
   FP_REJ_SESSION_CAP,  // max trades per session reached
   FP_REJ_IN_TRADE,     // a trade is open and "one at a time" is on
   FP_REJ_CONCURRENCY,  // max concurrent entries for this direction reached
   FP_REJ_EVENT_OFF,    // CHoCH or BOS entries disabled for this event type
   FP_REJ_EMA,          // EMA filter rejected it
   FP_REJ_VWAP,         // VWAP filter rejected it
   FP_REJ_RISK,         // risk <= 0 or below the minimum stop distance
   FP_REJ_RISK_CAP,     // one lot would risk more than RiskHardCapUSD
   FP_REJ_UNRECONCILED, // EA restarted into an un-matched live position
   FP_REJ_MARGIN        // not enough free margin for the sized order
  };

//+------------------------------------------------------------------+
//| Text helpers                                                     |
//+------------------------------------------------------------------+
string FpBreakEventText(const FpBreakEvent e)
  {
   switch(e)
     {
      case FP_EVENT_CHOCH: return("CHoCH");
      case FP_EVENT_BOS:   return("BOS");
      default:             return("None");
     }
  }

string FpTrailModeText(const FpTrailMode m)
  {
   switch(m)
     {
      case FP_TRAIL_RSTEP:     return("RStep");
      case FP_TRAIL_STRUCTURE: return("Structure");
      default:                 return("Off");
     }
  }

string FpSetupBandText(const FpSetupBand b)
  {
   switch(b)
     {
      case FP_BAND_NEAR:   return("Near");
      case FP_BAND_FAR:    return("Far");
      case FP_BAND_BEYOND: return("Beyond");
      default:             return("None");
     }
  }

string FpSwingRoleText(const FpSwingRole r)
  {
   switch(r)
     {
      case FP_ROLE_HH: return("HH");
      case FP_ROLE_HL: return("HL");
      case FP_ROLE_LH: return("LH");
      case FP_ROLE_LL: return("LL");
      default:         return("-");
     }
  }

#endif // FPMR_ENUMS_MQH
//+------------------------------------------------------------------+
