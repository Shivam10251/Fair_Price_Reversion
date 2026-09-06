// =============================================================================
//  FPMR · Core · SetupBands
//
//  The distance-band setup model (replaces the old flat R:R and the 4b
//  extended-move TP override).
//
//  An entry's distance from Fair Price, measured as a PERCENTAGE of Fair Price,
//  decides both whether the trade is taken and how its take-profit is chosen:
//
//      |entry - FairPrice| / FairPrice * 100  =  dist%
//
//         dist% <= zone%           -> None   : inside the non-tradeable zone
//         zone% <  dist% <= band1% -> Near   : take profit at the R:R multiple
//         band1% < dist% <= band2% -> Far    : take profit at Fair Price
//         dist% >  band2%          -> Beyond : too far from Fair Price, no trade
//
//  The three percentages are validated at load time to be strictly ordered
//  (zone% < band1% < band2%), so the four cases above never overlap.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public static class SetupBands
	{
		/// <summary>
		/// Classifies an entry price against Fair Price using the three percentage
		/// thresholds. Returns <see cref="FpSetupBand.None"/> when Fair Price is not
		/// available or the percentages are unusable, so no band-based trade is taken.
		/// </summary>
		public static FpSetupBand Classify(double entry, double fairPrice, bool hasFairPrice,
		                                   double zonePercent, double band1Percent, double band2Percent)
		{
			if (!hasFairPrice || double.IsNaN(fairPrice) || Math.Abs(fairPrice) <= double.Epsilon)
				return FpSetupBand.None;

			double distPercent = Math.Abs(entry - fairPrice) / Math.Abs(fairPrice) * 100.0;

			if (distPercent <= zonePercent)  return FpSetupBand.None;
			if (distPercent <= band1Percent) return FpSetupBand.Near;
			if (distPercent <= band2Percent) return FpSetupBand.Far;

			return FpSetupBand.Beyond;
		}
	}
}
