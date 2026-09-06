// =============================================================================
//  FPMR · Core · ExtendedTpEngine
//
//  The extended-move setup (Pine group 4b), reworked.
//
//  WHAT IT IS
//  When price has stretched X% away from Fair Price the move is treated as
//  over-extended, and the reversion back to Fair Price becomes the trade.
//  While the setup is ARMED two things change:
//
//    1. ONLY a BOS running back toward Fair Price may open a trade. A CHoCH may
//       not. The strategy's own "Take CHoCH / Take BOS" switches do not apply
//       while armed — this rule replaces them.
//    2. That trade targets the Fair Price line rather than the RR multiple.
//
//  A CHoCH is the FIRST break against the prevailing structure and is the weaker,
//  earlier signal. Requiring a BOS means the reversion has already broken
//  structure in its own direction before the trade is taken.
//
//  ARMING
//  Armed when |close - FP| / FP × 100 >= X%. Which side price sits on decides
//  which way the setup runs, expressed as the direction the reversion trade takes:
//
//      close ABOVE Fair Price  ->  ArmedDirection = -1  (short, reverting down)
//      close BELOW Fair Price  ->  ArmedDirection = +1  (long,  reverting up)
//
//  An armed setup is never re-armed, so its lifetime is decided purely by the
//  invalidation rules below.
//
//  INVALIDATION — the setup ends, through any number of trades, only when:
//
//    * price CLOSES on the far side of Fair Price. The reversion this setup
//      existed to trade has happened; there is nothing left to revert to.
//    * the session restarts, because Fair Price itself changes there.
//    * Fair Price is lost, or the feature is switched off.
//
//  WHAT IS DELIBERATELY ABSENT
//  There is no trade counter. The previous build refilled a Y-trade budget on
//  every bar price was still beyond the trigger, so the setup expired on an
//  arbitrary count that had nothing to do with the market. It now ends when the
//  condition that made it valid disappears, and not before.
//
//  The Fair Price target is skipped when it would sit on the wrong side of the
//  entry — that is a mechanical impossibility (an instantly filled or negative
//  reward target), not a policy choice. Sub-1R targets ARE taken.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public struct ExtendedTpResult
	{
		public double TakeProfit;
		public bool   OverrideUsed;
	}

	/// <summary>What changed on this bar, for logging.</summary>
	public enum FpXtpTransition { None, Armed, Invalidated }

	public sealed class ExtendedTpEngine
	{
		private readonly bool             _enabled;
		private readonly double           _triggerPercent;
		private readonly FpExtendedTpMode _mode;

		/// <summary>-1 short setup armed, +1 long setup armed, 0 idle.</summary>
		public int    ArmedDirection  { get; private set; }
		public double DistancePercent { get; private set; }

		public bool Armed   { get { return ArmedDirection != 0; } }
		public bool Enabled { get { return _enabled; } }

		public ExtendedTpEngine(bool enabled, double triggerPercent, FpExtendedTpMode mode)
		{
			_enabled        = enabled;
			_triggerPercent = triggerPercent;
			_mode           = mode;
			ArmedDirection  = 0;
			DistancePercent = double.NaN;
		}

		public void OnSessionStart()
		{
			ArmedDirection = 0;
		}

		public void Reset()
		{
			ArmedDirection  = 0;
			DistancePercent = double.NaN;
		}

		/// <summary>
		/// Call once per primary bar, BEFORE entry evaluation — the entry gate reads
		/// the state this produces.
		/// </summary>
		public FpXtpTransition OnBar(double close, double fairPrice, bool hasFairPrice)
		{
			DistancePercent = (hasFairPrice && Math.Abs(fairPrice) > double.Epsilon)
				? Math.Abs(close - fairPrice) / fairPrice * 100.0
				: double.NaN;

			if (!_enabled || !hasFairPrice || double.IsNaN(DistancePercent))
			{
				bool wasArmed  = Armed;
				ArmedDirection = 0;
				return wasArmed ? FpXtpTransition.Invalidated : FpXtpTransition.None;
			}

			// The direction a reversion trade would take from here.
			int side = close > fairPrice ? -1 : close < fairPrice ? 1 : 0;

			// INVALIDATION is tested first, so a bar that crosses Fair Price can never
			// also re-arm on the far side within the same bar.
			if (Armed && side != 0 && side != ArmedDirection)
			{
				ArmedDirection = 0;
				return FpXtpTransition.Invalidated;
			}

			// A close exactly ON Fair Price is not a crossing and leaves the setup alone.
			if (!Armed && side != 0 && DistancePercent >= _triggerPercent)
			{
				ArmedDirection = side;
				return FpXtpTransition.Armed;
			}

			return FpXtpTransition.None;
		}

		/// <summary>
		/// Final take-profit for a trade. <paramref name="direction"/> is +1 long, -1 short.
		/// <paramref name="offset"/> pulls the Fair Price target back toward the entry.
		/// </summary>
		public ExtendedTpResult ComputeTakeProfit(int direction, double entry, double risk, double rewardRatio,
		                                          double fairPrice, bool hasFairPrice, double offset, double tickSize)
		{
			double rrTarget = direction < 0 ? entry - risk * rewardRatio : entry + risk * rewardRatio;

			ExtendedTpResult result = new ExtendedTpResult { TakeProfit = rrTarget, OverrideUsed = false };

			// The direction test is belt-and-braces: the entry gate already refuses a
			// break running against the armed side.
			if (!_enabled || !Armed || direction != ArmedDirection || !hasFairPrice)
				return result;

			double fpTarget = direction < 0 ? fairPrice + offset : fairPrice - offset;

			bool viable = direction < 0 ? fpTarget < entry - tickSize : fpTarget > entry + tickSize;
			if (!viable)
				return result;

			result.OverrideUsed = true;

			switch (_mode)
			{
				case FpExtendedTpMode.FairPriceAlways:
					result.TakeProfit = fpTarget;
					break;

				case FpExtendedTpMode.NearerOfTheTwo:
					result.TakeProfit = direction < 0 ? Math.Max(fpTarget, rrTarget) : Math.Min(fpTarget, rrTarget);
					break;

				case FpExtendedTpMode.FartherOfTheTwo:
					result.TakeProfit = direction < 0 ? Math.Min(fpTarget, rrTarget) : Math.Max(fpTarget, rrTarget);
					break;
			}

			return result;
		}

		/// <summary>One-line state, for the log and the on-chart panel.</summary>
		public string Describe()
		{
			if (!_enabled)
				return "off";

			string dist = double.IsNaN(DistancePercent)
				? "-"
				: DistancePercent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%";

			if (!Armed)
				return "idle (" + dist + ")";

			return (ArmedDirection < 0 ? "ARMED short" : "ARMED long") + " (" + dist + ", waiting for a BOS)";
		}
	}
}
