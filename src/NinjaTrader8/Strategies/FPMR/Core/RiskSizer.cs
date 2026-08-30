// =============================================================================
//  FPMR · Core · RiskSizer
//
//  New in the NT8 build — the Pine version always traded 1 contract.
//  Sizes each trade so the dollar risk lands near RiskTargetUSD without ever
//  breaching RiskHardCapUSD.
//
//      riskPoints      = |entry - stop|
//      riskPerContract = riskPoints * PointValue
//      qty             = round(RiskTargetUSD / riskPerContract)   // nearest
//      qty             = clamp(qty, 1, MaxContracts)
//      while (qty * riskPerContract > hardCap && qty > 1) qty--
//      if (qty * riskPerContract > hardCap) -> skip the trade
//
//  PointValue is read from Instrument.MasterInstrument at run time; nothing here
//  assumes MNQ. On MNQ (PointValue $2) a 10-point stop gives 5 contracts and any
//  stop wider than 75 points is skipped at the default $150 hard cap.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public struct SizingResult
	{
		public bool   Accepted;
		public int    Quantity;
		public double RiskPoints;
		public double RiskPerContract;
		public double ResultingRisk;
		public bool   WithinTolerance;
		public bool   SteppedDown;      // the hard cap forced the quantity below the target size
		public bool   ClampedToMax;     // MaxContracts capped it
		public string SkipReason;

		public string Describe(double target, double tolerance, double hardCap)
		{
			string qty = Accepted ? Quantity.ToString(CultureInfo.InvariantCulture) : "0";
			return string.Format(CultureInfo.InvariantCulture,
				"stop {0:0.##} pts | ${1:0.##}/contract | qty {2} | risk ${3:0.##} | target ${4:0} +/- ${5:0} -> {6}{7}{8}{9}",
				RiskPoints, RiskPerContract, qty, ResultingRisk, target, tolerance,
				Accepted ? (WithinTolerance ? "ON TARGET" : "OUT OF BAND") : "SKIPPED",
				SteppedDown  ? " | stepped down for $" + hardCap.ToString("0", CultureInfo.InvariantCulture) + " cap" : string.Empty,
				ClampedToMax ? " | clamped to MaxContracts" : string.Empty,
				Accepted ? string.Empty : " | " + SkipReason);
		}
	}

	public static class RiskSizer
	{
		public static SizingResult Size(double entryPrice, double stopPrice, double pointValue,
		                                double riskTargetUsd, double riskToleranceUsd,
		                                double riskHardCapUsd, int maxContracts)
		{
			SizingResult r = new SizingResult();

			r.RiskPoints = Math.Abs(entryPrice - stopPrice);

			if (r.RiskPoints <= 0.0 || double.IsNaN(r.RiskPoints))
			{
				r.SkipReason = "stop distance is zero";
				return r;
			}

			if (pointValue <= 0.0)
			{
				r.SkipReason = "instrument PointValue is not positive";
				return r;
			}

			r.RiskPerContract = r.RiskPoints * pointValue;

			// Nearest whole contract to the target, then bounded.
			int qty = (int)Math.Round(riskTargetUsd / r.RiskPerContract, MidpointRounding.AwayFromZero);
			qty = Math.Max(qty, 1);

			if (maxContracts > 0 && qty > maxContracts)
			{
				qty          = maxContracts;
				r.ClampedToMax = true;
			}

			// Step down until the hard cap is respected.
			while (qty * r.RiskPerContract > riskHardCapUsd && qty > 1)
			{
				qty--;
				r.SteppedDown = true;
			}

			r.Quantity      = qty;
			r.ResultingRisk = qty * r.RiskPerContract;

			// Even a single contract breaches the cap -> no trade.
			if (r.ResultingRisk > riskHardCapUsd)
			{
				r.Accepted   = false;
				r.SkipReason = string.Format(CultureInfo.InvariantCulture,
					"1 contract risks ${0:0.##} which exceeds the ${1:0.##} hard cap (stop {2:0.##} pts)",
					r.RiskPerContract, riskHardCapUsd, r.RiskPoints);
				return r;
			}

			r.Accepted        = true;
			r.WithinTolerance = Math.Abs(r.ResultingRisk - riskTargetUsd) <= riskToleranceUsd;
			return r;
		}
	}
}
