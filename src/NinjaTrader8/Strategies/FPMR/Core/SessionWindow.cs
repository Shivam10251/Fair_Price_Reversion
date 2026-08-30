// =============================================================================
//  FPMR · Core · SessionWindow
//  One "HHMM-HHMM" trading window, evaluated purely on time-of-day in the
//  configured session time zone. Deliberately independent of the instrument's
//  trading hours template — the user's windows are the user's windows.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class SessionWindow
	{
		public int    Index       { get; private set; }
		public bool   Enabled     { get; private set; }
		public string Raw         { get; private set; }
		public bool   IsValid     { get; private set; }
		public string ParseError  { get; private set; }

		/// <summary>Minutes from midnight, inclusive.</summary>
		public int StartMinute { get; private set; }
		/// <summary>Minutes from midnight, exclusive — matches TradingView session semantics.</summary>
		public int EndMinute   { get; private set; }
		/// <summary>True when the window crosses midnight (end &lt;= start).</summary>
		public bool Wraps       { get; private set; }

		public SessionWindow(int index, bool enabled, string raw)
		{
			Index   = index;
			Enabled = enabled;
			Raw     = raw;

			int s, e;
			if (TryParse(raw, out s, out e))
			{
				StartMinute = s;
				EndMinute   = e;
				Wraps       = e <= s;
				IsValid     = true;
			}
			else
			{
				ParseError = "Session " + index + " window '" + raw + "' is not in HHMM-HHMM form.";
				IsValid    = false;
			}
		}

		public bool IsActive { get { return Enabled && IsValid; } }

		/// <summary>Membership test for a bar OPEN timestamp already converted to the session zone.</summary>
		public bool Contains(DateTime tzBarOpen)
		{
			if (!IsActive)
				return false;

			int m = tzBarOpen.Hour * 60 + tzBarOpen.Minute;
			return Wraps ? (m >= StartMinute || m < EndMinute)
			             : (m >= StartMinute && m < EndMinute);
		}

		/// <summary>
		/// The next moment this window opens at or after <paramref name="tzFrom"/>.
		/// Used by the news module to decide whether a release falls inside the
		/// lookback window that precedes a session open.
		/// </summary>
		public DateTime NextOpen(DateTime tzFrom)
		{
			DateTime today = tzFrom.Date.AddMinutes(StartMinute);
			return today >= tzFrom ? today : today.AddDays(1);
		}

		/// <summary>The most recent open of this window at or before <paramref name="tzFrom"/>.</summary>
		public DateTime PreviousOpen(DateTime tzFrom)
		{
			DateTime today = tzFrom.Date.AddMinutes(StartMinute);
			return today <= tzFrom ? today : today.AddDays(-1);
		}

		public static bool TryParse(string raw, out int startMinute, out int endMinute)
		{
			startMinute = 0;
			endMinute   = 0;

			if (string.IsNullOrWhiteSpace(raw))
				return false;

			// Tolerate "0930-1000", "09:30-10:00" and surrounding whitespace.
			string cleaned = raw.Replace(":", string.Empty).Replace(" ", string.Empty);
			string[] parts = cleaned.Split('-');
			if (parts.Length != 2)
				return false;

			return TryParseHhmm(parts[0], out startMinute) && TryParseHhmm(parts[1], out endMinute);
		}

		private static bool TryParseHhmm(string token, out int minutes)
		{
			minutes = 0;
			if (token == null || token.Length != 4)
				return false;

			int value;
			if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value))
				return false;

			int hh = value / 100;
			int mm = value % 100;
			if (hh < 0 || hh > 23 || mm < 0 || mm > 59)
				return false;

			minutes = hh * 60 + mm;
			return true;
		}

		public override string ToString()
		{
			return "S" + Index + " " + Raw + (Enabled ? string.Empty : " (off)");
		}
	}
}
