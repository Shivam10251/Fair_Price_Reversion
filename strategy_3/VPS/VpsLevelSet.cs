// =============================================================================
//  VPS · VpsLevelSet
//
//  The five indicator lines, plus the two rules that operate on them.
//
//  DYNAMIC MAXIMUM-DISTANCE TARGET (specification sections 4-6)
//  The target is NEVER hard-wired VAH->VAL or RH->RL. After an entry the engine
//  measures the distance from the ACTUAL entry price to every opposite-side
//  level and takes the farthest. A short from VAH may therefore target RL, and
//  a short from RH may target VAL. A level only qualifies when it lies on the
//  correct side of the entry — a "target" at or behind the entry is not a
//  target, and when none qualifies the trade is not taken at all.
//
//  BREAK-EVEN INTERMEDIATE LINES (specification sections 7-9)
//  Only lines strictly BETWEEN the entry price and the chosen target arm the
//  break-even move, and the entry line and the target line are both excluded BY
//  IDENTITY rather than by price. That distinction matters: if RL and VAL happen
//  to print at the same price, targeting RL must not silently disarm VAL.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.VPS
{
	public struct VpsLevelValue
	{
		public VpsLevel Id;
		public double   Price;
		public bool     Valid;
	}

	public sealed class VpsTargetChoice
	{
		public bool     Found;
		public VpsLevel Id;
		public double   Price;
		public double   Distance;
		public string   Rejection = string.Empty;
	}

	public sealed class VpsLevelSet
	{
		public double Vah, Poc, Val, Rh, Rl;
		public bool   HasProfile;   // VAH / POC / VAL are populated
		public bool   HasRange;     // RH / RL are populated

		/// <summary>Which profile session produced VAH/POC/VAL, for logging.</summary>
		public DateTime ProfileSession;
		/// <summary>Which range occurrence produced RH/RL, for logging.</summary>
		public DateTime RangeSession;

		public bool AnyHighSide { get { return HasProfile || HasRange; } }

		public double PriceOf(VpsLevel id)
		{
			switch (id)
			{
				case VpsLevel.Vah: return Vah;
				case VpsLevel.Poc: return Poc;
				case VpsLevel.Val: return Val;
				case VpsLevel.Rh:  return Rh;
				case VpsLevel.Rl:  return Rl;
				default:           return double.NaN;
			}
		}

		public bool IsValid(VpsLevel id)
		{
			switch (id)
			{
				case VpsLevel.Vah:
				case VpsLevel.Poc:
				case VpsLevel.Val: return HasProfile;
				case VpsLevel.Rh:
				case VpsLevel.Rl:  return HasRange;
				default:           return false;
			}
		}

		/// <summary>Every populated line, in no particular order.</summary>
		public List<VpsLevelValue> All()
		{
			List<VpsLevelValue> list = new List<VpsLevelValue>(5);

			AddIf(list, VpsLevel.Vah);
			AddIf(list, VpsLevel.Poc);
			AddIf(list, VpsLevel.Val);
			AddIf(list, VpsLevel.Rh);
			AddIf(list, VpsLevel.Rl);

			return list;
		}

		private void AddIf(List<VpsLevelValue> list, VpsLevel id)
		{
			if (!IsValid(id))
				return;

			double p = PriceOf(id);
			if (double.IsNaN(p))
				return;

			list.Add(new VpsLevelValue { Id = id, Price = p, Valid = true });
		}

		/// <summary>
		/// The farthest qualifying opposite-side level.
		/// <paramref name="direction"/> is +1 for a long (targets above) and -1 for
		/// a short (targets below). Candidates are VAH/RH for a long and VAL/RL for
		/// a short — the POC is never a target, only ever an intermediate line.
		/// </summary>
		public VpsTargetChoice SelectTarget(int direction, double entryPrice,
		                                    double minDistance, VpsTargetTieBreak tieBreak)
		{
			VpsTargetChoice choice = new VpsTargetChoice();

			VpsLevel a = direction > 0 ? VpsLevel.Vah : VpsLevel.Val;
			VpsLevel b = direction > 0 ? VpsLevel.Rh  : VpsLevel.Rl;

			int considered = 0;

			considered += Consider(choice, a, direction, entryPrice, minDistance, tieBreak) ? 1 : 0;
			considered += Consider(choice, b, direction, entryPrice, minDistance, tieBreak) ? 1 : 0;

			if (!choice.Found)
				choice.Rejection = considered == 0
					? "neither opposite-side level is available yet"
					: "no opposite-side level sits far enough beyond the entry to be a valid target";

			return choice;
		}

		private bool Consider(VpsTargetChoice choice, VpsLevel id, int direction, double entryPrice,
		                      double minDistance, VpsTargetTieBreak tieBreak)
		{
			if (!IsValid(id))
				return false;

			double price = PriceOf(id);
			if (double.IsNaN(price))
				return false;

			// Distance is signed by direction, so a level behind the entry is negative
			// and can never win.
			double distance = direction > 0 ? price - entryPrice : entryPrice - price;

			if (distance < minDistance)
				return true;   // it existed, it just did not qualify

			bool better = !choice.Found || distance > choice.Distance;

			// Exact tie: fall back to the configured, user-visible rule.
			if (!better && choice.Found && distance == choice.Distance)
			{
				bool idIsRange = id == VpsLevel.Rh || id == VpsLevel.Rl;
				better = tieBreak == VpsTargetTieBreak.PreferRange ? idIsRange : !idIsRange;
			}

			if (better)
			{
				choice.Found    = true;
				choice.Id       = id;
				choice.Price    = price;
				choice.Distance = distance;
			}

			return true;
		}

		/// <summary>
		/// Lines that arm the break-even move: every populated line other than the
		/// entry line and the target line whose price sits strictly between the
		/// entry price and the target price.
		/// </summary>
		public List<VpsLevelValue> IntermediateLines(VpsLevel entryLine, VpsLevel targetLine,
		                                             double entryPrice, double targetPrice)
		{
			List<VpsLevelValue> result = new List<VpsLevelValue>(3);

			double lo = Math.Min(entryPrice, targetPrice);
			double hi = Math.Max(entryPrice, targetPrice);

			foreach (VpsLevelValue v in All())
			{
				// Excluded by IDENTITY, so a coincident price cannot disarm a
				// genuinely different line.
				if (v.Id == entryLine || v.Id == targetLine)
					continue;

				if (v.Price > lo && v.Price < hi)
					result.Add(v);
			}

			return result;
		}

		public string Describe(string tickFormat)
		{
			return string.Format(CultureInfo.InvariantCulture,
				"VAH {0} | POC {1} | VAL {2} | RH {3} | RL {4}",
				HasProfile ? Vah.ToString(tickFormat, CultureInfo.InvariantCulture) : "-",
				HasProfile ? Poc.ToString(tickFormat, CultureInfo.InvariantCulture) : "-",
				HasProfile ? Val.ToString(tickFormat, CultureInfo.InvariantCulture) : "-",
				HasRange   ? Rh.ToString(tickFormat,  CultureInfo.InvariantCulture) : "-",
				HasRange   ? Rl.ToString(tickFormat,  CultureInfo.InvariantCulture) : "-");
		}
	}
}
