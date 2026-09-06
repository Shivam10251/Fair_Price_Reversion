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
		public double   StopPrice;
		public double   TargetPrice;
		public int      Quantity;
		public bool     ExtendedTpUsed;
		/// <summary>The displacement candle's stop was too tight, so the fallback distance was used.</summary>
		public bool     FallbackStopUsed;

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

			return Math.Abs(FillPrice - StopPrice) * remaining * pointValue;
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
				(ExtendedTpUsed ? " | FP-target" : string.Empty)
			  + (FallbackStopUsed ? " | fallback SL" : string.Empty));
		}
	}
}
