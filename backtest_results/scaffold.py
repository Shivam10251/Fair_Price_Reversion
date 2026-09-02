# -*- coding: utf-8 -*-
"""Creates the backtest_results tree with everything knowable WITHOUT running
NinjaTrader. Performance fields are left null and status is PENDING_RUN, because
no backtest has been executed. Nothing here is estimated or invented."""
import io, json, os

ROOT = os.path.dirname(os.path.abspath(__file__))

DATA_START = "2026-01-01"
DATA_END   = "2026-09-02"

# Shared: what every run must be configured with.
COMMON = {
    "instrument": "MNQ",
    "merge_policy": "MergeNonBackAdjusted",
    "session_template": "CME US Index Futures ETH",
    "bar_type": "Minute",
    "bar_value": 1,
    "date_range": {"start": DATA_START, "end": DATA_END},
    "data_availability_verified": {
        "source": "C:/Users/DELL/Documents/NinjaTrader 8/db/minute/",
        "contracts_present": ["MNQ 03-26", "MNQ 06-26", "MNQ 09-26"],
        "distinct_2026_days_with_1min_data": 200,
        "first_day": "2026-01-01",
        "last_day": "2026-09-02",
        "note": "MNQ 12-26 has no 1-minute data on this machine; it is outside the range anyway."
    }
}

EMPTY_PERF = {
    "net_profit": None, "gross_profit": None, "gross_loss": None,
    "profit_factor": None, "max_drawdown": None, "sharpe_ratio": None,
    "sortino_ratio": None, "commission": None, "total_slippage": None,
    "percent_profitable": None, "average_trade": None,
    "average_winning_trade": None, "average_losing_trade": None,
    "largest_winning_trade": None, "largest_losing_trade": None,
    "max_consecutive_wins": None, "max_consecutive_losses": None
}

EMPTY_TRADES = {
    "total": None, "long": None, "short": None,
    "winning": None, "losing": None,
    "winning_long": None, "winning_short": None,
    "losing_long": None, "losing_short": None,
    "average_trade_duration": None
}

STRATEGIES = [
    {
        "key": "strategy_1",
        "display": "Strategy 1",
        "nt_name": "FairPriceMeanReversion",
        "source": [
            "src/NinjaTrader8/Strategies/FairPriceMeanReversion.cs",
            "src/NinjaTrader8/Strategies/FairPriceMeanReversion.Params.cs",
            "src/NinjaTrader8/Strategies/FairPriceMeanReversion.Setup.cs",
            "src/NinjaTrader8/Strategies/FairPriceMeanReversion.Trading.cs",
            "src/NinjaTrader8/Strategies/FPMR/**  (18 files)"
        ],
        "concept": ("Fair Price is the first reference candle of a session (or a news candle). "
                    "Price above the zone permits shorts, below permits longs. A market-structure "
                    "CHoCH/BOS break of the active swing level is the displacement/entry candle, "
                    "traded back toward Fair Price."),
        "entry": "CHoCH or BOS break of the active swing level, on the correct side of the Fair Price zone",
        "stop": "Displacement candle extreme +/- StopBufferTicks (absolute price)",
        "target": "RewardRatio multiple of risk, or Fair Price when the extended-TP override is armed",
        "breakeven": "None",
        "sizing": "Risk-based: RiskTargetUSD 100 +/- 20, hard cap 150, max 10 contracts",
        "key_parameters": {
            "SessionTimeZoneId": "Asia/Kolkata",
            "AutoAdjustForUsDst": True,
            "Session1Window": "1900-1930 (enabled)",
            "Session2Window": "2000-2100 (disabled)",
            "Session3Window": "2330-0030 (disabled)",
            "UseTickPrecision": True,
            "UseDailyPnlLimits": False,
            "UseNewsFairPrice": False,
            "UseEmaFilter": False,
            "UseVwapFilter": False,
            "OnlyOneOpenTrade": True,
            "MaxTradesPerDay": 3
        },
        "dependencies": "FPMR/* helper classes only. No external NinjaScript indicators.",
        "timeframe_requirement": "Designed for 1 Minute. A different primary triggers an extra 1-minute series."
    },
    {
        "key": "strategy_2",
        "display": "Strategy 2",
        "nt_name": "MultiSessionFirstCandleStrategy",
        "source": ["strategy_2/MultiSessionFirstCandleStrategy.cs  (single file)"],
        "concept": ("For each of up to three sessions, isolate the FIRST 1-minute candle, wait for "
                    "it to close, and trade in its direction (green long, red short)."),
        "entry": "Direction of the session's first 1-minute candle, at its close",
        "stop": "FixedDistance 20 points (default), or FirstCandleExtreme",
        "target": "FixedDistance 40 points (default), or RewardMultiple of actual risk",
        "breakeven": "None",
        "sizing": "Risk-based: RiskPerTrade 100 +/- 30, max 10 contracts",
        "key_parameters": {
            "SessionTimeZoneId": "Asia/Kolkata",
            "AutoAdjustForUsDst": True,
            "Session1": "1900-0130 (enabled, 1 trade)",
            "Session2": "0330-0830 (enabled, 1 trade)",
            "Session3": "0930-1730 (enabled, 1 trade)",
            "MaxTradesPerDay": 3,
            "StopMode": "FixedDistance",
            "TargetMode": "FixedDistance",
            "DistanceUnit": "Points",
            "StopLossValue": 20,
            "TakeProfitValue": 40,
            "UseTickPrecision": True,
            "DojiAction": "NoTrade",
            "StrictFirstCandle": True
        },
        "dependencies": "None. Self-contained single file.",
        "timeframe_requirement": "HARD REQUIREMENT: 1 Minute primary. Anything else is a config error and blocks all orders."
    },
    {
        "key": "strategy_3",
        "display": "Strategy 3",
        "nt_name": "MnqVpLiquiditySweep",
        "source": [
            "strategy_3/MnqVpLiquiditySweep.cs",
            "strategy_3/MnqVpLiquiditySweep.Params.cs",
            "strategy_3/MnqVpLiquiditySweep.Setup.cs",
            "strategy_3/MnqVpLiquiditySweep.Trading.cs",
            "strategy_3/VPS/**  (6 files)"
        ],
        "concept": ("Volume-profile levels (VAH/POC/VAL) from the profile session and range levels "
                    "(RH/RL) from the range session. A liquidity sweep of a high-side level that is "
                    "rejected or engulfed goes short; a low-side sweep goes long."),
        "entry": "Fresh penetration of VAH/RH (short) or VAL/RL (long), confirmed by same-candle rejection OR an engulfing candle within the confirmation window",
        "stop": "The SWEEP candle's extreme (absolute price), + StopBufferTicks",
        "target": "Farthest qualifying opposite-side level (dynamic, never a fixed R)",
        "breakeven": "Any OTHER indicator line strictly between entry and target, once touched, moves the stop to the exact entry fill",
        "sizing": "Fixed 1 contract (spec default); RiskBased mode available",
        "key_parameters": {
            "SessionTimeZone": "Asia/Kolkata",
            "AutoAdjustForUsDst": True,
            "ProfileSession": "1900-0130  (= 0930-1600 New York)",
            "RangeSession": "0330-0530  (= 1800-2000 New York)",
            "EntryCutoff": "1500",
            "ProfileBins": 60,
            "ValueAreaPercent": 70.0,
            "SweepConfirmWindowBars": 1,
            "LevelProximityTicks": 20,
            "MinTargetDistanceTicks": 4,
            "TargetTieBreak": "PreferRange",
            "UseBreakEven": True,
            "SizingMode": "Fixed",
            "Contracts": 1,
            "UseTickPrecision": True,
            "ShowVisuals": False
        },
        "dependencies": ("VPS/* helper classes only. The volume profile is an in-strategy C# port of the "
                         "supplied TradingView Pine indicator's f_profile(); Pine cannot be called from "
                         "NinjaScript, so no external indicator is referenced."),
        "timeframe_requirement": "HARD REQUIREMENT: 1 Minute primary. Anything else is a config error and blocks all orders."
    }
]


