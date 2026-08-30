// =============================================================================
//  FPMR · News · NewsCalendarLoader
//
//  Reads a ForexFactory export (JSON or CSV) into NewsEvent objects, normalised
//  into the strategy's SESSION time zone. Loaded ONCE, never per bar.
//
//  TIMEZONE CONVENTION — read this before trusting a backtest
//  ----------------------------------------------------------
//  * ForexFactory's weekly JSON feed writes an ISO-8601 date WITH an explicit UTC
//    offset, e.g. "2024-05-03T08:30:00-04:00". When an offset is present it is
//    authoritative and the NewsFileTimeZone input is ignored for that row.
//  * ForexFactory's CSV download carries NO zone information. The timestamps are
//    rendered in whatever time zone the exporting account's FF profile was set to.
//    There is no way to recover that from the file, so it is an INPUT
//    (NewsFileTimeZone), defaulting to America/New_York, which is FF's own default.
//    Set it wrong and every news Fair Price is wrong — this is the single most
//    important thing to verify on the first backtest.
//  * Rows without a usable clock time ("All Day", "Tentative", blank) are skipped:
//    the rule needs the exact 1-minute candle of the release.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class NewsLoadResult
	{
		public List<NewsEvent> Events        = new List<NewsEvent>();
		public bool            Success;
		public string          Error;
		public int             SkippedRows;
		public string          DetectedFormat = "unknown";
		public string          DetectedDatePattern = "auto";
	}

	public static class NewsCalendarLoader
	{
		private static readonly string[] TitleKeys    = { "title", "event", "name", "event name", "eventname" };
		private static readonly string[] CurrencyKeys = { "currency", "country", "ccy", "curr" };
		private static readonly string[] ImpactKeys   = { "impact", "importance", "impact level", "impactlevel" };
		private static readonly string[] DateKeys     = { "date" };
		private static readonly string[] TimeKeys     = { "time" };
		private static readonly string[] StampKeys    = { "datetime", "date_time", "date time", "timestamp", "released", "release time", "start" };

		private static readonly string[] DatePatterns =
		{
			"yyyy-MM-dd", "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy",
			"yyyy/MM/dd", "dd.MM.yyyy", "MMM d yyyy", "MMM d, yyyy", "ddd MMM d"
		};

		private static readonly string[] TimePatterns =
		{
			"h:mmtt", "hh:mmtt", "h:mm tt", "hh:mm tt", "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"
		};

		/// <summary>
		/// Loads and normalises the calendar. Never throws — a failure comes back as
		/// Success=false with an Error the caller must log loudly before falling back
		/// to normal Fair Price behaviour.
		/// </summary>
		public static NewsLoadResult Load(string filePath, TimeZoneInfo fileZone, TimeZoneInfo sessionZone, string dateFormatOverride)
		{
			NewsLoadResult result = new NewsLoadResult();

			try
			{
				if (string.IsNullOrWhiteSpace(filePath))
				{
					result.Error = "News file path is empty.";
					return result;
				}

				if (!File.Exists(filePath))
				{
					result.Error = "News file not found: " + filePath;
					return result;
				}

				string text = File.ReadAllText(filePath, Encoding.UTF8).TrimStart('﻿', ' ', '\r', '\n', '\t');
				if (text.Length == 0)
				{
					result.Error = "News file is empty: " + filePath;
					return result;
				}

				bool looksJson = text[0] == '[' || text[0] == '{';
				result.DetectedFormat = looksJson ? "json" : "csv";

				List<Dictionary<string, string>> rows = looksJson
					? MiniJson.ParseObjectArray(text)
					: ParseCsv(text);

				if (rows.Count == 0)
				{
					result.Error = "News file parsed but contained no rows: " + filePath;
					return result;
				}

				string pattern = string.IsNullOrWhiteSpace(dateFormatOverride) ? null : dateFormatOverride.Trim();
				result.DetectedDatePattern = pattern ?? "auto";

				foreach (Dictionary<string, string> row in rows)
				{
					NewsEvent ev = BuildEvent(row, fileZone, sessionZone, pattern);
					if (ev == null)
						result.SkippedRows++;
					else
						result.Events.Add(ev);
				}

				if (result.Events.Count == 0)
				{
					result.Error = "News file had " + rows.Count + " rows but none carried a usable date/time. "
					             + "Check the column names and the date format.";
					return result;
				}

				result.Events.Sort((a, b) => a.TimeSessionTz.CompareTo(b.TimeSessionTz));
				result.Success = true;
				return result;
			}
			catch (Exception ex)
			{
				result.Error = "News file could not be parsed (" + ex.GetType().Name + "): " + ex.Message;
				return result;
			}
		}

		private static NewsEvent BuildEvent(Dictionary<string, string> row, TimeZoneInfo fileZone, TimeZoneInfo sessionZone, string datePattern)
		{
			string stamp = FirstValue(row, StampKeys);
			string datePart = FirstValue(row, DateKeys);
			string timePart = FirstValue(row, TimeKeys);

			DateTime whenSession;

			if (!string.IsNullOrWhiteSpace(stamp))
			{
				if (!TryResolveStamp(stamp, fileZone, sessionZone, out whenSession))
					return null;
			}
			else if (!string.IsNullOrWhiteSpace(datePart))
			{
				string combined = string.IsNullOrWhiteSpace(timePart) ? datePart : datePart.Trim() + " " + timePart.Trim();

				// "All Day" / "Tentative" / "Holiday" rows have no release minute.
				if (!string.IsNullOrWhiteSpace(timePart) && !LooksLikeClockTime(timePart))
					return null;

				if (!TryResolveDateAndTime(datePart, timePart, combined, datePattern, fileZone, sessionZone, out whenSession))
					return null;
			}
			else
			{
				return null;
			}

			return new NewsEvent
			{
				TimeSessionTz = whenSession,
				Currency      = (FirstValue(row, CurrencyKeys) ?? string.Empty).Trim(),
				Impact        = ParseImpact(FirstValue(row, ImpactKeys)),
				Title         = (FirstValue(row, TitleKeys) ?? string.Empty).Trim(),
				Source        = stamp ?? (datePart + " " + timePart)
			};
		}

		private static bool TryResolveStamp(string stamp, TimeZoneInfo fileZone, TimeZoneInfo sessionZone, out DateTime whenSession)
		{
			whenSession = default(DateTime);
			stamp = stamp.Trim();

			// An explicit offset (or trailing Z) is authoritative.
			if (HasExplicitOffset(stamp))
			{
				DateTimeOffset dto;
				if (DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out dto))
				{
					whenSession = TimeZoneInfo.ConvertTime(dto, sessionZone).DateTime;
					return true;
				}
				return false;
			}

			DateTime naive;
			if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.NoCurrentDateDefault, out naive))
				return false;

			whenSession = TimeZoneRegistry.Convert(naive, fileZone, sessionZone);
			return true;
		}

		private static bool TryResolveDateAndTime(string datePart, string timePart, string combined, string datePattern,
		                                          TimeZoneInfo fileZone, TimeZoneInfo sessionZone, out DateTime whenSession)
		{
			whenSession = default(DateTime);

			DateTime date;
			if (!TryParseDate(datePart.Trim(), datePattern, out date))
				return false;

			TimeSpan tod = TimeSpan.Zero;
			if (!string.IsNullOrWhiteSpace(timePart) && !TryParseTime(timePart.Trim(), out tod))
				return false;

			DateTime naive = date.Date.Add(tod);
			whenSession = TimeZoneRegistry.Convert(naive, fileZone, sessionZone);
			return true;
		}

		private static bool TryParseDate(string token, string overridePattern, out DateTime date)
		{
			if (!string.IsNullOrEmpty(overridePattern))
				return DateTime.TryParseExact(token, overridePattern, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

			if (DateTime.TryParseExact(token, DatePatterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
				return true;

			return DateTime.TryParse(token, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
		}

		private static bool TryParseTime(string token, out TimeSpan tod)
		{
			tod = TimeSpan.Zero;
			token = token.Replace(".", string.Empty).Trim();

			DateTime parsed;
			if (DateTime.TryParseExact(token, TimePatterns, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
			{
				tod = parsed.TimeOfDay;
				return true;
			}

			return false;
		}

		private static bool LooksLikeClockTime(string token)
		{
			if (string.IsNullOrWhiteSpace(token))
				return false;
			return token.IndexOf(':') >= 0;
		}

		private static bool HasExplicitOffset(string stamp)
		{
			if (stamp.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
				return true;

			// Look for +HH:MM / -HH:MM after the time portion, not the date separators.
			int t = stamp.IndexOf('T');
			int scanFrom = t >= 0 ? t : Math.Max(stamp.IndexOf(' '), 0);
			for (int i = scanFrom; i < stamp.Length; i++)
				if (stamp[i] == '+' || (stamp[i] == '-' && i > scanFrom + 2))
					return true;

			return false;
		}

		public static FpNewsImpact ParseImpact(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return FpNewsImpact.Unknown;

			string v = raw.Trim().ToLowerInvariant();

			if (v.StartsWith("high") || v == "3" || v == "red")                 return FpNewsImpact.High;
			if (v.StartsWith("med")  || v == "2" || v == "orange" || v == "ora") return FpNewsImpact.Medium;
			if (v.StartsWith("low")  || v == "1" || v == "yellow")              return FpNewsImpact.Low;

			return FpNewsImpact.Unknown;
		}

		/// <summary>RFC-4180-ish CSV reader: quoted fields, embedded commas, doubled quotes, CRLF or LF.</summary>
		public static List<Dictionary<string, string>> ParseCsv(string text)
		{
			List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();
			List<string[]> raw = new List<string[]>();

			List<string> field = new List<string>();
			StringBuilder cell = new StringBuilder();
			bool inQuotes = false;

			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];

				if (inQuotes)
				{
					if (c == '"')
					{
						if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
						else inQuotes = false;
					}
					else cell.Append(c);
					continue;
				}

				if (c == '"')      { inQuotes = true; }
				else if (c == ',') { field.Add(cell.ToString()); cell.Length = 0; }
				else if (c == '\r') { /* handled by \n */ }
				else if (c == '\n')
				{
					field.Add(cell.ToString()); cell.Length = 0;
					raw.Add(field.ToArray()); field.Clear();
				}
				else cell.Append(c);
			}

			if (cell.Length > 0 || field.Count > 0)
			{
				field.Add(cell.ToString());
				raw.Add(field.ToArray());
			}

			if (raw.Count < 2)
				return rows;

			string[] header = raw[0];
			for (int i = 0; i < header.Length; i++)
				header[i] = header[i].Trim().Trim('"');

			for (int r = 1; r < raw.Count; r++)
			{
				string[] line = raw[r];
				if (line.Length == 1 && string.IsNullOrWhiteSpace(line[0]))
					continue;

				Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				for (int c = 0; c < header.Length && c < line.Length; c++)
					row[header[c]] = line[c];

				rows.Add(row);
			}

			return rows;
		}

		private static string FirstValue(Dictionary<string, string> row, string[] keys)
		{
			foreach (string k in keys)
			{
				string v;
				if (row.TryGetValue(k, out v) && !string.IsNullOrWhiteSpace(v))
					return v;
			}
			return null;
		}
	}
}
