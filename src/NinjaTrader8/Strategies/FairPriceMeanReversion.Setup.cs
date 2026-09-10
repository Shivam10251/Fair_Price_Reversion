// =============================================================================
//  FAIR PRICE MEAN REVERSION  —  NinjaTrader 8
//  Part 2b of 3 — configuration, module wiring and restart reconciliation.
//
//  Everything here runs once per strategy instance (State.Configure /
//  State.DataLoaded / State.Realtime), never per bar. The news calendar in
//  particular is read from disk exactly once.
// =============================================================================
#region Using declarations
using System;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FPMR;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class FairPriceMeanReversion : Strategy
	{
		// ── State.Configure ───────────────────────────────────────────────────────
		private void ConfigureSeriesAndOrders()
		{
			// Concurrency. Pine used pyramiding = 0; NinjaTrader needs the ceiling
			// stated explicitly when the "one at a time" toggle is off.
			if (OnlyOneOpenTrade)
			{
				EntriesPerDirection = 1;
				EntryHandling       = EntryHandling.AllEntries;
			}
			else
			{
				EntriesPerDirection = Math.Max(1, MaxConcurrentEntriesPerDirection);
				EntryHandling       = EntryHandling.UniqueEntries;
			}

			// Same-bar TP/SL is the one ambiguity the TradingView build could not solve.
			// OrderFillResolution.High replays real 1-tick data through the fill engine,
			// which is NinjaTrader's built-in form of "add a 1-tick series"; adding a
			// second visible tick series on top of it would only double the data load.
			if (UseTickPrecision && BarsPeriod.BarsPeriodType != BarsPeriodType.Tick)
			{
				OrderFillResolution      = OrderFillResolution.High;
				OrderFillResolutionType  = BarsPeriodType.Tick;
				OrderFillResolutionValue = 1;
			}
			else
			{
				OrderFillResolution = OrderFillResolution.Standard;
			}

			// Fair Price reference series. Reuse the primary when it already matches,
			// so the common 1-minute chart loads no extra data.
			if (BarsPeriod.BarsPeriodType == BarsPeriodType.Minute && BarsPeriod.Value == FairPriceReferenceMinutes)
			{
				_fpSeriesIndex = 0;
			}
			else
			{
				AddDataSeries(BarsPeriodType.Minute, FairPriceReferenceMinutes);
				_fpSeriesIndex = 1;
			}
		}

		// ── State.DataLoaded ──────────────────────────────────────────────────────
		private void BuildModules()
		{
			_configError     = false;
			_configErrorText = string.Empty;

			_tickSize   = Instrument.MasterInstrument.TickSize;
			_pointValue = Instrument.MasterInstrument.PointValue;

			// The three setup percentages must be strictly ordered so the zone and the
			// two bands never overlap. Fixed TP/SL, when on, needs positive distances.
			if (Band1Percent <= ZonePercent)
				Fail("Band 1 % (" + Band1Percent + ") must be greater than the non-tradeable zone % (" + ZonePercent + ").");
			else if (Band2Percent <= Band1Percent)
				Fail("Band 2 % (" + Band2Percent + ") must be greater than Band 1 % (" + Band1Percent + ").");

			// The entry model inverts the direction of every trade, so it is stated once
			// at load rather than left to be inferred from the entry log.
			if (EntryModel == FpEntryModel.BosContinuation)
				Print("FPMR: entry model is BOS CONTINUATION. Above the Fair Price zone only LONGS on a bullish "
				    + "BOS, below it only SHORTS on a bearish BOS. Every CHoCH is refused, whatever 'Take CHoCH "
				    + "entries' says, and so is any break pointing back toward Fair Price. FAR-band setups take "
				    + "the R:R target, because Fair Price now sits behind a trade running away from it.");

			if (StopMode == FpStopMode.FixedPoints && FixedStopLossPoints <= 0.0)
				Fail("Fixed stop loss (points) must be greater than zero when the stop mode is FixedPoints.");

			if (TargetMode == FpTargetMode.FixedPoints && FixedTakeProfitPoints <= 0.0)
				Fail("Fixed take profit (points) must be greater than zero when the target mode is FixedPoints.");

			if (NewsTradeContinuation && NewsContinuationStopPoints <= 0.0)
				Fail("Continuation stop loss (points) must be greater than zero when news continuation is on.");

			// The take-profit zone pulls the target back toward the entry. If it were
			// wider than the no-trade zone the target could land on the WRONG side of
			// Fair Price, i.e. behind the entry, and the bracket would never fill.
			if (TargetMode != FpTargetMode.FixedPoints && TakeProfitZonePoints > 0.0)
				Print(string.Format(CultureInfo.InvariantCulture,
					"FPMR: take-profit zone is {0} points inside Fair Price. Every Fair Price target stops "
				  + "that far short of the mean, and an entry with less than {0} points of room to Fair Price "
				  + "is refused by the minimum reward:risk gate.", TakeProfitZonePoints));

			_sessionTz = TimeZoneRegistry.Resolve(SessionTimeZoneId);
			if (_sessionTz == null)
				Fail("Session timezone '" + SessionTimeZoneId + "' could not be resolved on this machine.");

			_barTz = string.IsNullOrWhiteSpace(BarTimeZoneOverrideId)
				? ResolveBarTimeZone()
				: TimeZoneRegistry.Resolve(BarTimeZoneOverrideId);

			if (_barTz == null)
				Fail("Bar timezone override '" + BarTimeZoneOverrideId + "' could not be resolved.");

			_primaryBarLength = PeriodLength(BarsPeriod);
			_fpBarLength      = TimeSpan.FromMinutes(FairPriceReferenceMinutes);

			if (_primaryBarLength == TimeSpan.Zero)
				Print("FPMR WARNING: the primary series is not time based, so bar OPEN times cannot be derived. "
				    + "Session membership will use the bar's CLOSE time instead. Use a minute or second chart for full fidelity.");

			// Rebase the typed SUMMER windows onto the DST anchor zone, where the same
			// market hours have ONE fixed clock time all year. See FPMR/Core/DstAnchor.
			TimeZoneInfo evaluationTz = _sessionTz;
			TimeSpan     summerShift  = TimeSpan.Zero;

			string w1 = Session1Window, w2 = Session2Window, w3 = Session3Window;

			if (AutoAdjustForUsDst)
			{
				TimeZoneInfo anchor = TimeZoneRegistry.Resolve(DstAnchor.AnchorTimeZoneId);

				if (anchor == null)
				{
					Fail("The DST anchor zone '" + DstAnchor.AnchorTimeZoneId
					   + "' could not be resolved on this machine.");
				}
				else if (_sessionTz != null && !_sessionTz.Equals(anchor))
				{
					summerShift  = DstAnchor.SummerShift(_sessionTz, anchor);
					evaluationTz = anchor;

					w1 = DstAnchor.ShiftWindow(w1, -summerShift);
					w2 = DstAnchor.ShiftWindow(w2, -summerShift);
					w3 = DstAnchor.ShiftWindow(w3, -summerShift);
				}
			}

			_sessionTz = evaluationTz;

			_sessions = new SessionManager(
				_sessionTz ?? TimeZoneInfo.Utc,
				new SessionWindow(1, Session1Enabled, w1),
				new SessionWindow(2, Session2Enabled, w2),
				new SessionWindow(3, Session3Enabled, w3));

			PrintSessionPlan(summerShift);

			string windowError = _sessions.FirstConfigError();
			if (windowError != null)
				Fail(windowError);

			_structure = new StructureEngine(ActiveLevelMode);
			_fair      = new FairPriceEngine(NewsFairPriceExpiresAtSessionOpen);
			_vwap      = new SessionVwap();

			// Only construct what the filter will actually read — an unused EMA is pure
			// per-bar cost, and a null leg is what tells the gate that leg is disabled.
			_emaFast = UseEmaFilter && UseEma1 ? EMA(EmaFastLength) : null;
			_emaSlow = UseEmaFilter && UseEma2 ? EMA(EmaSlowLength) : null;

			_highAt = i => High[i];
			_lowAt  = i => Low[i];

			LoadNewsCalendar();

			_trades.Clear();
			_openTrades.Clear();
			_justClosed.Clear();

			_fpCursor        = 0;
			_prevEffSession  = 0;
			_tradingStartBar = -1;
			_hadFairPrev     = false;
			_tradeSeq        = 0;
			_tradesDay       = 0;
			_tradesSession   = 0;
			_ambiguousCount  = 0;
			_unreconciled    = false;

			_realisedPnlDay   = 0.0;
			_dayLossHit       = false;
			_dayProfitHit     = false;
			_flattenRequested = false;
			_pnlDay           = DateTime.MinValue;

			Print(string.Format(CultureInfo.InvariantCulture,
				"FPMR loaded: {0} | tick {1} | point value ${2} | 1 pt = ${2} | session tz {3} | bar tz {4} | "
			  + "bar->session shift {5} | trading-hours template tz {6} (not used for bar times){7}",
				Instrument.FullName, _tickSize, _pointValue,
				_sessionTz == null ? "?" : _sessionTz.Id,
				_barTz == null ? "?" : _barTz.Id,
				DescribeShift(),
				TradingHoursTimeZoneName(),
				_configError ? " | CONFIG ERROR: " + _configErrorText : string.Empty));
		}

		private void LoadNewsCalendar()
		{
			_news     = null;
			_newsLoad = null;

			// Master gate: with news trading off, the calendar is never loaded and no
			// news-related entry or setup can fire, whatever the sub-toggles say.
			if (!UseNewsTrading || !UseNewsFairPrice)
				return;

			_newsTz = TimeZoneRegistry.Resolve(NewsFileTimeZoneId);
			if (_newsTz == null)
			{
				Log("FPMR: news file timezone '" + NewsFileTimeZoneId + "' could not be resolved. "
				  + "News Fair Price is DISABLED for this run; normal Fair Price applies.", LogLevel.Error);
				return;
			}

			_newsLoad = NewsCalendarLoader.Load(NewsFilePath, _newsTz, _sessionTz ?? TimeZoneInfo.Utc, NewsCsvDateFormat);

			if (!_newsLoad.Success)
			{
				// Fail loudly and safely: never trade a wrong reference price silently.
				Log("FPMR: news calendar could not be loaded — " + _newsLoad.Error
				  + " | News Fair Price is DISABLED for this run; the normal first-candle rule applies.", LogLevel.Error);
				return;
			}

			// With surprise classification off the resolver is handed a tolerance that
			// nothing can exceed and no detector, so every qualifying release keeps the
			// pre-news price as Fair Price — byte-for-byte the previous behaviour.
			ConsolidationDetector detector = UseNewsSurprise
				? new ConsolidationDetector(NewsConsolidationBars,
				                            NewsConsolidationRangeTicks * _tickSize,
				                            NewsConsolidationSearchBars)
				: null;

			SubscribeToNt8Calendar();

			_news = new NewsFairPriceResolver(_newsLoad.Events, _sessions, NewsImpactFilter,
			                                  NewsCurrencyFilter, NewsMultipleEventRule, NewsLookbackHours,
			                                  UseNewsSurprise ? NewsExpectedTolerancePercent   : double.MaxValue,
			                                  UseNewsSurprise ? NewsUnexpectedThresholdPercent : double.MaxValue,
			                                  UseNewsSurprise ? NewsUnknownRule : FpNewsUnknownRule.TreatAsExpected,
			                                  UseNewsSurprise && (NewsAllowContinuation || NewsTradeContinuation),
			                                  detector,
			                                  _nt8Calendar, NewsActualSource, NewsHandleInSession);

			int withBoth = 0;
			foreach (NewsEvent e in _newsLoad.Events)
				if (e.HasForecast && e.HasActual)
					withBoth++;

			Print(string.Format(CultureInfo.InvariantCulture,
				"FPMR news calendar: {0} events from {1} ({2}, dates {3}), {4} rows skipped. "
			  + "Rows without an explicit UTC offset were read as {5}.",
				_newsLoad.Events.Count, NewsFilePath, _newsLoad.DetectedFormat, _newsLoad.DetectedDatePattern,
				_newsLoad.SkippedRows, _newsTz.Id));

			if (UseNewsSurprise)
				Print(string.Format(CultureInfo.InvariantCulture,
					"FPMR news surprise: {0} of {1} events carry BOTH a forecast and an actual. "
				  + "The other {2} fall to the '{3}' rule. Expected <= {4}%, large surprise > {5}%. "
				  + "Consolidation = {6} bars inside {7} ticks, searched for {8} bars. Continuation {9}.",
					withBoth, _newsLoad.Events.Count, _newsLoad.Events.Count - withBoth, NewsUnknownRule,
					NewsExpectedTolerancePercent, NewsUnexpectedThresholdPercent,
					NewsConsolidationBars, NewsConsolidationRangeTicks, NewsConsolidationSearchBars,
					NewsAllowContinuation ? "ON" : "OFF"));

			if (UseNewsSurprise && withBoth == 0)
			{
				if (NewsActualSource == FpNewsActualSource.FileOnly)
				{
					Log("FPMR: surprise classification is ON, the actual source is FileOnly, and NO event in the "
					  + "calendar file carries an actual value — the file has no 'Actual' column. Every release "
					  + "will fall to the '" + NewsUnknownRule + "' rule, so no news branch can fire. Either add an "
					  + "Actual column or set the actual source to Nt8CalendarThenFile.", LogLevel.Warning);
				}
				else
				{
					Print("FPMR news: the calendar file carries no Actual column, which is normal for a "
					    + "forward-looking export. The actual for each release will come from NinjaTrader's live "
					    + "economic calendar as it prints. NOTHING ARRIVES IN A BACKTEST — a historical run will "
					    + "fall to the '" + NewsUnknownRule + "' rule for every release.");
				}
			}
		}

		/// <summary>
		/// Attaches to NinjaTrader's live economic calendar, which is where the ACTUAL
		/// released figure comes from. The calendar FILE stays the schedule: NinjaTrader
		/// publishes a push per release and exposes no queryable list of upcoming ones,
		/// so neither source replaces the other.
		/// </summary>
		private void SubscribeToNt8Calendar()
		{
			_nt8Calendar = null;

			if (NewsActualSource == FpNewsActualSource.FileOnly)
			{
				Print("FPMR news: actual source is FileOnly — NinjaTrader's live economic calendar is not used. "
				    + "This is the correct setting for a BACKTEST.");
				return;
			}

			_nt8Calendar = new Nt8EconomicFeed();

			if (_nt8Calendar.Subscribe())
			{
				Print("FPMR news: subscribed to NinjaTrader's economic calendar for actual values. "
				    + "Releases arrive as they print; nothing arrives on historical bars, so a BACKTEST needs "
				    + "an Actual column in the file and the FileOnly setting.");
			}
			else
			{
				Log("FPMR: could not subscribe to NinjaTrader's economic calendar (" + _nt8Calendar.SubscribeError
				  + "). Actual values will come from the calendar file only.", LogLevel.Warning);
				_nt8Calendar = null;
			}
		}

		/// <summary>
		/// Detaches from the calendar. MUST run — EconomicEventUpdateReceived is a STATIC
		/// event, so a strategy that never unsubscribes leaks itself for the life of the
		/// platform process and keeps receiving pushes after it has been removed.
		/// </summary>
		private void UnsubscribeFromNt8Calendar()
		{
			if (_nt8Calendar == null)
				return;

			_nt8Calendar.Unsubscribe();
			_nt8Calendar = null;
		}

		/// <summary>
		/// Prints what each window resolves to in summer AND winter, so the shift is
		/// visible before a bar is processed rather than inferred from fills.
		/// </summary>
		private void PrintSessionPlan(TimeSpan summerShift)
		{
			if (!AutoAdjustForUsDst || summerShift == TimeSpan.Zero)
			{
				Print("FPMR sessions: DST auto-adjust OFF — windows are taken literally in "
				    + SessionTimeZoneId + " and never move.");
				return;
			}

			TimeZoneInfo typed  = TimeZoneRegistry.Resolve(SessionTimeZoneId);
			TimeZoneInfo anchor = TimeZoneRegistry.Resolve(DstAnchor.AnchorTimeZoneId);
			TimeSpan     extra  = DstAnchor.WinterExtra(typed, anchor);

			Print(string.Format(CultureInfo.InvariantCulture,
				"FPMR sessions: typed in {0} as SUMMER values, pinned to {1} so winter follows automatically.\n"
			  + "  Session 1 {2}: {3} summer / {4} winter  (= {5} {1})\n"
			  + "  Session 2 {6}: {7} summer / {8} winter  (= {9} {1})\n"
			  + "  Session 3 {10}: {11} summer / {12} winter  (= {13} {1})",
				SessionTimeZoneId, DstAnchor.AnchorTimeZoneId,
				Session1Enabled ? "ON " : "off", Session1Window, DstAnchor.ShiftWindow(Session1Window, extra),
				DstAnchor.ShiftWindow(Session1Window, -summerShift),
				Session2Enabled ? "ON " : "off", Session2Window, DstAnchor.ShiftWindow(Session2Window, extra),
				DstAnchor.ShiftWindow(Session2Window, -summerShift),
				Session3Enabled ? "ON " : "off", Session3Window, DstAnchor.ShiftWindow(Session3Window, extra),
				DstAnchor.ShiftWindow(Session3Window, -summerShift)));
		}

		private TimeZoneInfo ResolveBarTimeZone()
		{
			// NinjaTrader expresses EVERY bar timestamp in the global display time zone
			// (Tools > Options > General > Time zone), which falls back to the PC's zone
			// when left unset. Time[0] is already in that zone.
			//
			// This must NOT read Bars.TradingHours.TimeZoneInfo. That is the zone the
			// instrument's SESSION TEMPLATE is authored in — for CME index futures it is
			// Central Standard Time — and it says nothing about how bar timestamps are
			// expressed. Using it made the strategy relabel an already-local timestamp as
			// Central and then convert it again, shifting every session by the difference
			// between the two zones (Central -> IST is 10h30m, so evening windows fired on
			// morning bars). Overridable from the inputs for a non-standard setup.
			try
			{
				TimeZoneInfo display = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				if (display != null)
					return display;
			}
			catch { /* fall through */ }

			return TimeZoneInfo.Local;
		}

		/// <summary>Bar zone -> session zone offset right now, so a wrong zone is obvious in the log.</summary>
		private string DescribeShift()
		{
			if (_barTz == null || _sessionTz == null)
				return "?";

			DateTime now = DateTime.UtcNow;
			TimeSpan delta = _sessionTz.GetUtcOffset(now) - _barTz.GetUtcOffset(now);
			return (delta < TimeSpan.Zero ? "-" : "+") + delta.Duration().ToString(@"hh\:mm");
		}

		/// <summary>Reported for diagnosis only — it is deliberately NOT used to interpret bar times.</summary>
		private string TradingHoursTimeZoneName()
		{
			try
			{
				if (Bars != null && Bars.TradingHours != null && Bars.TradingHours.TimeZoneInfo != null)
					return Bars.TradingHours.TimeZoneInfo.Id;
			}
			catch { /* not fatal */ }

			return "?";
		}


		private static TimeSpan PeriodLength(BarsPeriod period)
		{
			if (period == null)
				return TimeSpan.Zero;

			switch (period.BarsPeriodType)
			{
				case BarsPeriodType.Minute: return TimeSpan.FromMinutes(period.Value);
				case BarsPeriodType.Second: return TimeSpan.FromSeconds(period.Value);
				default:                    return TimeSpan.Zero;
			}
		}

		private void Fail(string message)
		{
			_configError     = true;
			_configErrorText = message;
			Log("FPMR CONFIGURATION ERROR: " + message + " The strategy will not place orders.", LogLevel.Error);
		}

		private void ReconcileOnRealtimeTransition()
		{
			// Do not assume the historical fill record matches the live account.
			if (Position.MarketPosition != MarketPosition.Flat && CountOpenTrades() == 0)
			{
				_unreconciled = true;
				Log("FPMR: went real-time holding a " + Position.MarketPosition + " position of "
				  + Position.Quantity + " that this strategy has no record of. New entries are blocked until flat. "
				  + "Flatten manually or restart the strategy once flat.", LogLevel.Warning);
			}
			else if (CountOpenTrades() > 0 && Position.MarketPosition == MarketPosition.Flat)
			{
				Print("FPMR: historical run ended with open trades but the live account is flat. Dropping stale trade records.");
				DropAllOpenTrades("RESTART");
			}
		}
	}
}
