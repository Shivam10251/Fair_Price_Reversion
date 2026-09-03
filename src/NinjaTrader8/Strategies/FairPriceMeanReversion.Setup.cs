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

			if (UseFixedTpSl)
			{
				if (FixedStopLossPoints <= 0.0)
					Fail("Fixed stop loss (points) must be greater than zero when Fixed TP/SL is on.");
				else if (FixedTakeProfitPoints <= 0.0)
					Fail("Fixed take profit (points) must be greater than zero when Fixed TP/SL is on.");
			}

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

			_news = new NewsFairPriceResolver(_newsLoad.Events, _sessions, NewsImpactFilter,
			                                  NewsCurrencyFilter, NewsMultipleEventRule, NewsLookbackHours,
			                                  UseNewsSurprise ? NewsExpectedTolerancePercent   : double.MaxValue,
			                                  UseNewsSurprise ? NewsUnexpectedThresholdPercent : double.MaxValue,
			                                  UseNewsSurprise ? NewsUnknownRule : FpNewsUnknownRule.TreatAsExpected,
			                                  UseNewsSurprise && NewsAllowContinuation,
			                                  detector);

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
				Log("FPMR: surprise classification is ON but NO event in the calendar has both a forecast and an "
				  + "actual value. Every release will fall to the '" + NewsUnknownRule + "' rule. Check that the "
				  + "file has 'forecast' and 'actual' columns.", LogLevel.Warning);
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
