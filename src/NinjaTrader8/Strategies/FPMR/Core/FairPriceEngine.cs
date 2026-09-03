// =============================================================================
//  FPMR · Core · FairPriceEngine
//
//  Runs on the REFERENCE timeframe (1 minute by default), not necessarily the
//  chart timeframe, exactly like the Pine request.security() version:
//
//     session bar 1 -> arm
//     session bar 2 -> FairPrice := source OF BAR 1, i.e. after it closed
//     session end   -> FairPrice := NaN
//
//  Fair Price is therefore never read from an unfinished candle.
//
//  NEWS OVERRIDE (new in the NT8 build)
//  When a qualifying release lands inside the lookback window before a session
//  opens, that session's Fair Price is the OPEN of the news candle and the
//  first-candle rule is never armed for it. The value goes live on the news
//  candle itself — before the session opens — because the AfterNewsCandle start
//  mode has to be able to trade from there. The session-open value is discarded,
//  not averaged and not used as a fallback.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class FairPriceEngine
	{
		public double   FairPrice        { get; private set; }
		public bool     IsNewsFairPrice  { get; private set; }
		public bool     ChangedThisBar   { get; private set; }
		/// <summary>Session the current news Fair Price belongs to, 0 when not news-sourced.</summary>
		public int      NewsSessionIndex { get; private set; }
		public DateTime NewsSessionOpenTz{ get; private set; }
		public NewsEvent NewsEventUsed   { get; private set; }
		/// <summary>What the news branch wants done about the current Fair Price.</summary>
		public FpNewsBias NewsBias        { get; private set; }

		private int      _currentSessionIndex;
		private bool     _armed;

		/// <summary>When true a news Fair Price is discarded at the session open, handing
		/// the session over to its own first-candle rule.</summary>
		private readonly bool _newsExpiresAtSessionOpen;

		public FairPriceEngine(bool newsExpiresAtSessionOpen)
		{
			_newsExpiresAtSessionOpen = newsExpiresAtSessionOpen;
			Reset();
		}

		public bool HasFairPrice { get { return !double.IsNaN(FairPrice); } }

		public void Reset()
		{
			FairPrice            = double.NaN;
			IsNewsFairPrice      = false;
			ChangedThisBar       = false;
			NewsSessionIndex     = 0;
			NewsSessionOpenTz    = default(DateTime);
			NewsEventUsed        = null;
			NewsBias             = FpNewsBias.Reversion;
			_currentSessionIndex = 0;
			_armed               = false;
		}

		/// <summary>
		/// Advances the engine by one REFERENCE bar.
		/// </summary>
		/// <param name="sessionIndex">Session index of this reference bar (0 = outside).</param>
		/// <param name="sessionOpenTz">Open instant of that session, ignored when sessionIndex is 0.</param>
		/// <param name="sourceOfPreviousBar">Configured Fair Price source of the PREVIOUS reference bar.</param>
		/// <param name="captureThisBar">A news capture made on this very bar, or null.</param>
		/// <param name="pendingForThisSession">Capture waiting for the session that just started, or null.</param>
		/// <param name="tzNow">This bar's open time in the session zone, used to expire a held news price.</param>
		public void OnReferenceBar(int sessionIndex, DateTime sessionOpenTz, double sourceOfPreviousBar,
		                           NewsFairPrice captureThisBar, NewsFairPrice pendingForThisSession, DateTime tzNow)
		{
			ChangedThisBar = false;
			double before  = FairPrice;

			// 1. A news candle just printed: Fair Price goes live immediately, even
			//    though the session it belongs to has not opened yet.
			if (captureThisBar != null)
			{
				FairPrice         = captureThisBar.FairPrice;
				IsNewsFairPrice   = true;
				NewsSessionIndex  = captureThisBar.SessionIndex;
				NewsSessionOpenTz = captureThisBar.SessionOpenTz;
				NewsEventUsed     = captureThisBar.Event;
				NewsBias          = captureThisBar.Bias;
				_armed            = false;
			}

			if (sessionIndex == 0)
			{
				_currentSessionIndex = 0;

				// Hold a news Fair Price across the gap between the news candle and
				// the session it belongs to; drop everything else at session end.
				bool holdingNews = IsNewsFairPrice && NewsSessionIndex != 0 && tzNow < NewsSessionOpenTz;
				if (!holdingNews)
				{
					FairPrice        = double.NaN;
					IsNewsFairPrice  = false;
					NewsSessionIndex = 0;
					NewsEventUsed    = null;
					NewsBias         = FpNewsBias.Reversion;
					_armed           = false;
				}
			}
			else if (sessionIndex != _currentSessionIndex)
			{
				_currentSessionIndex = sessionIndex;

				if (pendingForThisSession != null && !_newsExpiresAtSessionOpen)
				{
					// News override wins outright for the whole session: the first-candle
					// rule is never armed. Only taken when the news price does NOT expire
					// at the open.
					FairPrice         = pendingForThisSession.FairPrice;
					IsNewsFairPrice   = true;
					NewsSessionIndex  = pendingForThisSession.SessionIndex;
					NewsSessionOpenTz = pendingForThisSession.SessionOpenTz;
					NewsEventUsed     = pendingForThisSession.Event;
					NewsBias          = pendingForThisSession.Bias;
					_armed            = false;
				}
				else
				{
					FairPrice        = double.NaN;
					IsNewsFairPrice  = false;
					NewsSessionIndex = 0;
					NewsEventUsed    = null;
					NewsBias         = FpNewsBias.Reversion;
					_armed           = true;
				}
			}
			else if (_armed)
			{
				FairPrice       = sourceOfPreviousBar;
				IsNewsFairPrice = false;
				NewsBias        = FpNewsBias.Reversion;
				_armed          = false;
			}

			bool wasNaN = double.IsNaN(before);
			bool isNaN  = double.IsNaN(FairPrice);
			ChangedThisBar = !isNaN && (wasNaN || Math.Abs(FairPrice - before) > double.Epsilon);
		}

		/// <summary>Selects the configured source price from a bar's OHLC.</summary>
		public static double SelectSource(FpSource source, double open, double high, double low, double close)
		{
			switch (source)
			{
				case FpSource.Open: return open;
				case FpSource.HL2:  return (high + low) / 2.0;
				case FpSource.HLC3: return (high + low + close) / 3.0;
				default:            return close;
			}
		}
	}
}
