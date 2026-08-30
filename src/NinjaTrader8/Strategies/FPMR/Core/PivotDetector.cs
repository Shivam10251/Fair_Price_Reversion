// =============================================================================
//  FPMR · Core · PivotDetector
//
//  Confirmed swing detection, equivalent to Pine's ta.pivothigh / ta.pivotlow.
//  A pivot is only ever reported RightBars after the bar that formed it, which
//  is the whole reason the strategy cannot repaint.
//
//  Platform-free by design: the caller supplies bars-ago accessors, so the same
//  code drives the live series, a replay and any future unit test.
//
//  TIE HANDLING: the pivot must be a STRICT extreme on both sides. An equal high
//  inside the window disqualifies it. This matches Pine, where a flat double top
//  produces no pivot until one side is exceeded.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public struct PivotResult
	{
		public bool   Found;
		public double Price;
		/// <summary>Bars ago of the bar that formed the pivot, relative to the current bar.</summary>
		public int    BarsAgo;

		public static readonly PivotResult None = new PivotResult { Found = false, Price = double.NaN, BarsAgo = 0 };
	}

	public static class PivotDetector
	{
		/// <summary>Bars of history required before any pivot can be evaluated.</summary>
		public static int RequiredBars(int leftBars, int rightBars)
		{
			return leftBars + rightBars + 1;
		}

		/// <summary>
		/// Tests whether the bar sitting <paramref name="rightBars"/> bars ago is a pivot high.
		/// <paramref name="highAt"/> takes a bars-ago offset, exactly like NinjaTrader's High[i].
		/// </summary>
		public static PivotResult PivotHigh(Func<int, double> highAt, int leftBars, int rightBars, int barsAvailable)
		{
			if (highAt == null || leftBars < 1 || rightBars < 1)
				return PivotResult.None;
			if (barsAvailable < RequiredBars(leftBars, rightBars))
				return PivotResult.None;

			double candidate = highAt(rightBars);
			if (double.IsNaN(candidate))
				return PivotResult.None;

			for (int i = 1; i <= leftBars; i++)
				if (highAt(rightBars + i) >= candidate)
					return PivotResult.None;

			for (int i = 1; i <= rightBars; i++)
				if (highAt(rightBars - i) >= candidate)
					return PivotResult.None;

			return new PivotResult { Found = true, Price = candidate, BarsAgo = rightBars };
		}

		/// <summary>Mirror of <see cref="PivotHigh"/> for swing lows.</summary>
		public static PivotResult PivotLow(Func<int, double> lowAt, int leftBars, int rightBars, int barsAvailable)
		{
			if (lowAt == null || leftBars < 1 || rightBars < 1)
				return PivotResult.None;
			if (barsAvailable < RequiredBars(leftBars, rightBars))
				return PivotResult.None;

			double candidate = lowAt(rightBars);
			if (double.IsNaN(candidate))
				return PivotResult.None;

			for (int i = 1; i <= leftBars; i++)
				if (lowAt(rightBars + i) <= candidate)
					return PivotResult.None;

			for (int i = 1; i <= rightBars; i++)
				if (lowAt(rightBars - i) <= candidate)
					return PivotResult.None;

			return new PivotResult { Found = true, Price = candidate, BarsAgo = rightBars };
		}
	}
}
