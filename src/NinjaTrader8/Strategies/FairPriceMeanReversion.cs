// =============================================================================
//  FAIR PRICE MEAN REVERSION · BOS / CHoCH / DISPLACEMENT   —  NinjaTrader 8
//  Part 2 of 3 — lifecycle and per-bar orchestration.
//
//  THERE IS NO "FIRST CANDLE COLOUR" ENTRY IN THIS STRATEGY.
//  The first candle of a session establishes Fair Price and nothing else.
//
//  One engine only:
//      Fair Price -> regime (above / below / inside zone)
//      -> market-structure state machine (HH/HL/LH/LL)
//      -> break of the single ACTIVE level = CHoCH or BOS
//      -> the breaking candle IS the displacement candle = entry candle
//      -> SL = displacement candle extreme, TP = RR multiple (or Fair Price)
//      -> position size chosen so the dollar risk lands near the target
//
//  Nothing is hardcoded to MNQ: tick size and point value come from
//  Instrument.MasterInstrument at run time.
//
//  See docs/DESIGN_DECISIONS.md for every place NinjaTrader forced a deviation
//  from the Pine behaviour. The two that change results most:
//    1. Entries fill at the NEXT bar's open, not at the displacement candle's
//       close. NinjaTrader has no equivalent of process_orders_on_close.
//    2. Same-bar TP/SL is resolved from real ticks via OrderFillResolution.High.
// =============================================================================
#region Using declarations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies.FPMR;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class FairPriceMeanReversion : Strategy
	{
		// ── Modules ───────────────────────────────────────────────────────────────
		private SessionManager        _sessions;
		private StructureEngine       _structure;
		private FairPriceEngine       _fair;
		private ExtendedTpEngine      _xtp;
		private SessionVwap           _vwap;
		private VisualEngine          _viz;
		private NewsFairPriceResolver _news;
		private NewsLoadResult        _newsLoad;

		private EMA _emaFast;
		private EMA _emaSlow;

		// ── Resolved configuration ────────────────────────────────────────────────
		private TimeZoneInfo _sessionTz;
		private TimeZoneInfo _barTz;
		private TimeZoneInfo _newsTz;

		private int      _fpSeriesIndex = -1;
		private TimeSpan _primaryBarLength;
		private TimeSpan _fpBarLength;

		private double _tickSize   = 0.01;
		private double _pointValue = 1.0;
		private double _zoneOffset;
		private double _xtpOffset;

		private bool   _configError;
		private string _configErrorText = string.Empty;

		// ── Running state ─────────────────────────────────────────────────────────
		private int  _fpCursor;
		private int  _prevEffSession;
		private int  _tradingStartBar = -1;
		private bool _hadFairPrev;
		private int  _tradeSeq;
		private int  _tradesDay;
		private int  _tradesSession;
		private int  _ambiguousCount;
		private int  _uid;
		private int  _fairSeq;
		private int  _sessionSeq;
		private int  _fairBarIndex = -1;
		private bool _unreconciled;
		private string _lastExitText = "-";
		private string _lastRejectText = "-";

		private Func<int, double> _highAt;
		private Func<int, double> _lowAt;

		// ── Cached per-bar values used by the trading partial ─────────────────────
		private double _fairPrice, _zoneUpper, _zoneLower;
		private bool   _hasFair, _insideZone;
		private int    _posState;
		private bool   _inTradingWindow;
		private bool   _warmupDone;
		private int    _effSession;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description = "Fair Price mean reversion using market structure. CHoCH/BOS displacement entries, "
				            + "dollar-targeted position sizing and an optional news-driven Fair Price.";
				Name        = "FairPriceMeanReversion";

				Calculate                   = Calculate.OnBarClose;
				EntriesPerDirection         = 1;
				EntryHandling               = EntryHandling.AllEntries;
				IsExitOnSessionCloseStrategy= false;
				ExitOnSessionCloseSeconds   = 30;
				IsFillLimitOnTouch          = false;
				MaximumBarsLookBack         = MaximumBarsLookBack.Infinite;
				OrderFillResolution         = OrderFillResolution.Standard;
				Slippage                    = 0;
				StartBehavior               = StartBehavior.WaitUntilFlat;
				TimeInForce                 = TimeInForce.Gtc;
				TraceOrders                 = false;
				RealtimeErrorHandling       = RealtimeErrorHandling.StopCancelClose;
				StopTargetHandling          = StopTargetHandling.PerEntryExecution;
				BarsRequiredToTrade         = 20;
				IsInstantiatedOnEachOptimizationIteration = true;

				ApplyDefaults();
			}
			else if (State == State.Configure)
			{
				ConfigureSeriesAndOrders();
			}
			else if (State == State.DataLoaded)
			{
				BuildModules();
			}
			else if (State == State.Realtime)
			{
				ReconcileOnRealtimeTransition();
			}
			else if (State == State.Terminated)
			{
				if (_viz != null)
					_viz.RemoveAll();
			}
		}

		// ── Per bar ───────────────────────────────────────────────────────────────
		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;   // the Fair Price series is pumped from the primary bar, see below

			if (_configError || _sessions == null)
				return;

			int required = Math.Max(BarsRequiredToTrade, PivotDetector.RequiredBars(PivotLeftBars, PivotRightBars));
			if (CurrentBar < required)
				return;

			_viz.CurrentBarIndex = CurrentBar;

			DateTime barOpen = PrimaryBarOpenTime();
			SessionEvaluation ev = _sessions.Advance(barOpen, _barTz);

			PumpFairPriceSeries();

			_fairPrice  = _fair.FairPrice;
			_hasFair    = _fair.HasFairPrice;
			_zoneUpper  = _hasFair ? _fairPrice + _zoneOffset : double.NaN;
			_zoneLower  = _hasFair ? _fairPrice - _zoneOffset : double.NaN;

			_posState   = !_hasFair ? 0 : Close[0] > _zoneUpper ? 1 : Close[0] < _zoneLower ? -1 : 0;
			_insideZone = _hasFair && Close[0] <= _zoneUpper && Close[0] >= _zoneLower;

			ResolveTradingWindow(ev);

			bool sessionStart = _effSession != 0 && _effSession != _prevEffSession;
			bool sessionEnd   = _effSession == 0 && _prevEffSession != 0;
			_prevEffSession   = _effSession;

			if (ev.IsNewDay)
				_tradesDay = 0;

			if (sessionStart)
			{
				_tradesSession   = 0;
				_tradingStartBar = CurrentBar;
				_xtp.OnSessionStart();
				_viz.BeginSession(++_sessionSeq, CurrentBar);
			}

			// The band stops on the last bar that was still inside the window, so it never
			// bleeds past the session close.
			if (sessionEnd)
				_viz.EndSession(CurrentBar - 1);
			else if (_effSession != 0)
				_viz.ExtendSession(CurrentBar);

			_warmupDone = _tradingStartBar >= 0 && (CurrentBar - _tradingStartBar) >= MinBarsBeforeFirstTrade;

			_vwap.OnBar(High[0], Low[0], Close[0], Volume[0], sessionStart || ev.IsNewDay);

			// ── Structure, in the Pine call order ─────────────────────────────────
			bool fairPriceLost = !_hasFair && _hadFairPrev;
			_structure.BeginBar(_posState, sessionStart, fairPriceLost);
			_hadFairPrev = _hasFair;

			DetectAndFeedPivots();
			_structure.ExpireSetups(CurrentBar, SetupValidityBars);

			double breakUp   = BreakConfirmation == FpBreakConfirm.Close ? Close[0] : High[0];
			double breakDown = BreakConfirmation == FpBreakConfirm.Close ? Close[0] : Low[0];
			StructureBreak brk = _structure.DetectBreak(breakUp, breakDown, CurrentBar);

			_xtp.OnBar(Close[0], _fairPrice, _hasFair);

			DrawFairPriceIfNeeded();

			if (brk.Occurred)
			{
				_viz.DrawBreak(brk.Direction, brk.Event, High[0], Low[0], ++_uid);
				_viz.DrawBrokenLevel(brk.Direction, brk.BrokenLevel,
					brk.BrokenBarIndex >= 0 ? CurrentBar - brk.BrokenBarIndex : -1, _uid);
				EvaluateEntry(brk);
			}

			MaintainOpenTrades(sessionEnd);
			DrawStatePanel(ev);
		}

		private void ResolveTradingWindow(SessionEvaluation ev)
		{
			_effSession      = ev.Index;
			_inTradingWindow = ev.InSession;

			// AfterNewsCandle lets a session's trading begin from the news candle,
			// before the session window itself opens. The news candle then acts as
			// that session's logical start, so structure is not reset a second time
			// when the clock reaches the session open.
			if (UseNewsFairPrice
			    && NewsTradingStart == FpNewsTradingStart.AfterNewsCandle
			    && ev.Index == 0
			    && _fair.IsNewsFairPrice
			    && _fair.NewsSessionIndex != 0
			    && ev.TzBarOpen < _fair.NewsSessionOpenTz)
			{
				_effSession      = _fair.NewsSessionIndex;
				_inTradingWindow = true;
			}
		}

		private DateTime PrimaryBarOpenTime()
		{
			return _primaryBarLength == TimeSpan.Zero ? Time[0] : Time[0] - _primaryBarLength;
		}

		/// <summary>
		/// Advances the Fair Price engine over every reference bar that has closed at or
		/// before this primary bar. Driven from the primary bar rather than from
		/// BarsInProgress so the result never depends on NinjaTrader's multi-series
		/// call ordering.
		/// </summary>
		private void PumpFairPriceSeries()
		{
			int idx = _fpSeriesIndex;
			if (idx < 0 || CurrentBars[idx] < 0)
				return;

			// CurrentBars[] is the INDEX of the newest bar, so bar n sits barsAgo
			// (CurrentBars[idx] - n) back. The cursor walks forward, oldest first,
			// and stops at any reference bar that closes after this primary bar.
			while (_fpCursor <= CurrentBars[idx])
			{
				int barsAgo = CurrentBars[idx] - _fpCursor;

				if (Times[idx][barsAgo] > Time[0])
					break;

				ProcessReferenceBar(idx, barsAgo);
				_fpCursor++;
			}
		}

		private void ProcessReferenceBar(int idx, int barsAgo)
		{
			DateTime refOpen = Times[idx][barsAgo] - _fpBarLength;
			DateTime tz      = TimeZoneRegistry.Convert(refOpen, _barTz, _sessionTz);

			int sessionIndex = _sessions.IndexAt(tz);
			DateTime sessionOpenTz = sessionIndex == 0
				? default(DateTime)
				: _sessions.Windows[sessionIndex - 1].PreviousOpen(tz);

			double sourcePrev = double.NaN;
			if (barsAgo + 1 <= CurrentBars[idx])
				sourcePrev = FairPriceEngine.SelectSource(FairPriceSource,
					Opens[idx][barsAgo + 1], Highs[idx][barsAgo + 1], Lows[idx][barsAgo + 1], Closes[idx][barsAgo + 1]);

			NewsFairPrice capture = null;
			NewsFairPrice pending = null;

			if (_news != null)
			{
				capture = _news.OnReferenceBar(tz, _fpBarLength, Opens[idx][barsAgo]);
				if (sessionIndex != 0)
					pending = _news.PendingFor(sessionIndex, sessionOpenTz);
			}

			_fair.OnReferenceBar(sessionIndex, sessionOpenTz, sourcePrev, capture, pending, tz);

			if (_fair.ChangedThisBar && VerboseLogging)
				Print(string.Format(CultureInfo.InvariantCulture, "{0}  FAIR PRICE {1:0.#####}{2}",
					tz.ToString("yyyy-MM-dd HH:mm"), _fair.FairPrice,
					_fair.IsNewsFairPrice && _fair.NewsEventUsed != null
						? "  [news: " + _fair.NewsEventUsed + "]"
						: string.Empty));
		}

		private void DetectAndFeedPivots()
		{
			PivotResult ph = PivotDetector.PivotHigh(_highAt, PivotLeftBars, PivotRightBars, CurrentBar + 1);
			if (ph.Found)
			{
				PivotAccepted a = _structure.OnPivotHigh(ph.Price, CurrentBar - ph.BarsAgo, CurrentBar);
				if (_inTradingWindow)
					_viz.DrawSwing(a.Role, ph.BarsAgo, ph.Price, ++_uid);
			}

			PivotResult pl = PivotDetector.PivotLow(_lowAt, PivotLeftBars, PivotRightBars, CurrentBar + 1);
			if (pl.Found)
			{
				PivotAccepted a = _structure.OnPivotLow(pl.Price, CurrentBar - pl.BarsAgo, CurrentBar);
				if (_inTradingWindow)
					_viz.DrawSwing(a.Role, pl.BarsAgo, pl.Price, ++_uid);
			}
		}

		private void DrawFairPriceIfNeeded()
		{
			if (_fair.ChangedThisBar && _hasFair)
			{
				_fairSeq++;
				_fairBarIndex = CurrentBar;
				_viz.BeginFairPrice(_fairSeq, _fairPrice, _zoneUpper, _zoneLower, _fair.IsNewsFairPrice);
			}

			if (_hasFair && _fairBarIndex >= 0 && _inTradingWindow)
				_viz.ExtendFairPrice(_fairSeq, CurrentBar - _fairBarIndex);
		}
	}
}
