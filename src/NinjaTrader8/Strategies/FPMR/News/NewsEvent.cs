// =============================================================================
//  FPMR · News · NewsEvent
//  One economic calendar release, normalised into the strategy's session zone.
// =============================================================================
using System;

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
			return TimeSessionTz.ToString("yyyy-MM-dd HH:mm") + " " + Currency + " [" + Impact + "] " + Title;
		}
	}
}
