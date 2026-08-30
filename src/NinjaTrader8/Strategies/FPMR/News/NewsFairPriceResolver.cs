// =============================================================================
//  FPMR · News · NewsFairPriceResolver
//
//  Decides, per session, whether a qualifying release sits inside the lookback
//  window that precedes the session open, and captures the OPEN of the 1-minute
//  candle in which that release happened.
//
//  Determinism / no look-ahead: economic releases are SCHEDULED, so their times
//  are known days in advance. Selecting the winning event from the file ahead of
//  the session open uses no price information from the future. The Fair Price
//  itself is only ever taken from a candle that has actually printed.
// =============================================================================
using System;
using System.Collections.Generic;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class NewsFairPrice
	{
		public DateTime  SessionOpenTz;
		public int       SessionIndex;
		public double    FairPrice;
		public DateTime  NewsCandleOpenTz;
		public NewsEvent Event;
	}

	public sealed class NewsFairPriceResolver
	{
		private readonly List<NewsEvent>            _events;
		private readonly SessionManager             _sessions;
		private readonly FpNewsImpactFilter         _impactFilter;
		private readonly string[]                   _currencies;
		private readonly FpNewsMultipleEventRule    _multiRule;
		private readonly TimeSpan                   _lookback;

		// Winning event per session-open instant. Resolved from the file, cached.
		private readonly Dictionary<string, NewsEvent> _chosenCache = new Dictionary<string, NewsEvent>();

		// Captured Fair Price per session index, valid for one session open only.
		private readonly Dictionary<int, NewsFairPrice> _captured = new Dictionary<int, NewsFairPrice>();

		public NewsFairPriceResolver(List<NewsEvent> events, SessionManager sessions, FpNewsImpactFilter impactFilter,
		                             string currencyList, FpNewsMultipleEventRule multiRule, double lookbackHours)
		{
			_events       = events ?? new List<NewsEvent>();
			_sessions     = sessions;
			_impactFilter = impactFilter;
			_multiRule    = multiRule;
			_lookback     = TimeSpan.FromHours(Math.Max(0.0, lookbackHours));
			_currencies   = SplitCurrencies(currencyList);
		}

		public static string[] SplitCurrencies(string raw)
		{
			if (string.IsNullOrWhiteSpace(raw))
				return new string[0];

			string[] parts = raw.Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
			for (int i = 0; i < parts.Length; i++)
				parts[i] = parts[i].Trim();

			return parts;
		}

		/// <summary>
		/// The winning event for a given session open, or null.
		/// Window is [sessionOpen - lookback, sessionOpen) — the release must be BEFORE the open.
		/// </summary>
		public NewsEvent ChooseFor(DateTime sessionOpenTz)
		{
			string key = sessionOpenTz.ToString("yyyyMMddHHmm");

			NewsEvent cached;
			if (_chosenCache.TryGetValue(key, out cached))
				return cached;

			DateTime from = sessionOpenTz - _lookback;
			NewsEvent winner = null;

			foreach (NewsEvent ev in _events)
			{
				if (ev.TimeSessionTz < from || ev.TimeSessionTz >= sessionOpenTz)
					continue;
				if (!ev.MatchesImpact(_impactFilter))
					continue;
				if (!ev.MatchesCurrency(_currencies))
					continue;

				if (winner == null)
				{
					winner = ev;
					continue;
				}

				switch (_multiRule)
				{
					case FpNewsMultipleEventRule.First:
						// _events is time-sorted, so the first match already wins.
						break;

					case FpNewsMultipleEventRule.Last:
						winner = ev;
						break;

					case FpNewsMultipleEventRule.HighestImpact:
						if (ev.Impact > winner.Impact)
							winner = ev;
						break;
				}
			}

			_chosenCache[key] = winner;
			return winner;
		}

		/// <summary>
		/// Feed every reference-timeframe bar here. When the bar is the candle that
		/// contains the winning release for an upcoming session, its OPEN is captured
		/// as that session's Fair Price.
		/// Returns the capture when one was made on THIS bar, otherwise null.
		/// </summary>
		public NewsFairPrice OnReferenceBar(DateTime tzBarOpen, TimeSpan barLength, double barOpenPrice)
		{
			NewsFairPrice made = null;

			foreach (SessionWindow w in _sessions.Windows)
			{
				if (!w.IsActive)
					continue;

				DateTime nextOpen = w.NextOpen(tzBarOpen);

				// Drop a capture that belongs to a session open we have already passed.
				NewsFairPrice held;
				if (_captured.TryGetValue(w.Index, out held) && held.SessionOpenTz != nextOpen)
					_captured.Remove(w.Index);

				if (tzBarOpen < nextOpen - _lookback || tzBarOpen >= nextOpen)
					continue;

				NewsEvent chosen = ChooseFor(nextOpen);
				if (chosen == null)
					continue;

				// Is the release inside THIS candle?  [barOpen, barOpen + length)
				if (chosen.TimeSessionTz < tzBarOpen || chosen.TimeSessionTz >= tzBarOpen + barLength)
					continue;

				NewsFairPrice capture = new NewsFairPrice
				{
					SessionOpenTz    = nextOpen,
					SessionIndex     = w.Index,
					FairPrice        = barOpenPrice,
					NewsCandleOpenTz = tzBarOpen,
					Event            = chosen
				};

				_captured[w.Index] = capture;
				made = capture;
			}

			return made;
		}

		/// <summary>The capture waiting for a session, or null when there is none.</summary>
		public NewsFairPrice PendingFor(int sessionIndex, DateTime sessionOpenTz)
		{
			NewsFairPrice held;
			if (!_captured.TryGetValue(sessionIndex, out held))
				return null;

			return held.SessionOpenTz == sessionOpenTz ? held : null;
		}

		/// <summary>Clears captured state — used when the strategy restarts its bar processing.</summary>
		public void Reset()
		{
			_captured.Clear();
		}
	}
}
