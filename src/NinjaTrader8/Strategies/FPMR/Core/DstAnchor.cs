// =============================================================================
//  FPMR · Core · DstAnchor
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
//  market twice a year, and a backtest spanning a contract rollover crosses both
//  transitions.
//
//  THE FIX
//  The user types SUMMER times in their own zone. Rebasing those once onto the
//  anchor zone gives a window that is FIXED there all year, so evaluating
//  membership in the anchor zone makes the winter shift automatic.
//
//  Shifting the IST window by +1h in winter and evaluating a fixed Eastern
//  window are arithmetically identical, but the second form asks the OS timezone
//  database for the transition dates rather than hardcoding them — so it keeps
//  working if the rules change, and it resolves the ambiguous and invalid
//  wall-clock hours around a transition correctly.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public static class DstAnchor
	{
		/// <summary>The DST calendar every window is pinned to. These are CME products.</summary>
		public const string AnchorTimeZoneId = "America/New_York";

		/// <summary>
		/// How far the user's zone runs ahead of the anchor zone DURING the anchor's
		/// summer time. Asia/Kolkata against US Eastern gives +9h30. Read from the tz
		/// database at a reference instant inside US DST rather than assumed.
		/// </summary>
		public static TimeSpan SummerShift(TimeZoneInfo userZone, TimeZoneInfo anchorZone)
		{
			if (userZone == null || anchorZone == null)
				return TimeSpan.Zero;

			DateTimeOffset summerReference =
				new DateTimeOffset(new DateTime(2025, 7, 15, 12, 0, 0, DateTimeKind.Utc));

			return userZone.GetUtcOffset(summerReference) - anchorZone.GetUtcOffset(summerReference);
		}

		/// <summary>
		/// The extra offset winter adds, in the user's clock. One hour whenever the
		/// user's zone has no DST of its own and the anchor's does. Used for logging.
		/// </summary>
		public static TimeSpan WinterExtra(TimeZoneInfo userZone, TimeZoneInfo anchorZone)
		{
			if (userZone == null || anchorZone == null)
				return TimeSpan.Zero;

			DateTimeOffset winter = new DateTimeOffset(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc));

			return (userZone.GetUtcOffset(winter) - anchorZone.GetUtcOffset(winter))
			     - SummerShift(userZone, anchorZone);
		}

		/// <summary>Wraps a minute-of-day back into [0, 1440).</summary>
		public static int NormaliseMinutes(int minutes)
		{
			int m = minutes % 1440;
			return m < 0 ? m + 1440 : m;
		}

		/// <summary>
		/// Shifts a "HHMM-HHMM" window by <paramref name="delta"/>. Returns the input
		/// unchanged when it cannot be parsed, so a malformed window still reaches the
		/// normal parser and produces its own error message.
		/// </summary>
		public static string ShiftWindow(string window, TimeSpan delta)
		{
			if (string.IsNullOrWhiteSpace(window))
				return window;

			string[] parts = window.Trim().Split('-');
			if (parts.Length != 2)
				return window;

			int start, end;
			if (!TryParseClock(parts[0], out start) || !TryParseClock(parts[1], out end))
				return window;

			int d = (int)delta.TotalMinutes;

			return Clock(NormaliseMinutes(start + d)) + "-" + Clock(NormaliseMinutes(end + d));
		}

		/// <summary>Minutes from midnight for a "HHMM" token.</summary>
		public static bool TryParseClock(string token, out int minutes)
		{
			minutes = 0;

			string t = (token ?? string.Empty).Trim().Replace(":", string.Empty);
			if (t.Length != 4)
				return false;

			int hhmm;
			if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out hhmm))
				return false;

			int hh = hhmm / 100;
			int mm = hhmm % 100;

			if (hh < 0 || hh > 23 || mm < 0 || mm > 59)
				return false;

			minutes = hh * 60 + mm;
			return true;
		}

		/// <summary>"HHMM" label for a minute-of-day.</summary>
		public static string Clock(int minutes)
		{
			int m = NormaliseMinutes(minutes);
			return (m / 60).ToString("00", CultureInfo.InvariantCulture)
			     + (m % 60).ToString("00", CultureInfo.InvariantCulture);
		}
	}
}
