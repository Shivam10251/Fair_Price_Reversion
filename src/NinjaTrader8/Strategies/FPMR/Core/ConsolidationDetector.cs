// =============================================================================
//  FPMR · Core · ConsolidationDetector
//
//  Mechanical definition of "the market has accepted a new price".
//
//  After a news displacement the pre-news price is no longer defensible as Fair
//  Price, so the strategy has to wait for the market to agree on a new one. The
//  rule used here is RANGE COMPRESSION: the first run of WindowBars consecutive
//  reference candles whose combined high-low range fits inside MaxRangeTicks.
//  The midpoint of that run becomes the new Fair Price.
//
//  DESIGN NOTES
//    1. Fed on the REFERENCE timeframe, same as FairPriceEngine, so "bars" here
//       always means reference candles and never chart candles.
//    2. The window is a fixed-size ring buffer, so the per-bar cost is O(window)
//       with no allocation once armed. Nothing scans history.
//    3. Arm() records the bar the displacement happened on. Detection can only
//       ever look at candles that have already closed, so no look-ahead is
//       structurally possible.
//    4. The search gives up after MaxSearchBars. A release that never settles
//       must not leave the strategy waiting for the rest of the session.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class ConsolidationResult
	{
		/// <summary>A qualifying window closed on this bar.</summary>
		public bool   Found;
		/// <summary>The search ran past MaxSearchBars without finding one.</summary>
		public bool   Expired;
		/// <summary>Midpoint of the accepted range — the new Fair Price.</summary>
		public double FairPrice;
		public double High;
		public double Low;
		public int    StartBarIndex;
		public int    EndBarIndex;

		public static readonly ConsolidationResult None = new ConsolidationResult();
	}

	public sealed class ConsolidationDetector
	{
		private readonly int    _windowBars;
		private readonly double _maxRange;
		private readonly int    _maxSearchBars;

		private readonly double[] _highs;
		private readonly double[] _lows;
		private readonly int[]    _barIndex;

		private int  _count;      // how many bars have entered the ring since arming
		private int  _head;       // next write slot
		private bool _armed;
		private int  _armBarIndex;

		/// <param name="windowBars">Consecutive candles that must fit inside the range.</param>
		/// <param name="maxRangePoints">Maximum high-low span of that window, in price.</param>
		/// <param name="maxSearchBars">Give up this many bars after arming.</param>
		public ConsolidationDetector(int windowBars, double maxRangePoints, int maxSearchBars)
		{
			_windowBars    = Math.Max(1, windowBars);
			_maxRange      = Math.Max(0.0, maxRangePoints);
			_maxSearchBars = Math.Max(_windowBars, maxSearchBars);

			_highs    = new double[_windowBars];
			_lows     = new double[_windowBars];
			_barIndex = new int[_windowBars];
		}

		public bool IsArmed { get { return _armed; } }

		/// <summary>Begins looking for acceptance, starting with the NEXT bar fed in.</summary>
		public void Arm(int displacementBarIndex)
		{
			_armed       = true;
			_armBarIndex = displacementBarIndex;
			_count       = 0;
			_head        = 0;
		}

		public void Reset()
		{
			_armed = false;
			_count = 0;
			_head  = 0;
		}

		/// <summary>
		/// Feeds one closed reference candle. Returns a result whose Found is true on
		/// the bar the window completes, or whose Expired is true when the search is
		/// abandoned. Disarms itself in both cases.
		/// </summary>
		public ConsolidationResult OnBar(int barIndex, double high, double low)
		{
			if (!_armed)
				return ConsolidationResult.None;

			// The arming bar is the displacement candle itself. It is the thing the
			// market is reacting to, never part of the range that accepts a new price,
			// so it must not enter the window.
			if (barIndex <= _armBarIndex)
				return ConsolidationResult.None;

			if (barIndex - _armBarIndex > _maxSearchBars)
			{
				_armed = false;
				return new ConsolidationResult { Expired = true };
			}

			_highs[_head]    = high;
			_lows[_head]     = low;
			_barIndex[_head] = barIndex;
			_head            = (_head + 1) % _windowBars;
			if (_count < _windowBars)
				_count++;

			if (_count < _windowBars)
				return ConsolidationResult.None;

			double hi = double.MinValue;
			double lo = double.MaxValue;
			int    firstBar = int.MaxValue;
			int    lastBar  = int.MinValue;

			for (int i = 0; i < _windowBars; i++)
			{
				if (_highs[i] > hi) hi = _highs[i];
				if (_lows[i]  < lo) lo = _lows[i];
				if (_barIndex[i] < firstBar) firstBar = _barIndex[i];
				if (_barIndex[i] > lastBar)  lastBar  = _barIndex[i];
			}

			if (hi - lo > _maxRange)
				return ConsolidationResult.None;

			_armed = false;

			return new ConsolidationResult
			{
				Found         = true,
				FairPrice     = (hi + lo) / 2.0,
				High          = hi,
				Low           = lo,
				StartBarIndex = firstBar,
				EndBarIndex   = lastBar
			};
		}
	}
}
