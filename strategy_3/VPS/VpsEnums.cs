// =============================================================================
//  VPS · Enums
//
//  Every user-selectable mode, plus the identity of the five indicator lines.
//  The namespace is a sub-namespace of Strategies so NinjaTrader compiles these
//  into the Custom assembly WITHOUT registering them as NinjaScript objects.
// =============================================================================
namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	/// <summary>
	/// The five lines the strategy trades around. Identity matters as well as
	/// price: the break-even rule excludes the ENTRY line and the TARGET line
	/// specifically, not merely the prices they happen to sit at.
	/// </summary>
	public enum VpsLevel
	{
		None = 0,
		Vah,   // Value Area High   — high-side entry level
		Poc,   // Point of Control  — never an entry, only ever an intermediate
		Val,   // Value Area Low    — low-side entry level
		Rh,    // Range High        — high-side entry level
		Rl     // Range Low         — low-side entry level
	}

	/// <summary>Which confirmation produced the entry. Reporting only.</summary>
	public enum VpsConfirmation
	{
		None,
		Rejection,  // Method A — the sweep candle closed back through the level
		Engulfing   // Method B — a following candle engulfed at the swept level
	}

	/// <summary>
	/// Deterministic winner when two opposite-side targets sit at EXACTLY the same
	/// distance from the entry. Required by section 13 of the specification and
	/// exposed so the choice is never hidden inside the code.
	/// </summary>
	public enum VpsTargetTieBreak
	{
		PreferRange,      // RH / RL wins
		PreferValueArea   // VAH / VAL wins
	}

	/// <summary>How many contracts a signal is worth.</summary>
	public enum VpsSizingMode
	{
		/// <summary>A flat contract count, as section 15 of the specification asks for.</summary>
		Fixed,
		/// <summary>Sized so the dollar risk to the sweep-candle stop lands near a target.</summary>
		RiskBased
	}

	/// <summary>Which confirmation methods are allowed to fire.</summary>
	public enum VpsConfirmMode
	{
		RejectionOrEngulfing,  // either one, as specified
		RejectionOnly,
		EngulfingOnly
	}
}
