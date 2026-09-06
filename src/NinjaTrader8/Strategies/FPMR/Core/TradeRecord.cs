// =============================================================================
//  FPMR · Core · TradeRecord
//  Everything the strategy needs to know about one trade, from signal to exit.
//  Keyed by a unique entry signal name so concurrent trades can never collide.
// =============================================================================
using System;
using System.Globalization;

namespace NinjaTrader.NinjaScript.Strategies.FPMR
{
	public sealed class TradeRecord
	{
		public string       SignalName;
		public int          Sequence;
		public int          Direction;        // +1 long, -1 short
		public FpBreakEvent Event;

		public double   SignalPrice;          // displacement candle close
		public double   StopPrice;            // current stop — moves as the trail tightens it
		public double   InitialStopPrice;     // stop at entry, the reference for the R ratchet
		public double   TargetPrice;

		/// <summary>Trailing behaviour resolved for this trade at entry.</summary>
		public FpTrailMode Trail;
		/// <summary>Best favourable excursion in points since entry (for the R-step ratchet).</summary>
		public double   MaxFavorablePoints;
		public int      Quantity;
		/// <summary>True when the take-profit is Fair Price itself (a Band 2 "far" setup).</summary>
		public bool     TargetIsFairPrice;

		public int      EntryBarIndex;
		public DateTime EntryBarTime;
		public int      SessionIndex;

		public SizingResult Sizing;

		public bool     IsFilled;
		public double   FillPrice = double.NaN;
		public int      FilledQuantity;

		public bool     IsClosed;
		public string   ExitReason = "OPEN";
		public double   ExitPrice = double.NaN;
		public int      ExitBarIndex = -1;
		public DateTime ExitTime;

		/// <summary>Contracts closed so far. A trade is only IsClosed once this reaches FilledQuantity.</summary>
		public int      ExitedQuantity;
		/// <summary>Realised P&amp;L booked so far, summed across partial exits, net of commission.</summary>
		public double   RealisedPnl;
		public double   Commission;

		/// <summary>Dollar loss still at risk if the unexited remainder hits its stop.</summary>
		public double OpenRiskUsd(double pointValue)
		{
			if (!IsFilled || IsClosed || double.IsNaN(FillPrice))
				return 0.0;

			int remaining = FilledQuantity - ExitedQuantity;
			if (remaining <= 0)
				return 0.0;

			// Directional, and floored at zero: once the trail has moved the stop to
			// breakeven or into profit there is no dollar loss left at risk.
			double lossPoints = Direction > 0 ? FillPrice - StopPrice : StopPrice - FillPrice;
			if (lossPoints <= 0.0)
				return 0.0;

			return lossPoints * remaining * pointValue;
		}

		/// <summary>True when a single bar contained both the stop and the target.</summary>
		public bool AmbiguousBarSeen;

		/// <summary>Bars ago of this trade's entry bar, relative to <paramref name="currentBarIndex"/>.</summary>
		public int BarsAgoFrom(int currentBarIndex)
		{
			return Math.Max(0, currentBarIndex - EntryBarIndex);
		}

		public string Describe()
		{
			return string.Format(CultureInfo.InvariantCulture,
				"{0} {1} #{2} qty {3} @ {4:0.#####} | SL {5:0.#####} | TP {6:0.#####}{7}",
				Direction > 0 ? "LONG" : "SHORT",
				Event,
				Sequence,
				Quantity,
				SignalPrice,
				StopPrice,
				TargetPrice,
				TargetIsFairPrice ? " | FP-target" : string.Empty);
		}
	}
}
