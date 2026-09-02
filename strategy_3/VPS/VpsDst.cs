// =============================================================================
//  VPS · Daylight-saving handling
//
//  THE PROBLEM
//  IST (Asia/Kolkata) is UTC+5:30 all year — India does not observe DST. The US
//  markets do. So a US market event that happens at ONE fixed Eastern time lands
//  at TWO different IST times across the year:
//
//      US summer (EDT, UTC-4)   IST = ET + 9h30    09:30 ET = 19:00 IST
//      US winter (EST, UTC-5)   IST = ET + 10h30   09:30 ET = 20:00 IST
//
//  A window typed in IST and left alone would therefore drift an hour off the
//  market twice a year — and a backtest spanning a rollover crosses both
//  transitions.
//
//  THE FIX
//  The user types SUMMER IST times. Converting them once into the anchor zone's
//  wall clock gives a window that is FIXED there all year, so evaluating
//  membership in the anchor zone makes the winter shift automatic.
//
//  Shifting the IST window by +1h in winter and evaluating a fixed Eastern
//  window are arithmetically the same thing, but the second form asks the OS
//  timezone database for the transition dates instead of hardcoding them. That
//  keeps working if the rules ever change, and it resolves the ambiguous and
//  invalid wall-clock hours around a transition correctly.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	public static class VpsDst
	{
		/// <summary>The DST calendar every window is pinned to. MNQ is a CME product.</summary>
		public const string AnchorTimeZoneId = "America/New_York";

		/// <summary>
		/// How far the user's zone runs ahead of the anchor zone DURING the anchor's
		/// summer time. For Asia/Kolkata against US Eastern this is +9h30.
		///
		/// Measured from the tz database at a reference instant that is inside US DST
		/// in every year the rules have existed, rather than assumed.
		/// </summary>
		public static TimeSpan SummerShift(TimeZoneInfo userZone, TimeZoneInfo anchorZone)
		{
			if (userZone == null || anchorZone == null)
				return TimeSpan.Zero;

			DateTimeOffset summerReference =
				new DateTimeOffset(new DateTime(2025, 7, 15, 12, 0, 0, DateTimeKind.Utc));

			return userZone.GetUtcOffset(summerReference) - anchorZone.GetUtcOffset(summerReference);
		}

		/// <summary>Wraps a time of day back into [00:00, 24:00).</summary>
		public static TimeSpan Normalise(TimeSpan t)
		{
			TimeSpan day = TimeSpan.FromHours(24);

			while (t < TimeSpan.Zero) t += day;
			while (t >= day)          t -= day;

			return t;
		}

		/// <summary>"1900" style label for a time of day, for the startup log.</summary>
		public static string Clock(TimeSpan t)
		{
			return ((int)t.TotalHours).ToString("00") + t.Minutes.ToString("00");
		}
	}
}
