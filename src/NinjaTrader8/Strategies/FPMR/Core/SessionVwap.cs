// =============================================================================
//  FPMR · Core · SessionVwap
//
//  Session-anchored VWAP, computed here rather than borrowed from an indicator so
//  the anchor matches the Pine version exactly: it resets on session start and on
//  a new calendar day, both evaluated in the SESSION time zone.
//
//  NinjaTrader's bundled VWAP is order-flow licensed and anchors to the
//  instrument's trading session, neither of which matches the spec.
//
//  Instruments with no volume produce NaN, and the filter then passes — the same
//  behaviour as Pine's na(vwapVal) guard.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class SessionVwap
	{
		private double _cumulativePriceVolume;
		private double _cumulativeVolume;

		public double Value { get; private set; }

		public SessionVwap()
		{
			Reset();
		}

		public bool HasValue { get { return !double.IsNaN(Value); } }

		public void Reset()
		{
			_cumulativePriceVolume = 0.0;
			_cumulativeVolume      = 0.0;
			Value                  = double.NaN;
		}

		/// <summary>Call once per primary bar. <paramref name="anchor"/> restarts the accumulation.</summary>
		public void OnBar(double high, double low, double close, double volume, bool anchor)
		{
			if (anchor)
			{
				_cumulativePriceVolume = 0.0;
				_cumulativeVolume      = 0.0;
			}

			double typical = (high + low + close) / 3.0;

			if (volume > 0.0)
			{
				_cumulativePriceVolume += typical * volume;
				_cumulativeVolume      += volume;
			}

			Value = _cumulativeVolume > 0.0 ? _cumulativePriceVolume / _cumulativeVolume : double.NaN;
		}
	}
}
