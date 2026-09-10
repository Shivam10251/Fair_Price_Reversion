// =============================================================================
//  FPMR · News · Nt8EconomicFeed
//
//  Reads the ACTUAL released figure from NinjaTrader's own economic calendar,
//  so the strategy does not depend on somebody typing a number into a CSV in
//  the sixty seconds between the release and the close of the news candle.
//
//  WHAT NINJATRADER ACTUALLY EXPOSES — read this before changing anything
//  ---------------------------------------------------------------------
//  Two halves, and the second one is easy to miss:
//
//  1. PUSH. NinjaTrader.Cbi.Calendar publishes a static event,
//     EconomicEventUpdateReceived, carrying EventName, Country, Importance,
//     Actual, Consensus (the forecast), Prior and a Timestamp. It fires as each
//     release prints.
//
//  2. PULL. NinjaTrader.Tradovate.Adapter exposes a PUBLIC parameterless
//     RequestCalendarsEconomic(), reachable as
//         Cbi.Connection.Connections -> connection.Adapter
//     It asks the server for the whole calendar, and the answer comes back
//     through the SAME event as (1). Measured on a live connection: 4,708 events
//     in about five seconds, complete with actuals, consensus and priors,
//     including releases from earlier the same day.
//
//  (2) is what makes unattended use possible. A push alone is useless after a
//  restart and useless for a release that happened earlier in the session, which
//  would otherwise force somebody to type the number into a CSV by hand.
//
//  NAME MATCHING IS THE TRAP
//  The two sources name the same release differently — ForexFactory "PPI m/m"
//  against NinjaTrader "PPI (MoM)" — so Normalise() expands the period suffixes
//  before stripping punctuation. Without that expansion every month-on-month
//  release fails to pair up and falls silently to the unknown-value rule.
//
//  CONSEQUENCES
//  * Nothing arrives in a BACKTEST. Historical bars generate no push and the pull
//    needs a live connection, so a backtest needs an Actual column in the file
//    and FpNewsActualSource.FileOnly.
//  * Delivery depends on the connected adapter. RequestCalendarsEconomic is an
//    UNDOCUMENTED internal API reached by reflection; every step is guarded so a
//    platform update degrades to "no pull available" rather than throwing.
//  * The event fires on a feed thread, not the bar thread. Every read and write
//    of _latest is therefore under a lock.
//  * The subscription is to a STATIC event, so it outlives the strategy instance
//    unless it is removed. Unsubscribe() runs from State.Terminated.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	/// <summary>One release as NinjaTrader delivered it.</summary>
	public sealed class Nt8EconomicRelease
	{
		public string   EventName;
		public string   Country;
		public int      Importance;
		public double   Actual;
		public double   Consensus;
		public double   Prior;
		public DateTime Timestamp;
		/// <summary>Local wall-clock instant the push reached us, for the log.</summary>
		public DateTime ReceivedLocal;

		public bool HasActual   { get { return !double.IsNaN(Actual);    } }
		public bool HasConsensus{ get { return !double.IsNaN(Consensus); } }

		public override string ToString()
		{
			return Country + " " + EventName
			     + " actual " + (HasActual    ? Actual.ToString("0.####", CultureInfo.InvariantCulture)    : "-")
			     + " vs forecast " + (HasConsensus ? Consensus.ToString("0.####", CultureInfo.InvariantCulture) : "-")
			     + " (received " + ReceivedLocal.ToString("HH:mm:ss") + ")";
		}
	}

	public sealed class Nt8EconomicFeed
	{
		private readonly object _gate = new object();
		private readonly Dictionary<string, Nt8EconomicRelease> _latest =
			new Dictionary<string, Nt8EconomicRelease>(StringComparer.OrdinalIgnoreCase);

		private EventHandler<NinjaTrader.Cbi.EconomicUpdateArgs> _handler;
		private bool _subscribed;

		/// <summary>Releases received since the strategy started.</summary>
		public int ReceivedCount { get; private set; }
		/// <summary>Set when the subscription itself could not be established.</summary>
		public string SubscribeError { get; private set; }

		public bool IsSubscribed { get { return _subscribed; } }

		/// <summary>
		/// Attaches to the static calendar event. Never throws: a platform build or a
		/// provider without the calendar must degrade to "no actuals", not kill the
		/// strategy on load.
		/// </summary>
		public bool Subscribe()
		{
			if (_subscribed)
				return true;

			try
			{
				_handler = OnEconomicUpdate;
				NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived += _handler;
				_subscribed = true;
				return true;
			}
			catch (Exception ex)
			{
				_handler        = null;
				_subscribed     = false;
				SubscribeError  = ex.GetType().Name + ": " + ex.Message;
				return false;
			}
		}

		/// <summary>
		/// Asks the connected adapter for the whole economic calendar, instead of waiting
		/// for a release to print.
		///
		/// This is what makes the strategy usable unattended. A push only ever arrives
		/// for a release that happens WHILE the strategy is running, which is no help
		/// after a restart and no help for a release earlier in the day. A pull returns
		/// the calendar complete with actuals, consensus and priors — measured at 4,708
		/// events on a live connection, including releases from earlier the same day.
		///
		/// Reached by reflection:
		///     Cbi.Connection.Connections -> connection.Adapter -> RequestCalendarsEconomic()
		///
		/// That is an UNDOCUMENTED, INTERNAL NinjaTrader API. Nothing here references
		/// NinjaTrader.Tradovate.dll directly and every step is guarded, so a platform
		/// update that renames or removes the method degrades to "no pull available"
		/// rather than throwing. Results arrive through the same event as a live push.
		/// </summary>
		/// <returns>True when a request was actually issued.</returns>
		public bool RequestFullCalendar(out string detail)
		{
			detail = null;

			try
			{
				System.Collections.ICollection connections =
					NinjaTrader.Cbi.Connection.Connections as System.Collections.ICollection;

				if (connections == null || connections.Count == 0)
				{
					detail = "no connections yet";
					return false;
				}

				foreach (object c in connections)
				{
					if (c == null)
						continue;

					PropertyInfo statusProp = c.GetType().GetProperty("Status");
					object status = statusProp == null ? null : statusProp.GetValue(c, null);
					if (status == null || status.ToString() != "Connected")
						continue;

					PropertyInfo adapterProp = c.GetType().GetProperty("Adapter");
					object adapter = adapterProp == null ? null : adapterProp.GetValue(c, null);
					if (adapter == null)
						continue;

					MethodInfo pull = adapter.GetType().GetMethod("RequestCalendarsEconomic",
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
						null, Type.EmptyTypes, null);

					if (pull == null)
					{
						detail = "connected adapter " + adapter.GetType().Name + " has no RequestCalendarsEconomic";
						continue;
					}

					pull.Invoke(adapter, null);
					PullRequested = true;
					detail = "requested from " + adapter.GetType().FullName;
					return true;
				}

				detail = "no connected adapter exposes the calendar request";
				return false;
			}
			catch (Exception ex)
			{
				Exception real = ex is TargetInvocationException && ex.InnerException != null
					? ex.InnerException : ex;
				detail = real.GetType().Name + ": " + real.Message;
				return false;
			}
		}

		/// <summary>True once a full-calendar request has been issued successfully.</summary>
		public bool PullRequested { get; private set; }

		/// <summary>Detaches. MUST run from State.Terminated — the event is static.</summary>
		public void Unsubscribe()
		{
			if (!_subscribed || _handler == null)
				return;

			try   { NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived -= _handler; }
			catch { /* nothing useful to do on the way out */ }

			_handler    = null;
			_subscribed = false;
		}

		private void OnEconomicUpdate(object sender, NinjaTrader.Cbi.EconomicUpdateArgs e)
		{
			if (e == null)
				return;

			try
			{
				Nt8EconomicRelease r = new Nt8EconomicRelease
				{
					EventName     = e.EventName ?? string.Empty,
					Country       = e.Country   ?? string.Empty,
					Importance    = e.Importance,
					Actual        = e.Actual,
					Consensus     = e.Consensus,
					Prior         = e.Prior,
					Timestamp     = e.Timestamp,
					ReceivedLocal = DateTime.Now
				};

				lock (_gate)
				{
					_latest[Key(r.Country, r.EventName)] = r;
					ReceivedCount++;
				}
			}
			catch { /* a malformed push must not take the feed thread down */ }
		}

		/// <summary>
		/// Finds the release matching a scheduled calendar row. Exact key first, then a
		/// containment match within the same country, because the two sources name the
		/// same release differently ("Core PPI m/m" vs "PPI ex Food/Energy MoM").
		/// </summary>
		public Nt8EconomicRelease Find(string country, string title)
		{
			if (string.IsNullOrWhiteSpace(title))
				return null;

			lock (_gate)
			{
				Nt8EconomicRelease exact;
				if (_latest.TryGetValue(Key(country, title), out exact))
					return exact;

				string wantCountry = NormaliseCountry(country);
				string wantName    = Normalise(title);
				if (wantName.Length == 0)
					return null;

				Nt8EconomicRelease best = null;

				foreach (KeyValuePair<string, Nt8EconomicRelease> kv in _latest)
				{
					Nt8EconomicRelease r = kv.Value;

					if (wantCountry.Length > 0 && NormaliseCountry(r.Country) != wantCountry)
						continue;

					string haveName = Normalise(r.EventName);
					if (haveName.Length == 0)
						continue;

					if (haveName.IndexOf(wantName, StringComparison.Ordinal) < 0
					    && wantName.IndexOf(haveName, StringComparison.Ordinal) < 0)
						continue;

					// Most recent wins: a revision supersedes the first print.
					if (best == null || r.ReceivedLocal > best.ReceivedLocal)
						best = r;
				}

				return best;
			}
		}

		/// <summary>Everything received so far, newest first. Diagnostics only.</summary>
		public List<Nt8EconomicRelease> Snapshot()
		{
			lock (_gate)
			{
				List<Nt8EconomicRelease> all = new List<Nt8EconomicRelease>(_latest.Values);
				all.Sort((a, b) => b.ReceivedLocal.CompareTo(a.ReceivedLocal));
				return all;
			}
		}

		public string DescribeReceived(int max)
		{
			List<Nt8EconomicRelease> all = Snapshot();

			if (all.Count == 0)
				return "no releases received from NinjaTrader's economic calendar yet";

			StringBuilder sb = new StringBuilder();
			sb.Append(all.Count).Append(" release(s) received:");

			for (int i = 0; i < all.Count && i < max; i++)
				sb.Append("\n    ").Append(all[i]);

			return sb.ToString();
		}

		private static string Key(string country, string name)
		{
			return NormaliseCountry(country) + "|" + Normalise(name);
		}

		/// <summary>
		/// Lowercase, alphanumeric only, with the period suffixes spelled out FIRST.
		///
		/// The expansion is the part that matters and it is not cosmetic. ForexFactory
		/// writes "PPI m/m"; NinjaTrader writes "PPI (MoM)". Stripping punctuation alone
		/// gives "ppimm" and "ppimom", which do not match each other by containment in
		/// either direction, so every month-on-month release would silently fail to pair
		/// up and fall to the unknown-value rule. Verified against a real pull:
		/// NinjaTrader emits "PPI (MoM)", "Core PPI (MoM)", "Core PPI (YoY)".
		///
		///   "PPI m/m"    -> "ppimom"
		///   "PPI (MoM)"  -> "ppimom"
		/// </summary>
		private static string Normalise(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return string.Empty;

			string v = raw.ToLowerInvariant();

			// Longest first: "m/m" must not be rewritten before "mom" is considered.
			v = v.Replace("m/m", "mom").Replace("y/y", "yoy").Replace("q/q", "qoq");
			v = v.Replace("mm", "mom").Replace("yy", "yoy").Replace("qq", "qoq");

			StringBuilder sb = new StringBuilder(v.Length);

			for (int i = 0; i < v.Length; i++)
			{
				char c = v[i];
				if (char.IsLetterOrDigit(c))
					sb.Append(c);
			}

			return sb.ToString();
		}

		/// <summary>
		/// Maps the two conventions onto one code. The file writes ISO currency codes
		/// (USD, EUR); NinjaTrader writes country names or ISO country codes.
		/// </summary>
		private static string NormaliseCountry(string raw)
		{
			string v = Normalise(raw);

			switch (v)
			{
				case "usd": case "us": case "usa": case "unitedstates": return "usd";
				case "eur": case "eu": case "ez":  case "euro": case "eurozone": case "europeanunion": return "eur";
				case "gbp": case "uk": case "gb":  case "unitedkingdom": case "greatbritain": return "gbp";
				case "jpy": case "jp": case "japan": return "jpy";
				case "cad": case "ca": case "canada": return "cad";
				case "aud": case "au": case "australia": return "aud";
				case "nzd": case "nz": case "newzealand": return "nzd";
				case "chf": case "ch": case "switzerland": return "chf";
				case "cny": case "cn": case "china": return "cny";
				default: return v;
			}
		}
	}
}
