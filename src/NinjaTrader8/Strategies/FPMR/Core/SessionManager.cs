// =============================================================================
//  FPMR · Core · SessionManager
//
//  Owns the three windows, the session time zone, and the bar-to-bar transition
//  detection (session start / session end / new calendar day).
//
//  TIME MODEL — this is the part that must not be got wrong.
//  * NinjaTrader stamps a bar with its CLOSE time. TradingView evaluates a
//    session against the bar's OPEN time. Every membership test below therefore
//    takes the bar OPEN, which the caller derives as (barCloseTime - barLength).
//  * The comparison happens in the user's session zone, never in machine local
//    time and never against the instrument's session template.
// =============================================================================
using System;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class SessionEvaluation
	{
		public int      Index;        // 1..3, or 0 when outside every enabled window
		public bool     InSession;
		public bool     IsSessionStart;
		public bool     IsSessionEnd;
		public bool     IsNewDay;
		public DateTime TzBarOpen;
		public DateTime SessionOpenTz; // open instant of the active session, default when flat
	}

	public sealed class SessionManager
	{
		private readonly SessionWindow[] _windows;
		private readonly TimeZoneInfo    _sessionTz;

		private int  _prevIndex   = 0;
		private int  _prevDay     = int.MinValue;
		private bool _seenAnyBar  = false;

		public SessionManager(TimeZoneInfo sessionTz, SessionWindow s1, SessionWindow s2, SessionWindow s3)
		{
			_sessionTz = sessionTz;
			_windows   = new[] { s1, s2, s3 };
		}

		public TimeZoneInfo SessionTimeZone { get { return _sessionTz; } }
		public SessionWindow[] Windows      { get { return _windows; } }

		/// <summary>Any window that failed to parse, so the strategy can refuse to run.</summary>
		public string FirstConfigError()
		{
			foreach (SessionWindow w in _windows)
				if (w.Enabled && !w.IsValid)
					return w.ParseError;
			return null;
		}

		/// <summary>Overlapping windows resolve to the lowest-numbered enabled session (Pine behaviour).</summary>
		public int IndexAt(DateTime tzBarOpen)
		{
			for (int i = 0; i < _windows.Length; i++)
				if (_windows[i].Contains(tzBarOpen))
					return _windows[i].Index;
			return 0;
		}

		/// <summary>
		/// Advances the transition state machine by one bar.
		/// <paramref name="barOpenInSourceZone"/> is the bar's open timestamp as NinjaTrader
		/// reports it; it is converted here using <paramref name="sourceZone"/>.
		/// </summary>
		public SessionEvaluation Advance(DateTime barOpenInSourceZone, TimeZoneInfo sourceZone)
		{
			DateTime tz = TimeZoneRegistry.Convert(barOpenInSourceZone, sourceZone, _sessionTz);

			int idx = IndexAt(tz);
			int dayKey = tz.Year * 10000 + tz.Month * 100 + tz.Day;

			SessionEvaluation ev = new SessionEvaluation
			{
				Index          = idx,
				InSession      = idx != 0,
				TzBarOpen      = tz,
				IsNewDay       = _seenAnyBar && dayKey != _prevDay,
				IsSessionStart = idx != 0 && idx != _prevIndex,
				IsSessionEnd   = idx == 0 && _prevIndex != 0
			};

			ev.SessionOpenTz = idx == 0 ? default(DateTime) : _windows[idx - 1].PreviousOpen(tz);

			_prevIndex  = idx;
			_prevDay    = dayKey;
			_seenAnyBar = true;

			return ev;
		}

		/// <summary>
		/// The next open of any ENABLED window strictly after <paramref name="tzFrom"/> minus
		/// nothing — i.e. at or after it. Returns false when no window is enabled.
		/// The session index is returned so the news override can be scoped to one session.
		/// </summary>
		public bool TryGetNextSessionOpen(DateTime tzFrom, out DateTime openTz, out int sessionIndex)
		{
			openTz       = DateTime.MaxValue;
			sessionIndex = 0;

			foreach (SessionWindow w in _windows)
			{
				if (!w.IsActive)
					continue;

				DateTime candidate = w.NextOpen(tzFrom);
				if (candidate < openTz)
				{
					openTz       = candidate;
					sessionIndex = w.Index;
				}
			}

			return sessionIndex != 0;
		}

		/// <summary>Resets the transition state — used when the strategy re-enters Historical processing.</summary>
		public void Reset()
		{
			_prevIndex  = 0;
			_prevDay    = int.MinValue;
			_seenAnyBar = false;
		}
	}
}
