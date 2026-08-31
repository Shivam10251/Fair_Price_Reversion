// =============================================================================
//  FPMR · Core · TimeZoneRegistry
//
//  WHY THIS EXISTS
//  NinjaTrader 8 runs on the .NET Framework on Windows, where
//  TimeZoneInfo.FindSystemTimeZoneById() only understands WINDOWS time zone ids
//  ("Eastern Standard Time"), not the IANA ids the Pine version uses
//  ("America/New_York"). The Pine input list is kept verbatim so the two
//  platforms are configured identically, and this table translates.
//
//  DST is handled by TimeZoneInfo itself: the Windows zones below are the
//  DST-aware zones, so "Eastern Standard Time" correctly becomes EDT in summer.
// =============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public static class TimeZoneRegistry
	{
		/// <summary>The exact option list offered in the strategy properties.</summary>
		public static readonly string[] Options =
		{
			"America/New_York",
			"America/Chicago",
			"America/Los_Angeles",
			"Europe/London",
			"Europe/Berlin",
			"Asia/Kolkata",
			"Asia/Tokyo",
			"Asia/Shanghai",
			"Australia/Sydney",
			"UTC"
		};

		private static readonly Dictionary<string, string> IanaToWindows =
			new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "America/New_York",    "Eastern Standard Time"      },
			{ "America/Chicago",     "Central Standard Time"      },
			{ "America/Denver",      "Mountain Standard Time"     },
			{ "America/Los_Angeles", "Pacific Standard Time"      },
			{ "Europe/London",       "GMT Standard Time"          },
			{ "Europe/Berlin",       "W. Europe Standard Time"    },
			{ "Europe/Paris",        "Romance Standard Time"      },
			{ "Europe/Zurich",       "W. Europe Standard Time"    },
			{ "Asia/Kolkata",        "India Standard Time"        },
			{ "Asia/Calcutta",       "India Standard Time"        },
			{ "IST",                 "India Standard Time"        },
			{ "Asia/Tokyo",          "Tokyo Standard Time"        },
			{ "Asia/Shanghai",       "China Standard Time"        },
			{ "Asia/Hong_Kong",      "China Standard Time"        },
			{ "Asia/Singapore",      "Singapore Standard Time"    },
			{ "Asia/Dubai",          "Arabian Standard Time"      },
			{ "Australia/Sydney",    "AUS Eastern Standard Time"  },
			{ "Pacific/Auckland",    "New Zealand Standard Time"  },
			{ "UTC",                 "UTC"                        },
			{ "Etc/UTC",             "UTC"                        }
		};

		/// <summary>
		/// Resolves an IANA or Windows time zone id. Returns null when the id cannot be
		/// resolved — callers must treat null as a hard configuration error and say so,
		/// never silently fall back to machine local time.
		/// </summary>
		public static TimeZoneInfo Resolve(string id)
		{
			if (string.IsNullOrWhiteSpace(id))
				return null;

			id = id.Trim();

			// A Windows id, or a runtime new enough to understand IANA directly.
			try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
			catch { /* fall through to the translation table */ }

			string windowsId;
			if (IanaToWindows.TryGetValue(id, out windowsId))
			{
				try { return TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
				catch { return null; }
			}

			return null;
		}

		/// <summary>
		/// Converts a bar timestamp into the target zone.
		/// <paramref name="source"/> is the zone the bar timestamps are expressed in —
		/// on NT8 that is the zone of the data series' trading hours template.
		/// </summary>
		public static DateTime Convert(DateTime barTime, TimeZoneInfo source, TimeZoneInfo target)
		{
			if (source == null || target == null)
				return barTime;
			if (source.Equals(target))
				return barTime;

			// Bar timestamps carry no Kind information; force Unspecified so
			// ConvertTime does not reinterpret them against the machine's zone.
			DateTime unspecified = DateTime.SpecifyKind(barTime, DateTimeKind.Unspecified);
			return TimeZoneInfo.ConvertTime(unspecified, source, target);
		}
	}

	/// <summary>
	/// Turns the free-text timezone inputs into an editable drop-down in the strategy
	/// properties grid. The list is <see cref="TimeZoneRegistry.Options"/>; because
	/// GetStandardValuesExclusive is false, any other id TimeZoneRegistry.Resolve
	/// understands (America/Denver, Asia/Dubai, a raw Windows id, IST) can still be
	/// typed in by hand, and existing saved templates keep working.
	/// </summary>
	public class TimeZoneOptionConverter : StringConverter
	{
		public override bool GetStandardValuesSupported(ITypeDescriptorContext context)
		{
			return true;
		}

		// Editable, not a closed list — hand-typed ids remain valid.
		public override bool GetStandardValuesExclusive(ITypeDescriptorContext context)
		{
			return false;
		}

		public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context)
		{
			return new StandardValuesCollection(TimeZoneRegistry.Options);
		}
	}
}
