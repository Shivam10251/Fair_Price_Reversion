// =============================================================================
//  FPMR · Rendering · VisualEngine
//
//  All chart drawing lives here so the trading logic never touches the UI.
//
//  RULES OBSERVED
//  * Default shows the things the strategy actually trades on: entry/exit markers,
//    the Fair Price line and zone, the per-trade SL/TP zones and the session
//    shading. ShowFullVisuals adds only the DIAGNOSTIC layer on top — swing
//    labels, CHoCH/BOS/DISP marks and the dotted broken-level line.
//  * Session shading uses RegionHighlightX, which spans the full chart height, so
//    it needs no price bounds and never fights autoscale.
//  * Unique tags everywhere; nothing is redrawn every bar except the OPEN trade's
//    boxes and the live Fair Price line, which have to move.
//  * A trade's boxes stop extending exactly on the exit bar and are never touched
//    again afterwards.
//  * Every tag is tracked and removed on State.Terminated.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class VisualEngine
	{
		private readonly NinjaScriptBase _owner;
		private readonly Action<string>  _remove;

		private readonly List<string>       _allTags     = new List<string>();
		private readonly List<List<string>> _fairGroups  = new List<List<string>>();
		private readonly List<List<string>> _tradeGroups   = new List<List<string>>();
		private readonly List<List<string>> _sessionGroups = new List<List<string>>();

		public bool ShowFairPriceLine  = true;
		public bool ShowFairPriceZone  = true;
		public bool ShowFullVisuals    = false;
		public bool ShowRejections     = false;
		public bool ShowStatePanel     = false;
		public bool ShowTradeZones     = true;
		public bool ShowSessionShading = true;

		public int FairPriceHistory = 5;
		public int TradeHistory     = 30;
		public int SessionHistory   = 20;
		public int ForwardExtend    = 5;

		/// <summary>
		/// Rolling cap on the one-off structure marks (swings, CHoCH/BOS, broken levels,
		/// rejection labels). They are not tied to a session or a trade, so without a cap
		/// a multi-year backtest with full visuals on would accumulate them forever.
		/// </summary>
		public int TransientDrawingCap = 1000;

		public Brush LongBrush  = Brushes.MediumSeaGreen;
		public Brush ShortBrush = Brushes.IndianRed;
		public Brush FairBrush  = Brushes.Goldenrod;
		public Brush MutedBrush = Brushes.Gray;

		/// <summary>SL/TP zones get their own brushes so they stay red/green regardless of trade direction.</summary>
		public Brush StopBrush    = Brushes.IndianRed;
		public Brush TargetBrush  = Brushes.MediumSeaGreen;

		public Brush SessionBrush = Brushes.LightBlue;
		/// <summary>Kept low by default — the shading must never compete with the candles.</summary>
		public int   SessionOpacity = 12;

		public VisualEngine(NinjaScriptBase owner, Action<string> remover)
		{
			_owner  = owner;
			_remove = remover;
		}

		// ── Fair Price ────────────────────────────────────────────────────────────
		private List<string> _currentFairGroup;
		private double       _fairValue, _fairUpper, _fairLower;

		/// <summary>Starts a new Fair Price drawing group on the bar the value appeared.</summary>
		public void BeginFairPrice(int sequence, double fairPrice, double upper, double lower, bool isNews)
		{
			_currentFairGroup       = new List<string>();
			_fairValue              = fairPrice;
			_fairUpper              = upper;
			_fairLower              = lower;

			_fairGroups.Add(_currentFairGroup);
			PruneGroups(_fairGroups, FairPriceHistory);

			string label = isNews ? "FAIR PRICE (news)" : "FAIR PRICE";
			string tag   = Tag(_currentFairGroup, "FPTXT" + sequence);
			Draw.Text(_owner, tag, label, 0, fairPrice);
		}

		/// <summary>Extends the live Fair Price line/zone by one bar. Cheap: two objects, same tags.</summary>
		public void ExtendFairPrice(int sequence, int barsSinceFairPrice)
		{
			if (_currentFairGroup == null)
				return;

			int start = barsSinceFairPrice;
			int end   = -Math.Max(0, ForwardExtend);

			if (ShowFairPriceLine)
			{
				string tag = Tag(_currentFairGroup, "FPLN" + sequence);
				Draw.Line(_owner, tag, false, start, _fairValue, end, _fairValue, FairBrush, DashStyleHelper.Solid, 2);
			}

			if (ShowFairPriceZone && _fairUpper > _fairLower)
			{
				string tag = Tag(_currentFairGroup, "FPZN" + sequence);
				Draw.Rectangle(_owner, tag, false, start, _fairUpper, end, _fairLower, FairBrush, FairBrush, 8);
			}
		}

		// ── Session shading ───────────────────────────────────────────────────────
		private List<string> _currentSessionGroup;
		private int          _sessionStartBar = -1;
		private int          _sessionSeq;

		/// <summary>Opens a shaded band on the bar the session became active.</summary>
		public void BeginSession(int sequence, int startBarIndex)
		{
			if (!ShowSessionShading)
				return;

			_currentSessionGroup = new List<string>();
			_sessionGroups.Add(_currentSessionGroup);
			PruneGroups(_sessionGroups, SessionHistory);

			_sessionSeq      = sequence;
			_sessionStartBar = startBarIndex;

			ExtendSession(CurrentBarIndex);
		}

		/// <summary>
		/// Redraws the live band out to <paramref name="rightEdgeBarIndex"/>. Same tag every
		/// bar, so this replaces one object rather than accumulating them.
		/// </summary>
		public void ExtendSession(int rightEdgeBarIndex)
		{
			if (!ShowSessionShading || _currentSessionGroup == null || _sessionStartBar < 0)
				return;

			int start = CurrentBarIndex - _sessionStartBar;   // older edge, larger barsAgo
			int end   = CurrentBarIndex - rightEdgeBarIndex;

			if (start < end)
				return;

			int opacity = Math.Max(1, Math.Min(100, SessionOpacity));
			string tag  = Tag(_currentSessionGroup, "SESS" + _sessionSeq);
			Draw.RegionHighlightX(_owner, tag, start, end, SessionBrush, SessionBrush, opacity);
		}

		/// <summary>Freezes the band on the last in-session bar and stops extending it.</summary>
		public void EndSession(int lastInSessionBarIndex)
		{
			ExtendSession(lastInSessionBarIndex);
			_currentSessionGroup = null;
			_sessionStartBar     = -1;
		}

		// ── Structure ─────────────────────────────────────────────────────────────
		public void DrawSwing(FpSwingRole role, int barsAgo, double price, int uid)
		{
			if (!ShowFullVisuals)
				return;

			string tag  = TrackTransient("SW" + uid);
			Draw.Text(_owner, tag, role.ToString(), barsAgo, price, MutedBrush);
		}

		public void DrawBreak(int direction, FpBreakEvent evt, double high, double low, int uid)
		{
			if (!ShowFullVisuals)
				return;

			Brush b = direction < 0 ? ShortBrush : LongBrush;

			string evtTag = TrackTransient("EV" + uid);
			Draw.Text(_owner, evtTag, evt.ToString(), 0, direction < 0 ? high : low, b);

			string dispTag = TrackTransient("DP" + uid);
			if (direction < 0)
				Draw.TriangleDown(_owner, dispTag, false, 0, high, b);
			else
				Draw.TriangleUp(_owner, dispTag, false, 0, low, b);
		}

		public void DrawBrokenLevel(int direction, double level, int barsAgoOfSwing, int uid)
		{
			if (!ShowFullVisuals || barsAgoOfSwing < 0)
				return;

			Brush b   = direction < 0 ? ShortBrush : LongBrush;
			string tag = TrackTransient("BL" + uid);
			Draw.Line(_owner, tag, false, barsAgoOfSwing, level, 0, level, b, DashStyleHelper.Dot, 1);
		}

		public void DrawRejection(int direction, string reasonLabel, double high, double low, int uid)
		{
			if (!ShowRejections)
				return;

			string tag = TrackTransient("RJ" + uid);
			Draw.Text(_owner, tag, "x" + reasonLabel, 0, direction < 0 ? high : low, Brushes.DarkOrange);
		}

		// ── Trades ────────────────────────────────────────────────────────────────
		private readonly Dictionary<string, List<string>> _tradeTags = new Dictionary<string, List<string>>();

		public void OpenTrade(TradeRecord t)
		{
			List<string> group = new List<string>();
			_tradeTags[t.SignalName] = group;
			_tradeGroups.Add(group);
			PruneGroups(_tradeGroups, TradeHistory);

			Brush dir = t.Direction > 0 ? LongBrush : ShortBrush;

			string markTag = Tag(group, "EN" + t.SignalName);
			if (t.Direction > 0)
				Draw.ArrowUp(_owner, markTag, false, 0, t.SignalPrice, dir);
			else
				Draw.ArrowDown(_owner, markTag, false, 0, t.SignalPrice, dir);

			string lblTag = Tag(group, "ET" + t.SignalName);
			Draw.Text(_owner, lblTag, (t.Direction > 0 ? "LONG #" : "SHORT #") + t.Sequence
			                          + " x" + t.Quantity + (t.ExtendedTpUsed ? " FP" : string.Empty),
			          0, t.SignalPrice, dir);

			UpdateTrade(t, 0);
		}

		/// <summary>
		/// Extends the open trade's SL and TP zones to <paramref name="barsAgoRightEdge"/>
		/// (0 = current bar). Each zone is a shaded box from the entry price to the level,
		/// plus a dashed line and a price label on the level itself.
		/// Gated on ShowTradeZones, NOT ShowFullVisuals — the zones are the point of the
		/// chart, so they stay on when the diagnostic clutter is off.
		/// </summary>
		public void UpdateTrade(TradeRecord t, int barsAgoRightEdge)
		{
			if (!ShowTradeZones)
				return;

			List<string> group;
			if (!_tradeTags.TryGetValue(t.SignalName, out group))
				return;

			int left = t.BarsAgoFrom(CurrentBarIndex);
			if (left < barsAgoRightEdge)
				return;

			// ── Stop-loss zone: entry → stop ──────────────────────────────────────
			string riskTag = Tag(group, "RK" + t.SignalName);
			Draw.Rectangle(_owner, riskTag, false, left, Math.Max(t.SignalPrice, t.StopPrice),
			               barsAgoRightEdge, Math.Min(t.SignalPrice, t.StopPrice), StopBrush, StopBrush, 10);

			string slLineTag = Tag(group, "SL" + t.SignalName);
			Draw.Line(_owner, slLineTag, false, left, t.StopPrice, barsAgoRightEdge, t.StopPrice,
			          StopBrush, DashStyleHelper.Dash, 2);

			string slTxtTag = Tag(group, "SLT" + t.SignalName);
			Draw.Text(_owner, slTxtTag, Label("SL", t.StopPrice), barsAgoRightEdge, t.StopPrice, StopBrush);

			// ── Take-profit zone: entry → target ──────────────────────────────────
			string rewardTag = Tag(group, "RW" + t.SignalName);
			Draw.Rectangle(_owner, rewardTag, false, left, Math.Max(t.SignalPrice, t.TargetPrice),
			               barsAgoRightEdge, Math.Min(t.SignalPrice, t.TargetPrice), TargetBrush, TargetBrush, 10);

			string tpLineTag = Tag(group, "TP" + t.SignalName);
			Draw.Line(_owner, tpLineTag, false, left, t.TargetPrice, barsAgoRightEdge, t.TargetPrice,
			          TargetBrush, DashStyleHelper.Dash, 2);

			string tpTxtTag = Tag(group, "TPT" + t.SignalName);
			Draw.Text(_owner, tpTxtTag, Label("TP", t.TargetPrice), barsAgoRightEdge, t.TargetPrice, TargetBrush);
		}

		private static string Label(string prefix, double price)
		{
			return prefix + " " + price.ToString("0.#####", CultureInfo.InvariantCulture);
		}

		public void CloseTrade(TradeRecord t, int barsAgoExit)
		{
			UpdateTrade(t, barsAgoExit);

			List<string> group;
			if (!_tradeTags.TryGetValue(t.SignalName, out group))
				return;

			string tag = Tag(group, "EX" + t.SignalName);
			Brush b = t.ExitReason == "TP" ? LongBrush : ShortBrush;
			Draw.Text(_owner, tag, t.ExitReason + (t.AmbiguousBarSeen ? "?" : string.Empty), barsAgoExit, t.ExitPrice, b);
		}

		// ── Panel ─────────────────────────────────────────────────────────────────
		public void DrawPanel(string text)
		{
			if (!ShowStatePanel)
				return;

			string tag = Track("FPMRPANEL");
			Draw.TextFixed(_owner, tag, text, TextPosition.TopRight);
		}

		// ── Bookkeeping ───────────────────────────────────────────────────────────
		/// <summary>Set by the strategy each bar so trade boxes can compute their left edge.</summary>
		public int CurrentBarIndex { get; set; }

		private readonly Queue<string> _transient = new Queue<string>();

		/// <summary>Tracks a one-off mark and drops the oldest once the cap is reached.</summary>
		private string TrackTransient(string tag)
		{
			Track(tag);
			_transient.Enqueue(tag);

			while (_transient.Count > Math.Max(1, TransientDrawingCap))
			{
				string old = _transient.Dequeue();
				if (_remove != null)
					_remove(old);
				_allTags.Remove(old);
			}

			return tag;
		}

		private string Track(string tag)
		{
			if (!_allTags.Contains(tag))
				_allTags.Add(tag);
			return tag;
		}

		private string Tag(List<string> group, string tag)
		{
			if (group != null && !group.Contains(tag))
				group.Add(tag);
			return Track(tag);
		}

		private void PruneGroups(List<List<string>> groups, int keep)
		{
			while (groups.Count > Math.Max(1, keep))
			{
				List<string> oldest = groups[0];
				groups.RemoveAt(0);
				foreach (string tag in oldest)
				{
					if (_remove != null)
						_remove(tag);
					_allTags.Remove(tag);
				}
			}
		}

		public void RemoveAll()
		{
			if (_remove != null)
				foreach (string tag in _allTags)
					_remove(tag);

			_allTags.Clear();
			_transient.Clear();
			_fairGroups.Clear();
			_tradeGroups.Clear();
			_sessionGroups.Clear();
			_tradeTags.Clear();
			_currentFairGroup    = null;
			_currentSessionGroup = null;
			_sessionStartBar     = -1;
		}
	}
}
