// =============================================================================
//  VPS · VpsSweepTracker
//
//  Detects the liquidity sweep and holds it open for confirmation.
//
//  WHAT COUNTS AS A SWEEP (specification section 10)
//  A candle must physically TRADE THROUGH the level: high > level on the high
//  side, low < level on the low side. A close near the level is not a sweep.
//
//  FRESH PENETRATION ONLY
//  The sweep must be a new excursion through the level — the previous bar must
//  NOT already have been through it. Without that test a sustained move above
//  VAH would re-arm on every bar, which is acceptance rather than a sweep, and
//  it is also what stops one liquidity event from producing several entries
//  (specification section 13, "do not generate duplicate trades").
//
//  THE STOP BELONGS TO THE SWEEP CANDLE
//  The armed record keeps the SWEEP candle's own extreme. When a LATER candle
//  confirms the setup, the stop still comes from the candle that actually did
//  the sweeping, exactly as section 3 requires.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	public sealed class VpsArmedSweep
	{
		public VpsLevel Level;
		/// <summary>+1 for a low-side sweep (long setup), -1 for a high-side sweep (short setup).</summary>
		public int      Direction;
		/// <summary>The level price at the moment of the sweep.</summary>
		public double   LevelPrice;
		/// <summary>The sweep candle's own extreme — the initial stop.</summary>
		public double   SweepExtreme;
		public int      SweepBar;
		public DateTime SweepTime;
	}

	public sealed class VpsSweepTracker
	{
		private readonly int    _confirmWindowBars;
		private readonly double _proximity;

		/// <summary>One armed sweep per level; a fresh sweep of the same level replaces it.</summary>
		private readonly VpsArmedSweep[] _armed = new VpsArmedSweep[6];

		public VpsSweepTracker(int confirmWindowBars, double proximityPrice)
		{
			_confirmWindowBars = Math.Max(0, confirmWindowBars);
			_proximity         = Math.Max(0.0, proximityPrice);
		}

		public void Reset()
		{
			for (int i = 0; i < _armed.Length; i++)
				_armed[i] = null;
		}

		public VpsArmedSweep ArmedFor(VpsLevel level)
		{
			return _armed[(int)level];
		}

		public void Clear(VpsLevel level)
		{
			_armed[(int)level] = null;
		}

		/// <summary>Drops every sweep whose confirmation window has run out.</summary>
		public void Expire(int currentBar)
		{
			for (int i = 0; i < _armed.Length; i++)
			{
				VpsArmedSweep a = _armed[i];
				if (a != null && currentBar - a.SweepBar > _confirmWindowBars)
					_armed[i] = null;
			}
		}

		/// <summary>
		/// Tests one level for a fresh penetration on this bar and arms it if found.
		/// <paramref name="prevExtreme"/> is the previous bar's high (high side) or
		/// low (low side); it is what makes the penetration "fresh".
		/// </summary>
		public VpsArmedSweep DetectSweep(VpsLevel level, int direction, double levelPrice,
		                                 double barHigh, double barLow, double prevHigh, double prevLow,
		                                 int currentBar, DateTime barTime)
		{
			if (double.IsNaN(levelPrice))
				return null;

			bool through;
			bool wasThrough;

			if (direction < 0)
			{
				// High-side: this bar traded above the level, the previous one did not.
				through    = barHigh  > levelPrice;
				wasThrough = prevHigh > levelPrice;
			}
			else
			{
				through    = barLow  < levelPrice;
				wasThrough = prevLow < levelPrice;
			}

			if (!through || wasThrough)
				return null;

			VpsArmedSweep sweep = new VpsArmedSweep
			{
				Level        = level,
				Direction    = direction,
				LevelPrice   = levelPrice,
				SweepExtreme = direction < 0 ? barHigh : barLow,
				SweepBar     = currentBar,
				SweepTime    = barTime
			};

			_armed[(int)level] = sweep;
			return sweep;
		}

		/// <summary>
		/// THE confirmation. After a level has been swept, the setup is confirmed by
		/// a candle that does BOTH of these:
		///
		///   * closes in the rejecting COLOUR — red (close &lt; open) for a high-side
		///     sweep, green (close &gt; open) for a low-side sweep, and
		///   * closes back THROUGH the swept level — below it on the high side,
		///     above it on the low side.
		///
		/// The colour alone is not enough: a red candle that still closes above VAH
		/// is continuation, not rejection. Closing back through alone is not enough
		/// either: a green candle closing just under VAH is buyers holding the level.
		/// Both together are the rejection the specification describes.
		///
		/// The sweep candle itself qualifies when it satisfies both, so a candle that
		/// wicks through the level and closes red back inside is confirmed on the
		/// spot rather than waiting a bar.
		/// </summary>
		public static bool IsColourRejection(int direction, double open, double close, double levelPrice)
		{
			return direction < 0
				? close < open && close < levelPrice
				: close > open && close > levelPrice;
		}

		/// <summary>
		/// "At/around the swept level": the candle must have reached back into a band
		/// around it, rather than merely printing a red or green candle somewhere else.
		/// </summary>
		public bool IsNearLevel(int direction, double levelPrice, double high, double low)
		{
			return direction < 0 ? high >= levelPrice - _proximity
			                     : low  <= levelPrice + _proximity;
		}
	}
}
