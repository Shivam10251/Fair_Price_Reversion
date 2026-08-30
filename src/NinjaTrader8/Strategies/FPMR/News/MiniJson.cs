// =============================================================================
//  FPMR · News · MiniJson
//
//  A dependency-free JSON reader, present only so the news calendar can be a
//  .json file without shipping an external DLL (a hard requirement of the port).
//  It handles the shape ForexFactory publishes — an array of flat objects — and
//  tolerates nested values by parsing them and letting the caller ignore them.
//
//  Not a general-purpose library: no comments, no NaN/Infinity literals.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public static class MiniJson
	{
		/// <summary>Parses a document into Dictionary&lt;string,object&gt; / List&lt;object&gt; / string / double / bool / null.</summary>
		public static object Parse(string text)
		{
			if (string.IsNullOrEmpty(text))
				throw new FormatException("Empty JSON document.");

			int pos = 0;
			object value = ParseValue(text, ref pos);
			SkipWhitespace(text, ref pos);
			if (pos < text.Length)
				throw new FormatException("Unexpected trailing characters at offset " + pos + ".");

			return value;
		}

		/// <summary>Convenience: reads a top-level array of objects as string dictionaries.</summary>
		public static List<Dictionary<string, string>> ParseObjectArray(string text)
		{
			List<Dictionary<string, string>> rows = new List<Dictionary<string, string>>();

			object root = Parse(text);

			// Accept either a bare array or an object wrapping one array property.
			List<object> array = root as List<object>;
			if (array == null)
			{
				Dictionary<string, object> obj = root as Dictionary<string, object>;
				if (obj != null)
					foreach (KeyValuePair<string, object> kv in obj)
					{
						array = kv.Value as List<object>;
						if (array != null)
							break;
					}
			}

			if (array == null)
				throw new FormatException("Expected a JSON array of event objects.");

			foreach (object item in array)
			{
				Dictionary<string, object> o = item as Dictionary<string, object>;
				if (o == null)
					continue;

				Dictionary<string, string> row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				foreach (KeyValuePair<string, object> kv in o)
					row[kv.Key] = Stringify(kv.Value);

				rows.Add(row);
			}

			return rows;
		}

		private static string Stringify(object v)
		{
			if (v == null)   return null;
			if (v is string) return (string)v;
			if (v is bool)   return ((bool)v) ? "true" : "false";
			if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
			return v.ToString();
		}

		private static object ParseValue(string s, ref int pos)
		{
			SkipWhitespace(s, ref pos);
			if (pos >= s.Length)
				throw new FormatException("Unexpected end of JSON.");

			char c = s[pos];
			switch (c)
			{
				case '{': return ParseObject(s, ref pos);
				case '[': return ParseArray(s, ref pos);
				case '"': return ParseString(s, ref pos);
				case 't': Expect(s, ref pos, "true");  return true;
				case 'f': Expect(s, ref pos, "false"); return false;
				case 'n': Expect(s, ref pos, "null");  return null;
				default:  return ParseNumber(s, ref pos);
			}
		}

		private static Dictionary<string, object> ParseObject(string s, ref int pos)
		{
			Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
			pos++; // '{'
			SkipWhitespace(s, ref pos);

			if (pos < s.Length && s[pos] == '}') { pos++; return result; }

			while (true)
			{
				SkipWhitespace(s, ref pos);
				if (pos >= s.Length || s[pos] != '"')
					throw new FormatException("Expected an object key at offset " + pos + ".");

				string key = ParseString(s, ref pos);
				SkipWhitespace(s, ref pos);

				if (pos >= s.Length || s[pos] != ':')
					throw new FormatException("Expected ':' at offset " + pos + ".");
				pos++;

				result[key] = ParseValue(s, ref pos);
				SkipWhitespace(s, ref pos);

				if (pos >= s.Length)
					throw new FormatException("Unterminated object.");
				if (s[pos] == ',') { pos++; continue; }
				if (s[pos] == '}') { pos++; return result; }

				throw new FormatException("Expected ',' or '}' at offset " + pos + ".");
			}
		}

		private static List<object> ParseArray(string s, ref int pos)
		{
			List<object> result = new List<object>();
			pos++; // '['
			SkipWhitespace(s, ref pos);

			if (pos < s.Length && s[pos] == ']') { pos++; return result; }

			while (true)
			{
				result.Add(ParseValue(s, ref pos));
				SkipWhitespace(s, ref pos);

				if (pos >= s.Length)
					throw new FormatException("Unterminated array.");
				if (s[pos] == ',') { pos++; continue; }
				if (s[pos] == ']') { pos++; return result; }

				throw new FormatException("Expected ',' or ']' at offset " + pos + ".");
			}
		}

		private static string ParseString(string s, ref int pos)
		{
			StringBuilder sb = new StringBuilder();
			pos++; // opening quote

			while (pos < s.Length)
			{
				char c = s[pos++];

				if (c == '"')
					return sb.ToString();

				if (c != '\\')
				{
					sb.Append(c);
					continue;
				}

				if (pos >= s.Length)
					break;

				char esc = s[pos++];
				switch (esc)
				{
					case '"':  sb.Append('"');  break;
					case '\\': sb.Append('\\'); break;
					case '/':  sb.Append('/');  break;
					case 'b':  sb.Append('\b'); break;
					case 'f':  sb.Append('\f'); break;
					case 'n':  sb.Append('\n'); break;
					case 'r':  sb.Append('\r'); break;
					case 't':  sb.Append('\t'); break;
					case 'u':
						if (pos + 4 > s.Length)
							throw new FormatException("Truncated \\u escape.");
						sb.Append((char)int.Parse(s.Substring(pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
						pos += 4;
						break;
					default:
						throw new FormatException("Unknown escape '\\" + esc + "'.");
				}
			}

			throw new FormatException("Unterminated string.");
		}

		private static double ParseNumber(string s, ref int pos)
		{
			int start = pos;
			while (pos < s.Length && "+-0123456789.eE".IndexOf(s[pos]) >= 0)
				pos++;

			double value;
			string token = s.Substring(start, pos - start);
			if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
				throw new FormatException("Invalid number '" + token + "'.");

			return value;
		}

		private static void Expect(string s, ref int pos, string literal)
		{
			if (pos + literal.Length > s.Length || string.CompareOrdinal(s, pos, literal, 0, literal.Length) != 0)
				throw new FormatException("Expected '" + literal + "' at offset " + pos + ".");
			pos += literal.Length;
		}

		private static void SkipWhitespace(string s, ref int pos)
		{
			while (pos < s.Length && char.IsWhiteSpace(s[pos]))
				pos++;
		}
	}
}
