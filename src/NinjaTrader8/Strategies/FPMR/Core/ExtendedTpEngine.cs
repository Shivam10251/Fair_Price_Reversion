// =============================================================================
//  FPMR · Core · ExtendedTpEngine
//
//  The extended-move TP override (Pine group 4b), ported unchanged.
//
//  When price has stretched X% away from Fair Price, the next Y trades target the
//  Fair Price line instead of the plain RR multiple. The counter REFILLS to Y on
//  every bar price is still beyond the trigger, and resets to 0 at session start
//  because Fair Price itself changes there.
//
//  The override is skipped when the Fair Price target would sit on the wrong side
//  of the entry — that is a mechanical impossibility (an instantly filled or
//  negative-reward target), not a policy choice. Sub-1R targets ARE taken.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public struct ExtendedTpResult
	{
		public double TakeProfit;
		public bool   OverrideUsed;
	}

	public sealed class ExtendedTpEngine
	{
		private readonly bool             _enabled;
		private readonly double           _triggerPercent;
		private readonly int              _refillCount;
		private readonly FpExtendedTpMode _mode;

		public int    Remaining      { get; private set; }
		public double DistancePercent{ get; private set; }
		public bool   Armed          { get; private set; }

		public ExtendedTpEngine(bool enabled, double triggerPercent, int refillCount, FpExtendedTpMode mode)
		{
			_enabled        = enabled;
			_triggerPercent = triggerPercent;
			_refillCount    = Math.Max(1, refillCount);
			_mode           = mode;
			DistancePercent = double.NaN;
		}

		public void OnSessionStart()
		{
			Remaining = 0;
		}

		public void Reset()
		{
			Remaining       = 0;
			Armed           = false;
			DistancePercent = double.NaN;
		}

		/// <summary>Call once per primary bar, before entry evaluation.</summary>
		public void OnBar(double close, double fairPrice, bool hasFairPrice)
		{
			DistancePercent = (hasFairPrice && Math.Abs(fairPrice) > double.Epsilon)
				? Math.Abs(close - fairPrice) / fairPrice * 100.0
				: double.NaN;

			Armed = _enabled && hasFairPrice && !double.IsNaN(DistancePercent) && DistancePercent >= _triggerPercent;

			if (Armed)
				Remaining = _refillCount;
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

			if (!_enabled || Remaining <= 0 || !hasFairPrice)
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

		/// <summary>Consumes one of the armed trades. Call only when an order is actually submitted.</summary>
		public void Consume()
		{
			if (Remaining > 0)
				Remaining--;
		}
	}
}
