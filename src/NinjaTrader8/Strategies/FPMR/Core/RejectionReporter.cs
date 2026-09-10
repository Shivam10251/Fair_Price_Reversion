// =============================================================================
//  FPMR · Core · RejectionReporter
//
//  Every structure break is a displacement candle. If it did not become a trade,
//  exactly one gate stopped it. This reports the FIRST gate that failed, in the
//  same order the entry logic evaluates them, so "why was there no entry here?"
//  always has a single answer.
//
//  Order (session and time gates first, then side, then budget, then filters,
//  then the two risk gates that only exist in the NT8 build):
//     SESSION -> NO FP -> NEWS WAIT -> WARMUP -> IN ZONE -> SIDE
//     -> DAY LOSS -> DAY PROFIT -> DAY CAP -> SESS CAP
//     -> IN TRADE -> EVT OFF -> EMA -> VWAP -> RISK -> RISK CAP -> R:R
//
//  The two daily P&L gates sit with the other budget gates and ahead of the
//  filters: once the day's realised loss or profit limit is reached, WHY a
//  particular filter would also have blocked the trade is no longer interesting.
// =============================================================================
namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	/// <summary>Snapshot of every gate for one displacement candle.</summary>
	public struct GateState
	{
		public bool InSession;
		public bool HasFairPrice;
		public bool NewsReady;    // no news surprise is still waiting for its consolidation
		public bool WarmupDone;
		public bool InZone;
		public bool DistanceOk;  // entry is not beyond Band 2 (too far from Fair Price)
		public bool SideOk;
		public bool DailyLossOk;   // realised loss for the day is inside the limit
		public bool DailyProfitOk; // realised profit for the day is inside the limit
		public bool DayCapOk;
		public bool SessionCapOk;
		public bool FlatOk;
		public bool EventOk;
		public bool EmaOk;
		public bool VwapOk;
		public bool RiskOk;      // risk > 0 and >= minimum stop distance
		public bool RiskCapOk;   // sizing produced at least one contract within the hard cap
		public bool RewardOk;    // reward/risk of the chosen target meets the minimum
		public bool Reconciled;  // strategy is not stuck on an unmatched restart position
	}

	public static class RejectionReporter
	{
		public static FpReject FirstFailure(GateState g)
		{
			if (!g.Reconciled)   return FpReject.Unreconciled;
			if (!g.InSession)    return FpReject.Session;
			if (!g.HasFairPrice) return FpReject.NoFairPrice;
			if (!g.NewsReady)    return FpReject.NewsWait;
			if (!g.WarmupDone)   return FpReject.Warmup;
			if (g.InZone)        return FpReject.InZone;
			if (!g.DistanceOk)   return FpReject.TooFar;
			if (!g.SideOk)       return FpReject.Side;
			if (!g.DailyLossOk)  return FpReject.DailyLoss;
			if (!g.DailyProfitOk)return FpReject.DailyProfit;
			if (!g.DayCapOk)     return FpReject.DayCap;
			if (!g.SessionCapOk) return FpReject.SessionCap;
			if (!g.FlatOk)       return FpReject.InTrade;
			if (!g.EventOk)      return FpReject.EventOff;
			if (!g.EmaOk)        return FpReject.Ema;
			if (!g.VwapOk)       return FpReject.Vwap;
			if (!g.RiskOk)       return FpReject.Risk;
			if (!g.RiskCapOk)    return FpReject.RiskCap;
			if (!g.RewardOk)     return FpReject.RewardRisk;

			return FpReject.None;
		}

		/// <summary>Short label used on chart marks and in the log.</summary>
		public static string Label(FpReject reason)
		{
			switch (reason)
			{
				case FpReject.Session:      return "SESSION";
				case FpReject.NoFairPrice:  return "NO FP";
				case FpReject.NewsWait:     return "NEWS WAIT";
				case FpReject.DailyLoss:    return "DAY LOSS LIMIT";
				case FpReject.DailyProfit:  return "DAY PROFIT LIMIT";
				case FpReject.Warmup:       return "WARMUP";
				case FpReject.InZone:       return "IN ZONE";
				case FpReject.TooFar:       return "TOO FAR";
				case FpReject.Side:         return "SIDE";
				case FpReject.DayCap:       return "DAY CAP";
				case FpReject.SessionCap:   return "SESS CAP";
				case FpReject.InTrade:      return "IN TRADE";
				case FpReject.EventOff:     return "EVT OFF";
				case FpReject.Ema:          return "EMA";
				case FpReject.Vwap:         return "VWAP";
				case FpReject.Risk:         return "RISK";
				case FpReject.RiskCap:      return "RISK CAP";
				case FpReject.RewardRisk:   return "R:R TOO LOW";
				case FpReject.Unreconciled: return "UNRECONCILED";
				default:                    return "NONE";
			}
		}
	}
}
