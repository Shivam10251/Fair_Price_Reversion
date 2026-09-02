// =============================================================================
//  VPS · VolumeProfileEngine
//
//  A LINE-FOR-LINE port of f_profile() from the supplied Pine indicator
//  ("Session VP + Range + Volume + VWAP + EMA", v12). The specification asks for
//  the indicator's own outputs; Pine cannot be called from NinjaScript, so the
//  algorithm is reproduced exactly rather than approximated. Every step below
//  mirrors the Pine source, including its rounding and edge behaviour:
//
//    top = max(high), bot = min(low), binSize = (top - bot) / bins
//
//    For each collected bar:
//      br = high - low
//      br <= 0  -> the whole volume lands in the single bin containing `high`
//      br  > 0  -> the volume is spread across every bin the bar's range
//                  overlaps, weighted by overlap / br
//
//    POC = the heaviest bin, MIDPOINT   -> bot + (pocIdx + 0.5) * binSize
//    The value area expands outward from the POC bin, each step taking whichever
//    neighbour holds more volume, until vaPct of total volume is enclosed.
//    VAH = the TOP EDGE of the highest bin       -> bot + (up + 1) * binSize
//    VAL = the BOTTOM EDGE of the lowest bin     -> bot + dn * binSize
//
//  Two details that are easy to get wrong and are preserved deliberately:
//    * VAH and VAL are bin EDGES while POC is a bin MIDPOINT. They are not
//      symmetric around the POC and must not be "tidied" into midpoints.
//    * The POC scan uses a strict > comparison, so on a tie the LOWEST bin wins,
//      exactly as the Pine loop does.
//
//  NO LOOK-AHEAD: Compute() is only ever called once a session has fully closed,
//  and it reads nothing but bars that have already printed.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	public sealed class VpProfileResult
	{
		public bool   Valid;
		public double Poc;
		public double Vah;
		public double Val;
		public double Bottom;
		public double Top;
		public double BinSize;
		public double TotalVolume;
		public int    Samples;
	}

	public sealed class VolumeProfileEngine
	{
		private readonly List<double> _high = new List<double>();
		private readonly List<double> _low  = new List<double>();
		private readonly List<double> _vol  = new List<double>();

		private readonly int    _bins;
		private readonly double _valueAreaPercent;

		public VolumeProfileEngine(int bins, double valueAreaPercent)
		{
			_bins             = Math.Max(5, bins);
			_valueAreaPercent = Math.Max(1.0, Math.Min(100.0, valueAreaPercent));
		}

		public int SampleCount { get { return _high.Count; } }

		public void Reset()
		{
			_high.Clear();
			_low.Clear();
			_vol.Clear();
		}

		/// <summary>Collects one closed bar of the profile session.</summary>
		public void Add(double high, double low, double volume)
		{
			_high.Add(high);
			_low.Add(low);
			_vol.Add(double.IsNaN(volume) ? 0.0 : volume);
		}

		public VpProfileResult Compute()
		{
			VpProfileResult r = new VpProfileResult { Samples = _high.Count };

			int n = _high.Count;
			if (n == 0)
				return r;

			double top = double.MinValue;
			double bot = double.MaxValue;

			for (int i = 0; i < n; i++)
			{
				if (_high[i] > top) top = _high[i];
				if (_low[i]  < bot) bot = _low[i];
			}

			double rng = top - bot;
			double bs  = rng / _bins;

			r.Bottom  = bot;
			r.Top     = top;
			r.BinSize = bs;

			// A session whose every bar printed at one price has no profile.
			if (rng <= 0.0)
				return r;

			double[] binVol = new double[_bins];

			for (int i = 0; i < n; i++)
			{
				double h = _high[i];
				double l = _low[i];
				double v = _vol[i];
				double br = h - l;

				if (br <= 0.0)
				{
					int idx = Clamp((int)Math.Floor((h - bot) / bs));
					binVol[idx] += v;
				}
				else
				{
					int i0 = Clamp((int)Math.Floor((l - bot) / bs));
					int i1 = Clamp((int)Math.Floor((h - bot) / bs));

					for (int b = i0; b <= i1; b++)
					{
						double bl = bot + b * bs;
						double bh = bl + bs;
						double ov = Math.Min(h, bh) - Math.Max(l, bl);

						if (ov > 0.0)
							binVol[b] += v * ov / br;
					}
				}
			}

			double totalV = 0.0;
			for (int b = 0; b < _bins; b++)
				totalV += binVol[b];

			r.TotalVolume = totalV;

			if (totalV <= 0.0)
				return r;

			// POC. Strict > keeps the LOWEST bin on a tie, as the Pine loop does.
			int    pocIdx = 0;
			double maxV   = -1.0;

			for (int b = 0; b < _bins; b++)
			{
				if (binVol[b] > maxV)
				{
					maxV   = binVol[b];
					pocIdx = b;
				}
			}

			// Value area: expand outward from the POC, always taking the heavier
			// neighbour, until the target share of volume is enclosed.
			double target = totalV * _valueAreaPercent / 100.0;
			double acc    = maxV;
			int    up     = pocIdx;
			int    dn     = pocIdx;

			for (int i = 0; i < _bins; i++)
			{
				if (acc >= target)
					break;

				double vUp = up < _bins - 1 ? binVol[up + 1] : -1.0;
				double vDn = dn > 0         ? binVol[dn - 1] : -1.0;

				if (vUp < 0.0 && vDn < 0.0)
					break;                       // both edges reached

				if (vUp >= vDn)
				{
					up  = up + 1;
					acc = acc + vUp;
				}
				else
				{
					dn  = dn - 1;
					acc = acc + vDn;
				}
			}

			r.Poc   = bot + (pocIdx + 0.5) * bs;
			r.Vah   = bot + (up + 1) * bs;
			r.Val   = bot + dn * bs;
			r.Valid = true;

			return r;
		}

		private int Clamp(int idx)
		{
			return Math.Max(0, Math.Min(_bins - 1, idx));
		}
	}
}
