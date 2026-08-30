// =============================================================================
//  FPMR · Core · Enums
//  Every user-selectable mode in the strategy. Kept in one file so the option
//  sets can be audited against the Pine spec at a glance.
//  Namespace is a sub-namespace of Strategies so NinjaTrader compiles it as part
//  of the Custom assembly without registering these types as NinjaScript objects.
// =============================================================================
namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	/// <summary>Which price of the session's first reference candle becomes Fair Price.</summary>
	public enum FpSource { Close, Open, HL2, HLC3 }

	/// <summary>Unit used by the Fair Price zone distance and the extended-TP offset.</summary>
	public enum FpZoneUnit { Points, Ticks }

	/// <summary>Whether a structure level must be broken by a close or by a wick.</summary>
	public enum FpBreakConfirm { Close, Wick }

	/// <summary>
	/// LatestSwing  : the newest confirmed pivot always becomes the active level.
	/// RoleValidOnly: a replacement is accepted only when it carries the role that
	///                matches the current structure state (HL in bull / LL in bear).
	/// </summary>
	public enum FpActiveLevelMode { LatestSwing, RoleValidOnly }

	/// <summary>Target selection while the extended-move TP override is armed.</summary>
	public enum FpExtendedTpMode { FairPriceAlways, NearerOfTheTwo, FartherOfTheTwo }

	/// <summary>Impact rating as parsed from the calendar file.</summary>
	public enum FpNewsImpact { Unknown = 0, Low = 1, Medium = 2, High = 3 }

	/// <summary>Which impact ratings qualify a news event for the Fair Price override.</summary>
	public enum FpNewsImpactFilter { HighOnly, MediumOnly, Both }

	/// <summary>Which event wins when several qualify inside the lookback window.</summary>
	public enum FpNewsMultipleEventRule { First, Last, HighestImpact }

	/// <summary>When trading may begin on a session whose Fair Price came from news.</summary>
	public enum FpNewsTradingStart { AfterNewsCandle, AfterSessionOpen }

	/// <summary>
	/// Reporting-only preference for the candle that contains both TP and SL.
	/// The actual fill comes from the order fill resolution, not from this.
	/// </summary>
	public enum FpSameBarPriority { SlFirst, TpFirst, BarDirection, TickSequence }

	/// <summary>Structure direction. Mirrors Pine's structState (1 / -1 / 0).</summary>
	public enum FpStructState { Undefined = 0, Bullish = 1, Bearish = -1 }

	/// <summary>Role assigned to a confirmed pivot.</summary>
	public enum FpSwingRole { None, HH, HL, LH, LL }

	/// <summary>Classification of a structure break.</summary>
	public enum FpBreakEvent { None, CHoCH, BOS }

	/// <summary>
	/// The first gate that blocked a displacement candle from becoming a trade.
	/// Evaluated in the declared order — see RejectionReporter.
	/// </summary>
	public enum FpReject
	{
		None,
		Session,     // outside every enabled session window
		NoFairPrice, // Fair Price not established for this session yet
		Warmup,      // MinBarsBeforeFirstTrade has not elapsed
		InZone,      // close sits inside the Fair Price zone
		Side,        // break direction disagrees with the Fair Price side
		DayCap,      // max trades per day reached
		SessionCap,  // max trades per session reached
		InTrade,     // a trade is open and "one at a time" is on
		EventOff,    // CHoCH or BOS entries disabled for this event type
		Ema,         // EMA filter rejected it
		Vwap,        // VWAP filter rejected it
		Risk,        // risk <= 0 or below the minimum stop distance
		RiskCap,     // one contract would risk more than RiskHardCapUSD
		Unreconciled // strategy restarted into an un-matched live position
	}
}
