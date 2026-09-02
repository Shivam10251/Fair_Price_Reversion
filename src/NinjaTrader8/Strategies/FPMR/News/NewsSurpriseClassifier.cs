// =============================================================================
//  FPMR · News · NewsSurpriseClassifier
//
//  Answers the only question the news branch actually cares about:
//  was the release close to what the market already expected?
//
//  The strategy deliberately does NOT decide whether news is "good" or "bad".
//  Sentiment is not a signal here. The inputs are forecast, actual, and the size
//  of the gap between them.
//
//  NUMERIC PARSING
//  Calendar files write values as humans read them: "3.2%", "1,250K", "-0.4",
//  "(0.3)", "<0.1", "225B". ParseValue normalises all of that to a plain double.
//  Scale suffixes are applied so a K figure is never compared against a raw one.
//
//  BASIS FOR THE PERCENTAGE
//  Deviation is expressed against max(|forecast|, |actual|) rather than against
//  the forecast alone. A forecast of 0.0 is common (rate holds, flat CPI) and
//  dividing by it would make every such release an infinite surprise.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class NewsSurpriseResult
	{
		public FpNewsSurprise Surprise;
		/// <summary>|actual - forecast| as a percentage of the larger magnitude, NaN when unknown.</summary>
		public double         DeviationPercent = double.NaN;
		/// <summary>True when the classification came from the unknown-value rule, not from real numbers.</summary>
		public bool           FromUnknownRule;

		public string Describe()
		{
			return Surprise + (double.IsNaN(DeviationPercent)
				? " (no forecast/actual in file)"
				: " (" + DeviationPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% from forecast)");
		}
	}

	public static class NewsSurpriseClassifier
	{
		/// <param name="expectedTolerancePercent">At or below this, actual counts as matching forecast.</param>
		/// <param name="unexpectedThresholdPercent">Above this, the surprise counts as large.</param>
		public static NewsSurpriseResult Classify(NewsEvent ev,
		                                          double expectedTolerancePercent,
		                                          double unexpectedThresholdPercent,
		                                          FpNewsUnknownRule unknownRule)
		{
			NewsSurpriseResult r = new NewsSurpriseResult();

			if (ev == null || !ev.HasForecast || !ev.HasActual)
			{
				r.FromUnknownRule = true;
				switch (unknownRule)
				{
					case FpNewsUnknownRule.TreatAsUnexpected: r.Surprise = FpNewsSurprise.Unexpected; break;
					case FpNewsUnknownRule.SkipEvent:         r.Surprise = FpNewsSurprise.Unknown;    break;
					default:                                  r.Surprise = FpNewsSurprise.Expected;   break;
				}
				return r;
			}

			double basis = Math.Max(Math.Abs(ev.Forecast), Math.Abs(ev.Actual));

			// Both values are exactly zero: identical, therefore no surprise.
			if (basis <= double.Epsilon)
			{
				r.DeviationPercent = 0.0;
				r.Surprise         = FpNewsSurprise.Expected;
				return r;
			}

			r.DeviationPercent = 100.0 * Math.Abs(ev.Actual - ev.Forecast) / basis;

			double tol  = Math.Max(0.0, expectedTolerancePercent);
			double big  = Math.Max(tol, unexpectedThresholdPercent);

			r.Surprise = r.DeviationPercent <= tol ? FpNewsSurprise.Expected
			           : r.DeviationPercent <= big ? FpNewsSurprise.PartiallyUnexpected
			           :                             FpNewsSurprise.Unexpected;
			return r;
		}

		/// <summary>Maps a classification onto what the strategy should do about it.</summary>
		public static FpNewsBias BiasFor(FpNewsSurprise surprise, bool continuationEnabled)
		{
			switch (surprise)
			{
				// Priced in. Pre-news price stays fair; fade the displacement.
				case FpNewsSurprise.Expected:
					return FpNewsBias.Reversion;

				// The market may have legitimately repriced. Do not assume the old
				// level is still fair — wait for a new one, then revert toward it.
				case FpNewsSurprise.PartiallyUnexpected:
					return FpNewsBias.Wait;

				// Large surprise: the initial move is legitimate repricing, so the
				// first trade with it rather than against it.
				case FpNewsSurprise.Unexpected:
					return continuationEnabled ? FpNewsBias.Continuation : FpNewsBias.Wait;

				default:
					return FpNewsBias.Wait;
			}
		}

		/// <summary>
		/// Parses a calendar value into a number. Handles percent signs, thousands
		/// separators, K/M/B/T scale suffixes, accounting negatives "(0.3)" and the
		/// approximate prefixes "&lt;", "&gt;", "~". Returns NaN when there is no number.
		/// </summary>
		public static double ParseValue(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return double.NaN;

			string s = raw.Trim();

			bool negative = false;
			if (s.Length > 2 && s[0] == '(' && s[s.Length - 1] == ')')
			{
				negative = true;
				s = s.Substring(1, s.Length - 2).Trim();
			}

			// Strip comparison / approximation prefixes.
			while (s.Length > 0 && (s[0] == '<' || s[0] == '>' || s[0] == '~' || s[0] == '='))
				s = s.Substring(1).Trim();

			if (s.Length == 0)
				return double.NaN;

			double scale = 1.0;
			char last = char.ToUpperInvariant(s[s.Length - 1]);

			if (last == '%')
			{
				// A percentage is compared against another percentage, so the unit
				// cancels. Keep the face value rather than dividing by 100.
				s = s.Substring(0, s.Length - 1).Trim();
			}
			else if (last == 'K' || last == 'M' || last == 'B' || last == 'T')
			{
				scale = last == 'K' ? 1e3 : last == 'M' ? 1e6 : last == 'B' ? 1e9 : 1e12;
				s = s.Substring(0, s.Length - 1).Trim();
			}

			s = s.Replace(",", string.Empty);

			double value;
			if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
				return double.NaN;

			if (negative)
				value = -value;

			return value * scale;
		}
	}
}
