// =============================================================================
//  FPMR · News · NewsEvent
//  One economic calendar release, normalised into the strategy's session zone.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class NewsEvent
	{
		/// <summary>Release time expressed in the strategy's SESSION time zone.</summary>
		public DateTime     TimeSessionTz { get; set; }
		/// <summary>Currency / country code as written in the file (USD, EUR, ...).</summary>
		public string       Currency      { get; set; }
		public FpNewsImpact Impact        { get; set; }
		public string       Title         { get; set; }
		/// <summary>Verbatim source line or object, kept for log messages.</summary>
		public string       Source        { get; set; }

		// ── Forecast / actual ─────────────────────────────────────────────────────
		// Kept both raw and parsed. The raw strings go into the event report so a
		// mis-parsed column is obvious; the doubles drive the classification.
		public string ForecastRaw { get; set; }
		public string ActualRaw   { get; set; }
		public string PreviousRaw { get; set; }

		public double Forecast { get; set; }
		public double Actual   { get; set; }
		public double Previous { get; set; }

		// ── Live values from NinjaTrader's economic calendar ──────────────────────
		// The file is the SCHEDULE; NinjaTrader pushes the NUMBER as the release
		// prints. When a push has been matched to this row it supersedes whatever the
		// file's own columns said, because the file was written before the release.
		public double FeedActual   { get; set; }
		public double FeedForecast { get; set; }
		/// <summary>Set once a NinjaTrader calendar push has been matched to this row.</summary>
		public bool   FeedMatched  { get; set; }
		/// <summary>How the effective actual was obtained, for the event report.</summary>
		public string ActualOrigin { get; set; }

		/// <summary>Actual in force: the live feed's value when one arrived, else the file's.</summary>
		public double EffectiveActual
		{
			get { return FeedMatched && !double.IsNaN(FeedActual) ? FeedActual : Actual; }
		}

		/// <summary>Forecast in force. The feed's consensus is preferred only when the file has none.</summary>
		public double EffectiveForecast
		{
			get { return !double.IsNaN(Forecast) ? Forecast : FeedForecast; }
		}

		public bool HasForecast { get { return !double.IsNaN(EffectiveForecast); } }
		public bool HasActual   { get { return !double.IsNaN(EffectiveActual);   } }
		public bool HasPrevious { get { return !double.IsNaN(Previous); } }

		public NewsEvent()
		{
			Forecast     = double.NaN;
			Actual       = double.NaN;
			Previous     = double.NaN;
			FeedActual   = double.NaN;
			FeedForecast = double.NaN;
			ActualOrigin = "none";
		}

		/// <summary>
		/// Applies a matched NinjaTrader calendar push. Called at most once per release,
		/// from the bar thread, just before the release is classified.
		/// </summary>
		public void ApplyFeed(Nt8EconomicRelease r)
		{
			if (r == null)
				return;

			FeedActual   = r.Actual;
			FeedForecast = r.Consensus;
			FeedMatched  = true;
			ActualOrigin = "NinjaTrader calendar (" + r.EventName + ")";
		}

		public bool MatchesImpact(FpNewsImpactFilter filter)
		{
			switch (filter)
			{
				case FpNewsImpactFilter.HighOnly:   return Impact == FpNewsImpact.High;
				case FpNewsImpactFilter.MediumOnly: return Impact == FpNewsImpact.Medium;
				case FpNewsImpactFilter.Both:       return Impact == FpNewsImpact.High || Impact == FpNewsImpact.Medium;
				default:                            return false;
			}
		}

		/// <summary><paramref name="allowed"/> is a comma or space separated list, e.g. "USD,EUR".</summary>
		public bool MatchesCurrency(string[] allowed)
		{
			if (allowed == null || allowed.Length == 0)
				return true;
			if (string.IsNullOrEmpty(Currency))
				return false;

			for (int i = 0; i < allowed.Length; i++)
				if (string.Equals(allowed[i], Currency, StringComparison.OrdinalIgnoreCase))
					return true;

			return false;
		}

		public override string ToString()
		{
			string actualText = FeedMatched && !double.IsNaN(FeedActual)
				? FeedActual.ToString("0.####", CultureInfo.InvariantCulture) + " [live]"
				: (ActualRaw ?? "-");

			string values = HasForecast || HasActual
				? "  (forecast " + (ForecastRaw ?? "-") + ", actual " + actualText + ")"
				: string.Empty;

			return TimeSessionTz.ToString("yyyy-MM-dd HH:mm") + " " + Currency + " [" + Impact + "] " + Title + values;
		}
	}
}
