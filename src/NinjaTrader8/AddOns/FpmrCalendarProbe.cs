// =============================================================================
//  FPMR · AddOn · FpmrCalendarProbe
//
//  A diagnostic, not a trading component. It answers two questions that cannot be
//  answered from disk or by reflection alone:
//
//    1. Does NinjaTrader's economic calendar PUSH anything on this machine, with
//       this connection?
//    2. Can the calendar be PULLED on demand, rather than waited for?
//
//  WHY (2) MATTERS
//  A push only ever arrives for a release that prints while the strategy is
//  running. That is useless for a release that has already happened, and useless
//  for a VPS restart. But NinjaTrader.Tradovate.Adapter exposes a PUBLIC
//  RequestCalendarsEconomic() returning Task, reachable as:
//
//      Cbi.Connection.Connections -> connection.Adapter -> (Tradovate.Adapter)
//
//  Its results come back through the same Calendar.EconomicEventUpdateReceived
//  event, so if the pull works, every push below is the answer to a request we
//  made rather than an accident of timing.
//
//  THIS IS AN UNDOCUMENTED, INTERNAL API. It is reached by reflection so that
//  nothing here references NinjaTrader.Tradovate.dll directly, and every step is
//  guarded: a NinjaTrader update that renames or removes the method must degrade
//  to "no pull available", never throw into the platform.
//
//  SAFETY
//  AddOns are instantiated at startup with no chart and no user action, which is
//  what makes this safe to leave on an unattended VPS. It places no orders,
//  subscribes to no market data, and calls one read-only request method.
//
//  OUTPUT
//      Documents\NinjaTrader 8\FpmrCalendarProbe.log
// =============================================================================
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.AddOns
{
	public class FpmrCalendarProbe : AddOnBase
	{
		private static readonly object FileGate = new object();

		private EventHandler<NinjaTrader.Cbi.EconomicUpdateArgs> _handler;
		private bool     _subscribed;
		private int      _received;
		private string   _logPath;
		private Timer    _poll;
		private int      _pullAttempts;
		private bool     _pullDone;

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

				Write("PROBE STARTED - subscribed to Calendar.EconomicEventUpdateReceived.");

				// Poll for a connected adapter, then ask it for the calendar ONCE.
				_poll = new Timer(TryPull, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
			}
			catch (Exception ex)
			{
				_subscribed = false;
				Write("PROBE FAILED TO SUBSCRIBE - " + ex.GetType().Name + ": " + ex.Message);
			}
		}

		private void Stop()
		{
			if (_poll != null)
			{
				_poll.Dispose();
				_poll = null;
			}

			if (!_subscribed || _handler == null)
				return;

			try   { NinjaTrader.Cbi.Calendar.EconomicEventUpdateReceived -= _handler; }
			catch { /* nothing useful to do on the way out */ }

			_handler    = null;
			_subscribed = false;

			Write("PROBE STOPPED - " + _received + " release(s) received this session.");
		}

		/// <summary>
		/// Walks the live connections looking for an adapter that exposes
		/// RequestCalendarsEconomic, and calls it once. Everything is reflection and
		/// everything is guarded - this is an internal API and may simply not be there.
		/// </summary>
		private void TryPull(object _)
		{
			if (_pullDone)
				return;

			_pullAttempts++;

			// Give up quietly rather than polling a disconnected platform forever.
			if (_pullAttempts > 30)
			{
				_pullDone = true;
				Write("PULL GAVE UP - no adapter exposing RequestCalendarsEconomic appeared after "
				    + (_pullAttempts - 1) + " attempts (~10 minutes).");
				if (_poll != null) { _poll.Dispose(); _poll = null; }
				return;
			}

			try
			{
				ICollection connections = NinjaTrader.Cbi.Connection.Connections as ICollection;
				if (connections == null || connections.Count == 0)
					return;

				foreach (object c in connections)
				{
					if (c == null)
						continue;

					// Only bother with a connection that is actually up.
					PropertyInfo statusProp = c.GetType().GetProperty("Status");
					object status = statusProp == null ? null : statusProp.GetValue(c, null);
					if (status == null || status.ToString() != "Connected")
						continue;

					PropertyInfo adapterProp = c.GetType().GetProperty("Adapter");
					object adapter = adapterProp == null ? null : adapterProp.GetValue(c, null);
					if (adapter == null)
						continue;

					MethodInfo pull = adapter.GetType().GetMethod("RequestCalendarsEconomic",
						BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
						null, Type.EmptyTypes, null);

					if (pull == null)
					{
						Write("PULL UNAVAILABLE - connected adapter is " + adapter.GetType().FullName
						    + ", which has no RequestCalendarsEconomic method.");
						_pullDone = true;
						if (_poll != null) { _poll.Dispose(); _poll = null; }
						return;
					}

					Write("PULL CALLING RequestCalendarsEconomic() on " + adapter.GetType().FullName + " ...");
					pull.Invoke(adapter, null);
					Write("PULL CALL RETURNED without error. Any results arrive as PUSH lines below.");

					_pullDone = true;
					if (_poll != null) { _poll.Dispose(); _poll = null; }
					return;
				}
			}
			catch (Exception ex)
			{
				Exception real = ex is TargetInvocationException && ex.InnerException != null
					? ex.InnerException
					: ex;

				Write("PULL FAILED - " + real.GetType().Name + ": " + real.Message);
				_pullDone = true;
				if (_poll != null) { _poll.Dispose(); _poll = null; }
			}
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

			// Pushes arrive on a feed thread and the pull runs on a timer thread, so
			// appends are serialised.
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
