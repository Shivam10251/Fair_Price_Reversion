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

	/// <summary>Whether a structure level must be broken by a close or by a wick.</summary>
	public enum FpBreakConfirm { Close, Wick }

	/// <summary>
	/// LatestSwing  : the newest confirmed pivot always becomes the active level.
	/// RoleValidOnly: a replacement is accepted only when it carries the role that
	///                matches the current structure state (HL in bull / LL in bear).
	/// </summary>
	public enum FpActiveLevelMode { LatestSwing, RoleValidOnly }

	/// <summary>
	/// Which distance band from Fair Price an entry falls in, deciding both whether
	/// the trade is allowed and how its take-profit is chosen.
	///   None   : the entry sits inside the non-tradeable zone (no trade).
	///   Near   : between the zone edge and Band 1 — take profit at the R:R multiple.
	///   Far    : between Band 1 and Band 2 — take profit at Fair Price itself.
	///   Beyond : past Band 2 — too far from Fair Price, no trade.
	/// </summary>
	public enum FpSetupBand { None, Near, Far, Beyond }

	/// <summary>
	/// Which side of the Fair Price zone a structure break is allowed to trade.
	///   Reversion       : the original model. Above the zone only SHORTS, below it
	///                     only LONGS, so every trade heads back toward Fair Price.
	///                     Both BOS and CHoCH are eligible, per their own switches.
	///   BosContinuation : the inverse. Above the zone only LONGS on a bullish BOS,
	///                     below it only SHORTS on a bearish BOS — the move away from
	///                     Fair Price is traded rather than faded. Every CHoCH is
	///                     refused here, and so is any break pointing back toward Fair
	///                     Price, whatever "Take CHoCH entries" says. A FAR-band setup
	///                     takes the R:R target rather than Fair Price, which now sits
	///                     behind a trade running away from it.
	/// </summary>
	public enum FpEntryModel { Reversion, BosContinuation }

	/// <summary>
	/// Trailing-stop behaviour for percentage-band setups (Near and Far).
	///   Off       : the stop stays where it was placed at entry.
	///   RStep     : whole-R ratchet — at +1R the stop moves to breakeven, at +2R to
	///               +1R, at +3R to +2R, and so on. Only ever tightens.
	///   Structure : the stop trails confirmed swings — down to each lower swing high
	///               for shorts, up to each higher swing low for longs. Only tightens.
	/// </summary>
	public enum FpTrailMode { Off, RStep, Structure }

	/// <summary>Impact rating as parsed from the calendar file.</summary>
	public enum FpNewsImpact { Unknown = 0, Low = 1, Medium = 2, High = 3 }

	/// <summary>Which impact ratings qualify a news event for the Fair Price override.</summary>
	public enum FpNewsImpactFilter { HighOnly, MediumOnly, Both }

	/// <summary>Which event wins when several qualify inside the lookback window.</summary>
	public enum FpNewsMultipleEventRule { First, Last, HighestImpact }

	/// <summary>When trading may begin on a session whose Fair Price came from news.</summary>
	public enum FpNewsTradingStart { AfterNewsCandle, AfterSessionOpen }

	/// <summary>
	/// How far the ACTUAL release differed from the FORECAST, as a share of the
	/// forecast. Drives which branch of the news decision tree applies.
	///   Expected            : actual ~= forecast, the release was already priced in.
	///   PartiallyUnexpected : a meaningful surprise; the market may legitimately reprice.
	///   Unexpected          : a large surprise, or an event with no forecast at all.
	/// </summary>
	public enum FpNewsSurprise { Unknown, Expected, PartiallyUnexpected, Unexpected }

	/// <summary>
	/// What the news branch wants the strategy to do.
	///   Reversion    : trade back toward Fair Price (the strategy's normal behaviour).
	///   Continuation : trade WITH the displacement, away from the pre-news price.
	///   Wait         : no entries until a new Fair Price has been established.
	/// </summary>
	public enum FpNewsBias { Reversion, Continuation, Wait }

	/// <summary>
	/// What to do with a release whose forecast or actual is missing from the file.
	/// A forward-looking calendar has no actuals, so this is the common case live.
	/// </summary>
	public enum FpNewsUnknownRule { TreatAsExpected, TreatAsUnexpected, SkipEvent }

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
		NewsWait,    // a news surprise is waiting for its post-news consolidation
		Warmup,      // MinBarsBeforeFirstTrade has not elapsed
		InZone,      // close sits inside the Fair Price zone
		TooFar,      // close sits beyond Band 2 — too far from Fair Price to trade
		Side,        // break direction disagrees with the Fair Price side
		DailyLoss,   // realised loss for the trading day reached the limit
		DailyProfit, // realised profit for the trading day reached the limit
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
