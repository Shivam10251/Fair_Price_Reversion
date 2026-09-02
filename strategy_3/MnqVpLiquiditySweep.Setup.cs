// =============================================================================
//  MNQ VOLUME PROFILE LIQUIDITY SWEEP  —  NinjaTrader 8
//  Setup — module construction and configuration validation.
//
//  Every failure here is loud and disables trading rather than letting the
//  strategy run against levels it could not build correctly. A wrong time zone
//  or an unparseable session would otherwise place the profile on the wrong
//  bars and produce plausible-looking but meaningless levels.
// =============================================================================
#region Using declarations
using System;
using System.Globalization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript.Strategies.VPS;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class MnqVpLiquiditySweep : Strategy
	{
		private void BuildModules()
		{
			_configError     = false;
			_configErrorText = string.Empty;

			_tickSize   = Instrument.MasterInstrument.TickSize;
			_pointValue = Instrument.MasterInstrument.PointValue;
			_tickFormat = TickFormat(_tickSize);

			_barTz     = ResolveBarTimeZone();
			_barLength = PeriodLength(BarsPeriod);

			// The Pine indicator builds its profile from the chart's own bars, so the
			// primary series IS the profile's resolution. Both supported timeframes
			// are legitimate; they simply produce different profiles, and 5 Minute is
			// the coarser of the two. The timeframe is taken from the Data Series
			// rather than from a parameter of its own — one setting, so the strategy
			// and the chart can never disagree about which bars built the levels.
			if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute
			    || (BarsPeriod.Value != 1 && BarsPeriod.Value != 5))
				Fail(string.Format(CultureInfo.InvariantCulture,
					"This strategy requires a 1 Minute or 5 Minute primary data series. The current series is {0} {1}. "
				  + "Set it in the Data Series window; the volume profile is built from these bars.",
					BarsPeriod.Value, BarsPeriod.BarsPeriodType));

			TimeZoneInfo anchor = VpsTimeZone.Resolve(VpsDst.AnchorTimeZoneId);
			if (AutoAdjustForUsDst && anchor == null)
				Fail("The DST anchor zone '" + VpsDst.AnchorTimeZoneId + "' could not be resolved on this machine.");

			VpsSession profileTyped = new VpsSession("Profile", ProfileSession, SessionTimeZone);
			if (!profileTyped.IsValid)
				Fail(profileTyped.ParseError);

			VpsSession rangeTyped = new VpsSession("Range", RangeSession, SessionTimeZone);
			if (!rangeTyped.IsValid)
				Fail(rangeTyped.ParseError);

			// Rebase onto the anchor zone so the winter shift is automatic. See VpsDst.
			_profileWindow = AutoAdjustForUsDst ? profileTyped.ToAnchor(anchor) : profileTyped;
			_rangeWindow   = AutoAdjustForUsDst ? rangeTyped.ToAnchor(anchor)   : rangeTyped;

			// The entry cutoff is a single clock time, so it is carried as a window
			// running from midnight up to it and rebased the same way.
			_entryCutoff = null;
			if (!string.IsNullOrWhiteSpace(EntryCutoff))
			{
				VpsSession cutoffTyped = new VpsSession("Entry cutoff", "0000-" + EntryCutoff.Trim(), SessionTimeZone);
				if (!cutoffTyped.IsValid)
					Fail("'No entries after' must be a HHMM clock time such as 1500. " + cutoffTyped.ParseError);
				else
					_entryCutoff = AutoAdjustForUsDst ? cutoffTyped.ToAnchor(anchor) : cutoffTyped;
			}

			if (!SweepValueArea && !SweepRange)
				Fail("Both 'Sweep VAH / VAL' and 'Sweep RH / RL' are off, so no level can ever originate a trade.");

			if (!EnableLongs && !EnableShorts)
				Fail("Both longs and shorts are disabled.");

			if (SizingMode == VpsSizingMode.RiskBased && RiskPerTradeUSD <= 0)
				Fail("Risk per trade ($) must be greater than zero in risk-based mode.");

			_profile = new VolumeProfileEngine(ProfileBins, ValueAreaPercent);
			_sweeps  = new VpsSweepTracker(SweepConfirmWindowBars, LevelProximityTicks * _tickSize);

			_profileOccurrence = DateTime.MinValue;
			_rangeOccurrence   = DateTime.MinValue;
			_freezeOccurrence  = DateTime.MinValue;
			_freezeTime        = DateTime.MinValue;
			_freezes           = 0;
			_inProfile         = false;
			_inRange           = false;
			_rangeHigh         = 0;
			_rangeLow          = 0;
			_tradingDay        = DateTime.MinValue;
			_tradesDay         = 0;
			_tradeSeq          = 0;

			ResetTrade();

			PrintSessionPlan();

			Print(string.Format(CultureInfo.InvariantCulture,
				"MnqVpLiquiditySweep loaded: {0} | {1} {2} series | tick {3} | point value ${4} | bar tz {5}{6}",
				Instrument.FullName, BarsPeriod.Value, BarsPeriod.BarsPeriodType,
				_tickSize, _pointValue, _barTz == null ? "?" : _barTz.Id,
				_configError ? "\n  CONFIG ERROR: " + _configErrorText : string.Empty));
		}

		/// <summary>
		/// Prints what each window resolves to in summer AND winter, so the shift is
		/// visible before a single bar is processed rather than inferred from fills.
		/// </summary>
		private void PrintSessionPlan()
		{
			if (!AutoAdjustForUsDst)
			{
				Print(string.Format(CultureInfo.InvariantCulture,
					"  DST auto-adjust is OFF. Windows are taken literally in {0} and never move:\n"
				  + "    Profile {1} | Range {2} | No entries after {3}",
					SessionTimeZone, ProfileSession, RangeSession,
					string.IsNullOrWhiteSpace(EntryCutoff) ? "-" : EntryCutoff));
				return;
			}

			Print(string.Format(CultureInfo.InvariantCulture,
				"  Times typed in {0} as SUMMER (US-DST) values, pinned to {1} so winter follows automatically.\n"
			  + "    Profile : {2} summer / {3} winter  (= {4} {5})  {6} rows, {7}% value area\n"
			  + "    Range   : {8} summer / {9} winter  (= {10} {11})  entries open when it closes\n"
			  + "    Cutoff  : {12} summer / {13} winter",
				SessionTimeZone, VpsDst.AnchorTimeZoneId,
				ProfileSession, WinterLabel(ProfileSession), _profileWindow.WindowLabel, VpsDst.AnchorTimeZoneId,
				ProfileBins, ValueAreaPercent,
				RangeSession, WinterLabel(RangeSession), _rangeWindow.WindowLabel, VpsDst.AnchorTimeZoneId,
				string.IsNullOrWhiteSpace(EntryCutoff) ? "-" : EntryCutoff,
				string.IsNullOrWhiteSpace(EntryCutoff) ? "-" : WinterLabel(EntryCutoff)));
		}

		/// <summary>
		/// The same typed window an hour later, which is what winter looks like when
		/// the user's zone has no DST of its own and the anchor's does.
		/// </summary>
		private string WinterLabel(string typedWindow)
		{
			TimeZoneInfo user   = VpsTimeZone.Resolve(SessionTimeZone);
			TimeZoneInfo anchor = VpsTimeZone.Resolve(VpsDst.AnchorTimeZoneId);
			if (user == null || anchor == null)
				return typedWindow;

			// Winter shift = how much the anchor's standard offset differs from its
			// summer offset, expressed in the user's clock. Normally exactly one hour.
			DateTimeOffset winterRef = new DateTimeOffset(new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc));
			DateTimeOffset summerRef = new DateTimeOffset(new DateTime(2025, 7, 15, 12, 0, 0, DateTimeKind.Utc));
			TimeSpan delta = (user.GetUtcOffset(winterRef) - anchor.GetUtcOffset(winterRef))
			               - (user.GetUtcOffset(summerRef) - anchor.GetUtcOffset(summerRef));

			string[] parts = typedWindow.Trim().Split('-');
			for (int i = 0; i < parts.Length; i++)
			{
				VpsSession one = new VpsSession("x", "0000-" + parts[i].Trim(), SessionTimeZone);
				if (one.IsValid)
					parts[i] = VpsDst.Clock(VpsDst.Normalise(one.End + delta));
			}

			return string.Join("-", parts);
		}

		/// <summary>
		/// NinjaTrader expresses every bar timestamp in the global display time zone
		/// (Tools > Options > General), falling back to the PC's zone when unset.
		/// This deliberately does NOT read Bars.TradingHours.TimeZoneInfo: that is the
		/// zone the instrument's SESSION TEMPLATE is authored in and says nothing about
		/// how bar timestamps are expressed.
		/// </summary>
		private TimeZoneInfo ResolveBarTimeZone()
		{
			try
			{
				TimeZoneInfo display = NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo;
				if (display != null)
					return display;
			}
			catch { /* fall through */ }

			return TimeZoneInfo.Local;
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

		private static string TickFormat(double tickSize)
		{
			if (tickSize >= 1.0)    return "0.##";
			if (tickSize >= 0.01)   return "0.##";
			if (tickSize >= 0.0001) return "0.#####";
			return "0.########";
		}

		private void Fail(string message)
		{
			_configError     = true;
			_configErrorText = message;
			Log("MnqVpLiquiditySweep CONFIGURATION ERROR: " + message + " The strategy will not place orders.",
				LogLevel.Error);
			Print("[CONFIG ERROR] " + message);
		}
	}
}
