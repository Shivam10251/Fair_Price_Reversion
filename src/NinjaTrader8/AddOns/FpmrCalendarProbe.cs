// =============================================================================
//  FPMR · AddOn · FpmrCalendarProbe
//
//  A diagnostic, not a trading component. It answers one question that cannot be
//  answered from disk or by reflection:
//
//      does NinjaTrader's economic calendar actually PUSH anything on this
//      machine, with this connection?
//
//  NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived is a static event. It is
//  declared in NinjaTrader.Core.dll and, of the shipped vendor assemblies, only
//  NinjaTrader.Tradovate.dll references EconomicCalendarUpdate — so whether it
//  fires depends on the connected provider, and the only way to find out is to
//  listen.
//
//  WHY AN ADDON RATHER THAN AN INDICATOR OR A STRATEGY
//  AddOns are instantiated by NinjaTrader at startup, with no chart and no user
//  action. That makes this safe to leave running on an unattended VPS: it places
//  no orders, subscribes to no market data, and touches nothing but its own log.
//
//  OUTPUT
//      Documents\NinjaTrader 8\FpmrCalendarProbe.log
//  One line per push, plus a startup and shutdown line. If the file exists and
//  contains only the startup line after a release has passed, the calendar does
//  not feed this connection and the strategy must get actuals from the file.
// =============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Text;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
	public class FpmrCalendarProbe : AddOnBase
	{
		private static readonly object      FileGate = new object();
		private EventHandler<NinjaTrader.Cbi.EconomicUpdateArgs> _handler;
		private bool   _subscribed;
		private int    _received;
		private string _logPath;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name = "FpmrCalendarProbe";
			}
			else if (State == State.Configure)
			{
				Start();
			}
			else if (State == State.Terminated)
			{
				Stop();
			}
		}

		private void Start()
		{
			if (_subscribed)
				return;

			try
			{
				_logPath = Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
					"NinjaTrader 8", "FpmrCalendarProbe.log");

				_handler = OnEconomicUpdate;
				NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived += _handler;
				_subscribed = true;

				Write("PROBE STARTED - subscribed to Calendar.EconomicEventUpdateReceived. "
				    + "Every economic release NinjaTrader pushes will be logged below.");
			}
			catch (Exception ex)
			{
				_subscribed = false;
				Write("PROBE FAILED TO SUBSCRIBE - " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private void Stop()
		{
			if (!_subscribed || _handler == null)
				return;

			try   { NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived -= _handler; }
			catch { /* nothing useful to do on the way out */ }

			_handler    = null;
			_subscribed = false;

			Write("PROBE STOPPED - " + _received + " release(s) received this session.");
		}

		private void OnEconomicUpdate(object sender, NinjaTrader.Cbi.EconomicUpdateArgs e)
		{
			if (e == null)
				return;

			try
			{
				_received++;

				StringBuilder sb = new StringBuilder();
				sb.Append("PUSH #").Append(_received).Append("  ");
				sb.Append("country=").Append(e.Country ?? "-").Append("  ");
				sb.Append("event=").Append(e.EventName ?? "-").Append("  ");
				sb.Append("importance=").Append(e.Importance).Append("  ");
				sb.Append("actual=").Append(Num(e.Actual)).Append("  ");
				sb.Append("consensus=").Append(Num(e.Consensus)).Append("  ");
				sb.Append("prior=").Append(Num(e.Prior)).Append("  ");
				sb.Append("timestamp=").Append(e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));

				Write(sb.ToString());
			}
			catch (Exception ex)
			{
				Write("PUSH HANDLER ERROR - " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private static string Num(double v)
		{
			return double.IsNaN(v) ? "NaN" : v.ToString("0.####", CultureInfo.InvariantCulture);
		}

		private void Write(string line)
		{
			if (string.IsNullOrEmpty(_logPath))
				return;

			// The event arrives on a feed thread, so appends are serialised.
			try
			{
				lock (FileGate)
					File.AppendAllText(_logPath,
						DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
			}
			catch { /* a diagnostic must never take the platform down */ }
		}
	}
}
