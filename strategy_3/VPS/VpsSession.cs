// =============================================================================
//  VPS · Session and time zone handling
//
//  The Pine indicator evaluates its two sessions with time(timeframe.period,
//  "HHMM-HHMM", tz), which tests the bar against a clock window in a NAMED IANA
//  zone. Two things have to be reproduced exactly:
//
//    1. IANA ids. .NET Framework 4.8 — which NinjaTrader 8 runs on — only knows
//       Windows zone ids, so "America/New_York" throws. VpsTimeZone maps the ids
//       this indicator actually uses and falls back to the OS for anything else.
//
//    2. Bar timestamps. NinjaTrader stamps a bar at its CLOSE; TradingView tests
//       a session against the bar's OPEN. Every membership test here therefore
//       takes the bar's open time, which the strategy derives as
//       (Time[0] - barLength) before converting.
//
//  A window whose end is at or before its start wraps past midnight, matching
//  Pine's handling of sessions such as 1800-0200.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	public static class VpsTimeZone
	{
		private static readonly Dictionary<string, string> IanaToWindows =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				{ "America/New_York",    "Eastern Standard Time"      },
				{ "America/Chicago",     "Central Standard Time"      },
				{ "America/Denver",      "Mountain Standard Time"     },
				{ "America/Los_Angeles", "Pacific Standard Time"      },
				{ "Asia/Kolkata",        "India Standard Time"        },
				{ "Asia/Calcutta",       "India Standard Time"        },
				{ "Asia/Tokyo",          "Tokyo Standard Time"        },
				{ "Asia/Shanghai",       "China Standard Time"        },
				{ "Asia/Hong_Kong",      "China Standard Time"        },
				{ "Asia/Singapore",      "Singapore Standard Time"    },
				{ "Asia/Dubai",          "Arabian Standard Time"      },
				{ "Australia/Sydney",    "AUS Eastern Standard Time"  },
				{ "Europe/London",       "GMT Standard Time"          },
				{ "Europe/Berlin",       "W. Europe Standard Time"    },
				{ "Europe/Paris",        "Romance Standard Time"      },
				{ "Europe/Zurich",       "W. Europe Standard Time"    },
				{ "Europe/Moscow",       "Russian Standard Time"      },
				{ "UTC",                 "UTC"                        },
				{ "Etc/UTC",             "UTC"                        },
				{ "IST",                 "India Standard Time"        },
				{ "EST",                 "Eastern Standard Time"      },
			};

		/// <summary>Resolves an IANA id, a Windows id, or a known shorthand. Null when unknown.</summary>
		public static TimeZoneInfo Resolve(string id)
		{
			if (string.IsNullOrWhiteSpace(id))
				return null;

			string key = id.Trim();

			string windows;
			if (IanaToWindows.TryGetValue(key, out windows))
			{
				try { return TimeZoneInfo.FindSystemTimeZoneById(windows); }
				catch { /* fall through to a direct attempt */ }
			}

			try { return TimeZoneInfo.FindSystemTimeZoneById(key); }
			catch { return null; }
		}

		/// <summary>
		/// Converts between two zones. Both conversions go through UTC so a DST
		/// boundary is resolved by the OS database rather than by arithmetic.
		/// </summary>
		public static DateTime Convert(DateTime value, TimeZoneInfo from, TimeZoneInfo to)
		{
			if (from == null || to == null || from.Equals(to))
				return value;

			DateTime unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

			// An invalid local time sits inside a spring-forward gap. Nudging it
			// forward by the adjustment keeps the sequence monotonic.
			if (from.IsInvalidTime(unspecified))
				unspecified = unspecified.AddHours(1);

			DateTimeOffset utc = new DateTimeOffset(unspecified, from.GetUtcOffset(unspecified));
			return TimeZoneInfo.ConvertTime(utc, to).DateTime;
		}
	}

	/// <summary>
	/// One "HHMM-HHMM" clock window evaluated in its own time zone.
	/// </summary>
	public sealed class VpsSession
	{
		public string       Name       { get; private set; }
		/// <summary>The zone membership is actually evaluated in.</summary>
		public TimeZoneInfo Zone       { get; private set; }
		/// <summary>The zone the user typed the times in, when they were rebased.</summary>
		public TimeZoneInfo SourceZone { get; private set; }
		public TimeSpan     Start      { get; private set; }
		public TimeSpan     End        { get; private set; }
		public bool         IsValid    { get; private set; }
		public string       ParseError { get; private set; }
		/// <summary>True when the window runs past midnight, e.g. 1800-0200.</summary>
		public bool         Wraps      { get; private set; }

		/// <summary>
		/// Rebases this window onto the DST anchor zone, treating the times as the
		/// user's SUMMER wall clock. The returned window is fixed in the anchor zone
		/// all year, so the winter shift happens by itself. Returns the original
		/// window unchanged when either zone cannot be resolved.
		/// </summary>
		public VpsSession ToAnchor(TimeZoneInfo anchorZone)
		{
			if (!IsValid || anchorZone == null || Zone == null || Zone.Equals(anchorZone))
				return this;

			TimeSpan shift = VpsDst.SummerShift(Zone, anchorZone);

			VpsSession moved = new VpsSession(Name, Zone, anchorZone,
				VpsDst.Normalise(Start - shift),
				VpsDst.Normalise(End   - shift));

			return moved;
		}

		/// <summary>Private constructor used by ToAnchor; skips parsing.</summary>
		private VpsSession(string name, TimeZoneInfo sourceZone, TimeZoneInfo zone, TimeSpan start, TimeSpan end)
		{
			Name       = name;
			SourceZone = sourceZone;
			Zone       = zone;
			Start      = start;
			End        = end;
			Wraps      = end <= start;
			IsValid    = true;
		}

		public VpsSession(string name, string window, string timeZoneId)
		{
			Name = name;

			Zone = VpsTimeZone.Resolve(timeZoneId);
			if (Zone == null)
			{
				ParseError = name + " time zone '" + timeZoneId + "' could not be resolved.";
				return;
			}

			TimeSpan s, e;
			if (!TryParseWindow(window, out s, out e))
			{
				ParseError = name + " session '" + window + "' is not in HHMM-HHMM form, e.g. 0930-1600.";
				return;
			}

			Start      = s;
			End        = e;
			SourceZone = Zone;
			Wraps      = e <= s;
			IsValid    = true;
		}

		/// <summary>Human-readable window for the startup log, e.g. "1900-0130".</summary>
		public string WindowLabel
		{
			get { return VpsDst.Clock(Start) + "-" + VpsDst.Clock(End); }
		}

		private static bool TryParseWindow(string window, out TimeSpan start, out TimeSpan end)
		{
			start = TimeSpan.Zero;
			end   = TimeSpan.Zero;

			if (string.IsNullOrWhiteSpace(window))
				return false;

			string[] parts = window.Trim().Split('-');
			if (parts.Length != 2)
				return false;

			return TryParseClock(parts[0], out start) && TryParseClock(parts[1], out end);
		}

		private static bool TryParseClock(string token, out TimeSpan value)
		{
			value = TimeSpan.Zero;

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

			value = new TimeSpan(hh, mm, 0);
			return true;
		}

		/// <summary><paramref name="tzTime"/> must already be in this session's zone.</summary>
		public bool Contains(DateTime tzTime)
		{
			if (!IsValid)
				return false;

			TimeSpan t = tzTime.TimeOfDay;

			return Wraps ? (t >= Start || t < End)
			             : (t >= Start && t < End);
		}

		/// <summary>
		/// Identifies WHICH occurrence of the window a time belongs to, so a session
		/// that wraps midnight is still one continuous occurrence. Returns
		/// DateTime.MinValue when the time sits outside the window.
		/// </summary>
		public DateTime OccurrenceOf(DateTime tzTime)
		{
			if (!Contains(tzTime))
				return DateTime.MinValue;

			DateTime openToday = tzTime.Date.Add(Start);

			// Past midnight and before the close: the occurrence opened yesterday.
			if (Wraps && tzTime.TimeOfDay < End)
				openToday = openToday.AddDays(-1);

			return openToday;
		}
	}
}
