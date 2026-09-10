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
//
//  SURPRISE BRANCHING
//  The pre-news price is only defensible as Fair Price when the release matched
//  its forecast — that is the case where the market had already priced it in.
//  When ACTUAL differs meaningfully from FORECAST the market may have legitimately
//  repriced, so the capture is DEFERRED: no Fair Price goes live until the post-
//  news range compression finds a new accepted price. Until then the strategy has
//  no Fair Price for that session and therefore takes no trades.
//
//  LIMITATION: one deferred capture is resolved at a time. Two sessions whose news
//  candles land on the same minute would share the detector; the later one wins.
//  That combination does not occur with a single-currency calendar filter.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class NewsFairPrice
	{
		public DateTime  SessionOpenTz;
		public int       SessionIndex;
		public double    FairPrice;
		public DateTime  NewsCandleOpenTz;
		public NewsEvent Event;

		/// <summary>Open of the news candle — the price the market held going in.</summary>
		public double              PreNewsPrice;
		public FpNewsSurprise      Surprise;
		public NewsSurpriseResult  SurpriseResult;
		public FpNewsBias          Bias;

		/// <summary>True when the release landed INSIDE the session rather than before it.</summary>
		public bool   InSession;

		/// <summary>True while waiting for post-news consolidation to name a new Fair Price.</summary>
		public bool   AwaitingConsolidation;
		/// <summary>Sign of the displacement measured on the news candle. 0 until known.</summary>
		public int    DisplacementDirection;
		/// <summary>Magnitude of the news-candle displacement, in price.</summary>
		public double DisplacementSize;

		public bool IsLive { get { return !AwaitingConsolidation && !double.IsNaN(FairPrice); } }
	}

	/// <summary>
	/// A direct entry request produced by a PRE-SESSION release whose actual missed
	/// its forecast by more than the threshold. The market repriced for a real
	/// reason, so the first trade goes WITH the news candle rather than fading it,
	/// entered at that candle's close on a fixed stop and a fixed target.
	///
	/// An IN-SESSION surprise never produces one of these: there the session's own
	/// Fair Price stands and the strategy keeps trading reversion against it.
	/// </summary>
	public sealed class NewsContinuationSignal
	{
		public NewsEvent Event;
		/// <summary>+1 to buy, -1 to sell. Sign of the news candle's body.</summary>
		public int       Direction;
		public double    NewsCandleOpen;
		public double    NewsCandleClose;
		public DateTime  NewsCandleOpenTz;
		public int       SessionIndex;
		public DateTime  SessionOpenTz;
		public NewsSurpriseResult SurpriseResult;
	}

	public sealed class NewsFairPriceResolver
	{
		private readonly List<NewsEvent>            _events;
		private readonly SessionManager             _sessions;
		private readonly FpNewsImpactFilter         _impactFilter;
		private readonly string[]                   _currencies;
		private readonly FpNewsMultipleEventRule    _multiRule;
		private readonly TimeSpan                   _lookback;

		private readonly double                _expectedTolerancePercent;
		private readonly double                _unexpectedThresholdPercent;
		private readonly FpNewsUnknownRule     _unknownRule;
		private readonly bool                  _continuationEnabled;
		private readonly ConsolidationDetector _consolidation;

		/// <summary>Live NinjaTrader calendar, or null when the file is the only source.</summary>
		private readonly Nt8EconomicFeed       _feed;
		private readonly FpNewsActualSource    _actualSource;
		/// <summary>Whether a release landing INSIDE a session may re-anchor Fair Price.</summary>
		private readonly bool                  _handleInSession;

		/// <summary>The single capture currently waiting on post-news consolidation.</summary>
		private NewsFairPrice _awaiting;

		/// <summary>Per-event report text, emitted once when the branch is decided.</summary>
		public string LastReport { get; private set; }
		/// <summary>Set on the bar LastReport changed, so the caller can print it exactly once.</summary>
		public bool   ReportIsNew { get; private set; }

		// Winning event per session-open instant. Resolved from the file, cached.
		private readonly Dictionary<string, NewsEvent> _chosenCache = new Dictionary<string, NewsEvent>();

		// Captured Fair Price per session index, valid for one session open only.
		private readonly Dictionary<int, NewsFairPrice> _captured = new Dictionary<int, NewsFairPrice>();

		public NewsFairPriceResolver(List<NewsEvent> events, SessionManager sessions, FpNewsImpactFilter impactFilter,
		                             string currencyList, FpNewsMultipleEventRule multiRule, double lookbackHours,
		                             double expectedTolerancePercent, double unexpectedThresholdPercent,
		                             FpNewsUnknownRule unknownRule, bool continuationEnabled,
		                             ConsolidationDetector consolidation,
		                             Nt8EconomicFeed feed, FpNewsActualSource actualSource,
		                             bool handleInSession)
		{
			_events       = events ?? new List<NewsEvent>();
			_sessions     = sessions;
			_impactFilter = impactFilter;
			_multiRule    = multiRule;
			_lookback     = TimeSpan.FromHours(Math.Max(0.0, lookbackHours));
			_currencies   = SplitCurrencies(currencyList);

			_expectedTolerancePercent   = expectedTolerancePercent;
			_unexpectedThresholdPercent = unexpectedThresholdPercent;
			_unknownRule                = unknownRule;
			_continuationEnabled        = continuationEnabled;
			_consolidation              = consolidation;
			_feed                       = feed;
			_actualSource               = actualSource;
			_handleInSession            = handleInSession;
		}

		/// <summary>
		/// The continuation entry produced on this bar, taken by the caller exactly once.
		/// Null on every bar that did not produce one.
		/// </summary>
		public NewsContinuationSignal PendingContinuation { get; private set; }

		public NewsContinuationSignal TakeContinuation()
		{
			NewsContinuationSignal s = PendingContinuation;
			PendingContinuation = null;
			return s;
		}

		/// <summary>
		/// Fills in the ACTUAL from NinjaTrader's live calendar before classification.
		/// The file supplies the schedule and the forecast; this supplies the number
		/// that only exists once the release has printed.
		/// </summary>
		private void ResolveActual(NewsEvent ev)
		{
			if (ev == null)
				return;

			if (_actualSource == FpNewsActualSource.FileOnly)
			{
				ev.ActualOrigin = ev.ActualRaw != null ? "calendar file" : "none";
				return;
			}

			if (_feed != null && !ev.FeedMatched)
			{
				Nt8EconomicRelease live = _feed.Find(ev.Currency, ev.Title);
				if (live != null)
					ev.ApplyFeed(live);
			}

			if (!ev.FeedMatched)
			{
				// Nt8CalendarOnly: an unmatched release has no actual at all, so the
				// unknown-value rule decides rather than a stale figure from the file.
				if (_actualSource == FpNewsActualSource.Nt8CalendarOnly)
				{
					ev.Actual       = double.NaN;
					ev.ActualOrigin = "none (NinjaTrader calendar delivered nothing for this release)";
				}
				else
				{
					ev.ActualOrigin = ev.ActualRaw != null
						? "calendar file (no live push matched)"
						: "none (no live push, no Actual column in the file)";
				}
			}
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
		/// Feed every reference-timeframe bar here.
		///
		/// Two jobs, in this order:
		///   1. If this candle CONTAINS the winning release for an upcoming session,
		///      classify the surprise and pick the branch.
		///        expected -> Fair Price = this candle's OPEN, live immediately.
		///        surprise -> nothing goes live; arm the consolidation search.
		///   2. If a deferred capture is waiting, feed this candle to the detector.
		///      Fair Price goes live on the bar the consolidation window completes.
		///
		/// Returns the capture that went LIVE on this bar, otherwise null.
		/// </summary>
		public NewsFairPrice OnReferenceBar(DateTime tzBarOpen, TimeSpan barLength, int barIndex,
		                                    double barOpen, double barHigh, double barLow, double barClose)
		{
			ReportIsNew = false;

			// A release either precedes a session (where it may re-anchor that session's
			// Fair Price, or fire a continuation trade) or lands inside one (where it may
			// only re-anchor Fair Price). Both passes run; only one can match a candle.
			NewsFairPrice made = DetectNewsCandle(tzBarOpen, barLength, barIndex, barOpen, barHigh, barLow, barClose);

			if (made == null && _handleInSession)
				made = DetectInSessionNewsCandle(tzBarOpen, barLength, barOpen, barClose);

			if (made != null)
				return made;

			return AdvanceConsolidation(barIndex, barHigh, barLow);
		}

		private NewsFairPrice DetectNewsCandle(DateTime tzBarOpen, TimeSpan barLength, int barIndex,
		                                       double barOpen, double barHigh, double barLow, double barClose)
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

				// The file is the schedule and the forecast; the ACTUAL is only knowable
				// once the release has printed, so it is resolved here, not at load time.
				ResolveActual(chosen);

				NewsSurpriseResult sr = NewsSurpriseClassifier.Classify(
					chosen, _expectedTolerancePercent, _unexpectedThresholdPercent, _unknownRule);

				// SkipEvent: the file gave nothing to judge on, so this release overrides
				// nothing and the normal first-candle rule applies to the session.
				if (sr.Surprise == FpNewsSurprise.Unknown)
					continue;

				FpNewsBias bias = NewsSurpriseClassifier.BiasFor(sr.Surprise, _continuationEnabled);

				NewsFairPrice capture = new NewsFairPrice
				{
					SessionOpenTz         = nextOpen,
					SessionIndex          = w.Index,
					NewsCandleOpenTz      = tzBarOpen,
					Event                 = chosen,
					PreNewsPrice          = barOpen,
					Surprise              = sr.Surprise,
					SurpriseResult        = sr,
					Bias                  = bias,
					DisplacementDirection = barClose > barOpen ? 1 : barClose < barOpen ? -1 : 0,
					DisplacementSize      = Math.Abs(barClose - barOpen)
				};

				if (sr.Surprise == FpNewsSurprise.Expected)
				{
					// Priced in. The price the market held going into the release is the
					// fair one, and the displacement after it is the thing to fade.
					capture.FairPrice             = barOpen;
					capture.AwaitingConsolidation = false;

					_captured[w.Index] = capture;
					_awaiting          = null;
					if (_consolidation != null)
						_consolidation.Reset();

					made = capture;
				}
				else if (_continuationEnabled)
				{
					// The actual missed its forecast by more than the threshold, so the move
					// is legitimate repricing rather than something to fade. Trade WITH the
					// news candle, entered at its close on a fixed stop and target.
					//
					// No Fair Price override is made. The session that follows keeps its own
					// first-candle rule, which is what any follow-on entry works from.
					capture.FairPrice             = double.NaN;
					capture.AwaitingConsolidation = false;

					_captured.Remove(w.Index);
					_awaiting = null;

					if (capture.DisplacementDirection != 0)
					{
						PendingContinuation = new NewsContinuationSignal
						{
							Event            = chosen,
							Direction        = capture.DisplacementDirection,
							NewsCandleOpen   = barOpen,
							NewsCandleClose  = barClose,
							NewsCandleOpenTz = tzBarOpen,
							SessionIndex     = w.Index,
							SessionOpenTz    = nextOpen,
							SurpriseResult   = sr
						};
					}
				}
				else
				{
					// Continuation disabled: refuse to name a Fair Price until the post-news
					// range says where price is being accepted.
					capture.FairPrice             = double.NaN;
					capture.AwaitingConsolidation = true;

					_captured.Remove(w.Index);
					_awaiting = capture;
					if (_consolidation != null)
						_consolidation.Arm(barIndex);
				}

				WriteReport(capture);
			}

			return made;
		}

		/// <summary>
		/// A release that lands INSIDE an open session.
		///
		/// The two branches are deliberately asymmetric, and this is the whole rule:
		///   ACTUAL ~= FORECAST  -> the release was priced in, so the move it caused is
		///                          unfair. The news candle's OPEN replaces the session's
		///                          Fair Price and the rest of the session reverts to it.
		///   ACTUAL != FORECAST  -> the market repriced for a real reason, but mid-session
		///                          there is no continuation trade. The session's own
		///                          first-candle Fair Price simply STANDS, untouched, and
		///                          the strategy keeps trading reversion against it.
		///
		/// So a mid-session surprise is a no-op here, which is why this returns null for
		/// it rather than arming anything.
		/// </summary>
		private NewsFairPrice DetectInSessionNewsCandle(DateTime tzBarOpen, TimeSpan barLength,
		                                                double barOpen, double barClose)
		{
			int sessionIndex = _sessions.IndexAt(tzBarOpen);
			if (sessionIndex == 0)
				return null;

			SessionWindow window = null;
			foreach (SessionWindow w in _sessions.Windows)
				if (w.Index == sessionIndex)
					{ window = w; break; }

			if (window == null)
				return null;

			DateTime sessionOpenTz = window.PreviousOpen(tzBarOpen);

			// Only one in-session re-anchor per session: the first qualifying release wins,
			// so a cluster of same-minute prints cannot keep moving Fair Price underneath
			// a trade that is already running against it.
			NewsFairPrice existing;
			if (_captured.TryGetValue(sessionIndex, out existing)
				&& existing.InSession && existing.SessionOpenTz == sessionOpenTz)
				return null;

			NewsEvent chosen = null;

			foreach (NewsEvent ev in _events)
			{
				if (ev.TimeSessionTz < tzBarOpen || ev.TimeSessionTz >= tzBarOpen + barLength)
					continue;
				if (!ev.MatchesImpact(_impactFilter) || !ev.MatchesCurrency(_currencies))
					continue;

				chosen = ev;
				break;
			}

			if (chosen == null)
				return null;

			ResolveActual(chosen);

			NewsSurpriseResult sr = NewsSurpriseClassifier.Classify(
				chosen, _expectedTolerancePercent, _unexpectedThresholdPercent, _unknownRule);

			NewsFairPrice capture = new NewsFairPrice
			{
				SessionOpenTz         = sessionOpenTz,
				SessionIndex          = sessionIndex,
				InSession             = true,
				NewsCandleOpenTz      = tzBarOpen,
				Event                 = chosen,
				PreNewsPrice          = barOpen,
				Surprise              = sr.Surprise,
				SurpriseResult        = sr,
				Bias                  = FpNewsBias.Reversion,
				DisplacementDirection = barClose > barOpen ? 1 : barClose < barOpen ? -1 : 0,
				DisplacementSize      = Math.Abs(barClose - barOpen)
			};

			// SkipEvent with nothing to judge on: the release overrides nothing and the
			// session's own first-candle rule applies. Not a "missed forecast".
			if (sr.Surprise == FpNewsSurprise.Unknown)
			{
				capture.FairPrice = double.NaN;
				LastReport  = BuildReport(capture, "release landed INSIDE the session but carried no actual to judge on - "
					+ "the session's own Fair Price stands, untouched");
				ReportIsNew = true;
				return null;
			}

			if (sr.Surprise != FpNewsSurprise.Expected)
			{
				// Surprise mid-session. Nothing is overridden and nothing is armed; the
				// session Fair Price stands. Reported so the log explains the non-event.
				capture.FairPrice = double.NaN;
				LastReport  = BuildReport(capture, "release landed INSIDE the session and MISSED its forecast - "
					+ "no continuation mid-session, the session's own Fair Price stands and reversion continues against it");
				ReportIsNew = true;
				return null;
			}

			// Priced in: the price held going into the release is the fair one from here on.
			capture.FairPrice             = barOpen;
			capture.AwaitingConsolidation = false;

			_captured[sessionIndex] = capture;
			_awaiting               = null;
			if (_consolidation != null)
				_consolidation.Reset();

			LastReport  = BuildReport(capture, "release landed INSIDE the session and MATCHED its forecast - "
				+ "the news candle's open replaces the session Fair Price for the rest of the session");
			ReportIsNew = true;
			return capture;
		}

		private NewsFairPrice AdvanceConsolidation(int barIndex, double barHigh, double barLow)
		{
			if (_awaiting == null || _consolidation == null)
				return null;

			ConsolidationResult c = _consolidation.OnBar(barIndex, barHigh, barLow);

			if (c.Expired)
			{
				// Never settled. Abandon the override rather than guess a level; the
				// session then simply runs without a news Fair Price.
				NewsFairPrice dead = _awaiting;
				_awaiting   = null;
				LastReport  = BuildReport(dead, "consolidation never formed inside the search window - news override abandoned");
				ReportIsNew = true;
				return null;
			}

			if (!c.Found)
				return null;

			NewsFairPrice cap = _awaiting;
			cap.FairPrice             = c.FairPrice;
			cap.AwaitingConsolidation = false;

			_captured[cap.SessionIndex] = cap;
			_awaiting = null;

			LastReport  = BuildReport(cap, "new Fair Price accepted at the post-news consolidation midpoint");
			ReportIsNew = true;
			return cap;
		}

		/// <summary>True while a surprise is waiting for its new Fair Price.</summary>
		public bool IsAwaitingConsolidation { get { return _awaiting != null; } }

		/// <summary>Bias attached to the capture governing this session, Reversion when there is none.</summary>
		public FpNewsBias BiasFor(int sessionIndex, DateTime sessionOpenTz)
		{
			NewsFairPrice held = PendingFor(sessionIndex, sessionOpenTz);
			return held == null ? FpNewsBias.Reversion : held.Bias;
		}

		private void WriteReport(NewsFairPrice cap)
		{
			LastReport = BuildReport(cap, cap.AwaitingConsolidation
				? "waiting for post-news consolidation to establish a new Fair Price"
				: "pre-news price stands as Fair Price");
			ReportIsNew = true;
		}

		/// <summary>
		/// The per-event summary. Printed once per decision so a backtest log reads
		/// back as a sequence of news judgements rather than raw order flow.
		/// </summary>
		private static string BuildReport(NewsFairPrice cap, string note)
		{
			NewsEvent e = cap.Event;
			StringBuilder sb = new StringBuilder();

			sb.AppendLine("-------- NEWS --------");
			sb.AppendLine("NEWS          : " + (e == null ? "-" : e.Title));
			sb.AppendLine("SCHEDULED?    : Yes (calendar)");
			sb.AppendLine("FORECAST      : " + (e == null || e.ForecastRaw == null ? "-" : e.ForecastRaw));
			sb.AppendLine("ACTUAL        : " + (e == null ? "-" : e.HasActual
				? e.EffectiveActual.ToString("0.####", CultureInfo.InvariantCulture)
				: "-"));
			sb.AppendLine("ACTUAL SOURCE : " + (e == null ? "-" : e.ActualOrigin));
			sb.AppendLine("WHERE         : " + (cap.InSession ? "inside the session window" : "before the session opened"));
			sb.AppendLine("CLASSIFICATION: " + (cap.SurpriseResult == null ? cap.Surprise.ToString() : cap.SurpriseResult.Describe()));
			sb.AppendLine("PRE-NEWS PRICE: " + cap.PreNewsPrice.ToString("0.#####", CultureInfo.InvariantCulture));
			sb.AppendLine("DISPLACEMENT  : " + (cap.DisplacementDirection > 0 ? "Up " : cap.DisplacementDirection < 0 ? "Down " : "flat ")
			                                 + cap.DisplacementSize.ToString("0.##", CultureInfo.InvariantCulture) + " pts on the news candle");
			sb.AppendLine("FAIR PRICE    : " + (double.IsNaN(cap.FairPrice)
				? "pending"
				: cap.FairPrice.ToString("0.#####", CultureInfo.InvariantCulture)
				  + (cap.Surprise == FpNewsSurprise.Expected ? "  (pre-news price)" : "  (post-news consolidation midpoint)")));
			sb.AppendLine("STRATEGY      : " + cap.Bias);
			sb.Append    ("REASON        : " + note);

			return sb.ToString();
		}

		/// <summary>The capture waiting for a session, or null when there is none.</summary>
		public NewsFairPrice PendingFor(int sessionIndex, DateTime sessionOpenTz)
		{
			NewsFairPrice held;
			if (!_captured.TryGetValue(sessionIndex, out held))
				return null;

			// A deferred capture stays invisible until consolidation names its Fair
			// Price, so nothing downstream can trade off a level that is still pending.
			if (!held.IsLive)
				return null;

			return held.SessionOpenTz == sessionOpenTz ? held : null;
		}

		/// <summary>Clears captured state — used when the strategy restarts its bar processing.</summary>
		public void Reset()
		{
			_captured.Clear();
			_awaiting = null;
			if (_consolidation != null)
				_consolidation.Reset();
		}
	}
}
