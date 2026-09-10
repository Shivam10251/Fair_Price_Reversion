// =============================================================================
//  FPMR · News · Nt8EconomicFeed
//
//  Reads the ACTUAL released figure from NinjaTrader's own economic calendar,
//  so the strategy does not depend on somebody typing a number into a CSV in
//  the sixty seconds between the release and the close of the news candle.
//
//  WHAT NINJATRADER ACTUALLY EXPOSES — read this before changing anything
//  ---------------------------------------------------------------------
//  NinjaTrader.Cbi.Calendar publishes a static event, EconomicEventUpdateReceived,
//  carrying EventName, Country, Importance, Actual, Consensus (the forecast),
//  Prior and a Timestamp. That is a PUSH, delivered as each release prints.
//
//  There is NO queryable list of upcoming releases anywhere in the public API —
//  reflection over NinjaTrader.Core.dll finds events and alert plumbing, and no
//  collection of scheduled economic events. So this class can answer
//  "what was the actual for the release that just happened?" and cannot answer
//  "what is scheduled at 18:00 today?".
//
//  The calendar FILE therefore remains the schedule, and this feed supplies the
//  number. Both halves are required; neither replaces the other.
//
//  CONSEQUENCES
//  * Nothing arrives in a backtest. Historical bars generate no calendar push, so
//    a backtest must use an Actual column in the file (FpNewsActualSource.FileOnly).
//  * Delivery depends on the connected provider supplying the calendar feed. If
//    nothing ever arrives, MatchedCount stays 0 and the strategy says so rather
//    than silently classifying every release off a missing number.
//  * The event fires on a feed thread, not the bar thread. Every read and write
//    of _latest is therefore under a lock.
//  * The subscription is to a STATIC event, so it outlives the strategy instance
//    unless it is removed. Unsubscribe() runs from State.Terminated.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
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
		/// Lowercase, alphanumeric only. Drops the punctuation and spacing that differ
		/// between the two naming conventions ("Core CPI m/m" -> "corecpimm").
		/// </summary>
		private static string Normalise(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return string.Empty;

			StringBuilder sb = new StringBuilder(raw.Length);

			for (int i = 0; i < raw.Length; i++)
			{
				char c = raw[i];
				if (char.IsLetterOrDigit(c))
					sb.Append(char.ToLowerInvariant(c));
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