def w(path, text):
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    io.open(path, "w", encoding="utf-8", newline="\n").write(text)


def wj(path, obj):
    w(path, json.dumps(obj, indent=2) + "\n")


for st in STRATEGIES:
    base = os.path.join(ROOT, "2026", st["key"])

    cfg = dict(COMMON)
    cfg.update({
        "strategy": st["display"],
        "ninjatrader_strategy_name": st["nt_name"],
        "source_files": st["source"],
        "logic": {
            "concept": st["concept"],
            "entry": st["entry"],
            "stop_loss": st["stop"],
            "take_profit": st["target"],
            "break_even": st["breakeven"],
            "position_sizing": st["sizing"]
        },
        "dependencies": st["dependencies"],
        "timeframe_requirement": st["timeframe_requirement"],
        "parameters_as_run": st["key_parameters"],
        "order_fill_resolution": "High / Tick / 1  (set by the strategy when UseTickPrecision is on)",
        "commission_note": "Set on the NinjaTrader account commission template, not in NinjaScript.",
        "status": "PENDING_RUN"
    })
    wj(os.path.join(base, "config.json"), cfg)

    wj(os.path.join(base, "summary.json"), {
        "strategy": st["display"],
        "ninjatrader_strategy_name": st["nt_name"],
        "instrument": "MNQ",
        "year": 2026,
        "date_range": {"start": DATA_START, "end": DATA_END},
        "configuration": {
            "merge_policy": "MergeNonBackAdjusted",
            "timeframe": "1 Minute",
            "session_template": "CME US Index Futures ETH"
        },
        "status": "PENDING_RUN",
        "status_reason": ("No backtest has been executed. The NinjaTrader 8 Strategy Analyzer is a GUI "
                          "application with no headless or CLI mode, so it could not be driven from here. "
                          "Run it per backtest_results/README.md, export the results, then run "
                          "scripts/import_nt8_results.py to populate this file."),
        "performance": EMPTY_PERF,
        "trades": EMPTY_TRADES
    })

    w(os.path.join(base, "trades.csv"),
      "# No backtest executed yet - no trades to record.\n"
      "# Populate by exporting the Strategy Analyzer Trades grid and running\n"
      "# scripts/import_nt8_results.py. See backtest_results/README.md.\n")

print("scaffold written")
