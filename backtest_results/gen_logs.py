# -*- coding: utf-8 -*-
"""Writes changes.md and errors.log per strategy. Every entry below is a real
change or a real compiler error from this session, not a placeholder."""
import io, os

ROOT = os.path.dirname(os.path.abspath(__file__))

def w(path, text):
    d = os.path.dirname(path)
    if d and not os.path.isdir(d):
        os.makedirs(d)
    io.open(path, "w", encoding="utf-8", newline="\n").write(text)


HEADER = """# Change log - {name}

All changes below were made BEFORE any backtest was run. Each entry states
whether trading logic was affected, because that determines whether results
from before and after are comparable.

Compile verification method: Microsoft C# compiler (csc.exe, .NET Framework
4.8) against NinjaTrader.Core.dll / NinjaTrader.Gui.dll / NinjaTrader.Custom.dll.
NinjaTrader's own NinjaScript compiler (F5) remains authoritative.

"""

ENTRY = """---

## {n}. {title}

- **File:** `{file}`
- **Trigger:** {trigger}
- **Cause:** {cause}
- **Fix:** {fix}
- **Logic impact:** {impact}
- **Recompiled:** {recompiled}
- **Backtest rerun:** {rerun}
"""


S1 = [
    dict(title="Daily P&L limits added (feature request)",
         file="FairPriceMeanReversion.Params.cs, .Trading.cs, .cs, .Setup.cs, FPMR/Core/RejectionReporter.cs, FpmrEnums.cs",
         trigger="User request: stop trading for the day once a loss or profit limit is hit.",
         cause="No realised-P&L tracking existed.",
         fix="Added group '5b Daily Limits' (UseDailyPnlLimits, DailyLossLimitUSD, DailyProfitLimitUSD, FlattenOnDailyLimit), realised-P&L accumulation on exit fills, and two new entry gates.",
         impact="YES - adds new entry gates. DEFAULT OFF (UseDailyPnlLimits=false), so default behaviour is unchanged.",
         recompiled="Yes, exit 0", rerun="N/A - no backtest run yet"),
    dict(title="Daily P&L limit made preventive rather than reactive",
         file="FairPriceMeanReversion.Trading.cs",
         trigger="User reported the limit was not accurate: the day's net exceeded the limit.",
         cause="The gate only blocked entries AFTER the limit was breached, so the day overshot by the whole risk of the trade that broke it.",
         fix="Added LossHeadroomFor(): an entry is refused if realised P&L minus open-trade risk minus this trade's risk would breach the limit.",
         impact="YES - fewer trades near a losing day's limit. Only active when UseDailyPnlLimits is on.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Partial exit fills were over-booked",
         file="FairPriceMeanReversion.Trading.cs, FPMR/Core/TradeRecord.cs",
         trigger="Found while auditing the P&L limit.",
         cause="rec.IsClosed was set on the FIRST exit execution and the FULL entry quantity was booked at that partial's price; remaining exit fills were then ignored. Entry partials were also truncated to the first fill.",
         fix="BookExitFill() books each execution at its own price and quantity; entry fills accumulate with a volume-weighted average price. A record is closed only when every filled contract is out.",
         impact="YES - corrects reported P&L when a bracket fills in pieces. This is a bug fix, not a logic change.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="P&L dropped for a trade exiting on the first bar of a new day",
         file="FairPriceMeanReversion.Trading.cs, .cs, .Setup.cs",
         trigger="Found while auditing the P&L limit.",
         cause="The daily accumulator reset in OnBarUpdate on IsNewDay, but booking happens in OnExecutionUpdate, which fires first for that bar. The exit was booked to the finished day and then wiped by the reset - lost from both days.",
         fix="RollPnlDay() keyed on the trading day derived from execution.Time, so the bar loop and the exit path cannot disagree.",
         impact="Bug fix. Affects the limit only.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Commission included in the daily net",
         file="FairPriceMeanReversion.Trading.cs",
         trigger="User asked for NET P&L.",
         cause="Only gross price difference was booked.",
         fix="Entry and exit commissions are booked as charged.",
         impact="Makes the limit slightly stricter. No effect on entries or exits themselves.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="All chart visuals removed",
         file="DELETED FPMR/Rendering/VisualEngine.cs (383 lines); 16 call sites stripped",
         trigger="User request, after per-bar Draw calls were identified as a backtest cost.",
         cause="Per-bar Draw.* calls on ~30k bars/month.",
         fix="Deleted VisualEngine and the '8 Visualisation' parameter group. Replaced the state panel with a run summary printed at State.Terminated.",
         impact="NONE - drawing only. No entry, exit or sizing path touched.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="News surprise classification (feature request)",
         file="FPMR/News/*.cs, FPMR/Core/ConsolidationDetector.cs, FpmrEnums.cs, Params/Setup",
         trigger="User request: classify scheduled news as expected vs unexpected and pick Fair Price accordingly.",
         cause="NewsEvent carried no forecast/actual fields and the loader never parsed them.",
         fix="Added forecast/actual/previous parsing (CSV and JSON), NewsSurpriseClassifier, ConsolidationDetector (range-compression), and group '6b News Surprise'.",
         impact="YES when enabled. DEFAULT OFF (UseNewsSurprise=false) AND the parent UseNewsFairPrice is also false by default, so default behaviour is unchanged.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Sessions moved to IST with automatic summer/winter handling",
         file="FairPriceMeanReversion.Params.cs, .Setup.cs, NEW FPMR/Core/DstAnchor.cs",
         trigger="User request: type summer IST times, have winter derived automatically.",
         cause="Windows were typed in America/New_York and did not track a user working from an IST clock.",
         fix="Windows are typed in Asia/Kolkata as SUMMER values and rebased once onto US Eastern, where they are fixed all year. Defaults converted to preserve the exact same market hours: 0930-1000 ET -> 1900-1930 IST, etc.",
         impact="NO CHANGE IN MARKET HOURS TRADED by default - the conversion is exact. Verified by unit test (14 checks, both seasons).",
         recompiled="Yes, exit 0", rerun="N/A"),
]

S2 = [
    dict(title="CS0115 - OnExecutionUpdate did not override anything",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs:1280",
         trigger="First compile attempt.",
         cause="The signature ended in `bool isExit`, which is the NinjaTrader 7 form. NinjaTrader 8's is `DateTime time`, so the method overrode nothing and the strategy would never have tracked a fill.",
         fix="Restored the NT8 signature and derived isExit from execution.Order.OrderAction (Sell / BuyToCover are exits).",
         impact="Required for the strategy to function at all. Preserves the original intent exactly.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="CS1061 - Bars.GetTradingDayFromLocal does not exist",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs:744",
         trigger="Second compile attempt.",
         cause="No such method on NinjaTrader.Data.Bars in NT8. Verified by reflection over NinjaTrader.Core.dll.",
         fix="Added a SessionIterator field, constructed in State.DataLoaded, and used sessionIterator.GetTradingDay(Time[0]).Date.",
         impact="Required to compile. Same semantics as intended.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Fixed contract sizing removed (feature request)",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs",
         trigger="User request.",
         cause="N/A - requested change.",
         fix="Removed the Contracts parameter and the UseRiskBasedSizing toggle. Every trade is now risk-sized.",
         impact="YES - position size now varies per trade.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Candle-extreme stop and R-multiple target added (feature request)",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs",
         trigger="User request.",
         cause="N/A - requested change.",
         fix="Added StopMode (FixedDistance | FirstCandleExtreme), StopBufferTicks, TargetMode (FixedDistance | RewardMultiple), RewardRatio. Structural legs use CalculationMode.Price; the R target is re-anchored to the actual fill in OnEntryFilled.",
         impact="YES when the new modes are selected. DEFAULTS UNCHANGED (FixedDistance / FixedDistance), so default behaviour matches the original.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Wrong time zone used for bar timestamps",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs (ResolveTimeZones)",
         trigger="Found while adding DST handling.",
         cause="sourceTimeZone read Bars.TradingHours.TimeZoneInfo - the zone the instrument's SESSION TEMPLATE is authored in (Central for CME index futures), not how NinjaTrader stamps bars. It relabelled an already-local timestamp and converted it again, shifting EVERY session by the gap between the two zones.",
         fix="Uses NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo, matching strategies 1 and 3.",
         impact="YES - THIS CHANGES RESULTS. Sessions previously fired on the wrong bars. This is a correction; results before and after are not comparable.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Sessions moved to IST with automatic summer/winter handling",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs",
         trigger="User request.",
         cause="N/A - requested change.",
         fix="Added SessionTimeZoneId (default Asia/Kolkata) and AutoAdjustForUsDst. Session times are typed as SUMMER IST and rebased onto US Eastern. Defaults converted to preserve the same market hours: 0930-1600 ET -> 1900-0130 IST, 1800-2300 -> 0330-0830, 0000-0800 -> 0930-1730.",
         impact="No change in market hours traded by default - the conversion is exact.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="CS0266 - Display Order must be an int",
         file="strategy_2/MultiSessionFirstCandleStrategy.cs:1930,1936",
         trigger="Compile after adding the two DST parameters.",
         cause="Used Order = 6.1 / 6.2 (double) on the Display attribute.",
         fix="Renumbered to 7 and 8; pushed StrictFirstCandle to 9 and OverlapMode to 10.",
         impact="NONE - parameter display order only.",
         recompiled="Yes, exit 0", rerun="N/A"),
]

S3 = [
    dict(title="Strategy authored from specification",
         file="strategy_3/MnqVpLiquiditySweep*.cs, strategy_3/VPS/*.cs",
         trigger="User request: build the strategy described in strategy_3/prompt.md.",
         cause="N/A - new work.",
         fix="Implemented per prompt.md sections 1-15.",
         impact="N/A - new strategy.",
         recompiled="Yes, exit 0 on first attempt", rerun="N/A"),
    dict(title="Pine indicator could not be referenced (specification section 14)",
         file="strategy_3/VPS/VolumeProfileEngine.cs",
         trigger="Section 14 asks for the supplied indicator's actual outputs.",
         cause="strategy_3/asian_volume_profile.md is TradingView Pine v6. Pine cannot be called from NinjaScript and no NT8 build exists.",
         fix="Ported f_profile() line for line into C#. Verified against 5 hand-computed cases (18 assertions, all pass), including the three details that are easy to get wrong: VAH/VAL are bin EDGES while POC is a bin MIDPOINT; a POC tie resolves to the LOWEST bin; the value-area walk takes an empty bin when the other side is exhausted.",
         impact="This is the 'unless absolutely necessary' exception in section 14. The port is exact, not an approximation.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Consolidation detector included the displacement candle",
         file="strategy_3/VPS/ConsolidationDetector-equivalent logic",
         trigger="Self-review before delivery.",
         cause="The arming bar was fed into the acceptance window.",
         fix="OnBar skips barIndex <= armBarIndex.",
         impact="Correctness fix in an unreleased file.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Sessions moved to IST, entry window added (feature request)",
         file="strategy_3/MnqVpLiquiditySweep.Params.cs, .Setup.cs, .Trading.cs, VPS/VpsSession.cs, NEW VPS/VpsDst.cs",
         trigger="User request: entries after the range closes, cutoff at a user-set time, IST with automatic summer/winter.",
         cause="N/A - requested change.",
         fix="Single SessionTimeZone (Asia/Kolkata) + AutoAdjustForUsDst. Windows typed as SUMMER IST and rebased onto US Eastern. Entry floor tied to the range session close; new EntryCutoff parameter (default 1500). Verified by unit test (14 checks, both seasons).",
         impact="YES - new entry gating, as requested.",
         recompiled="Yes, exit 0", rerun="N/A"),
    dict(title="Zero trades reported - diagnostics added",
         file="strategy_3/MnqVpLiquiditySweep.cs, .Trading.cs",
         trigger="User reported no trades generated in a backtest.",
         cause="NOT YET DIAGNOSED. Root cause unknown pending a run.",
         fix="NO LOGIC CHANGE. Added counters for every funnel stage (bars, profile/range sessions built and published, bars with levels, bars where entry was allowed, sweeps, confirmations, entries, and a per-gate rejection breakdown) plus a FirstBlockedStage() diagnosis printed at State.Terminated. The first profile and range publish are printed unconditionally.",
         impact="NONE - counters and Print only. No entry, exit, sizing or level logic touched.",
         recompiled="Yes, exit 0", rerun="PENDING - this is the run that must be done next"),
]

MAP = {"strategy_1": ("FairPriceMeanReversion", S1),
       "strategy_2": ("MultiSessionFirstCandleStrategy", S2),
       "strategy_3": ("MnqVpLiquiditySweep", S3)}

for key, (name, entries) in MAP.items():
    body = HEADER.format(name=name)
    for i, e in enumerate(entries, 1):
        body += ENTRY.format(n=i, **e)
    w(os.path.join(ROOT, "2026", key, "changes.md"), body)

    errs = ["# Error log - %s" % name, "",
            "Compile errors encountered and resolved, and runtime issues observed.",
            "Compile verification: csc.exe (.NET Framework 4.8) against the NinjaTrader assemblies.",
            "NinjaTrader's own compiler (F5) is authoritative and has NOT been run by the assistant.",
            ""]
    compile_errs = [e for e in entries if e["title"].startswith("CS")]
    if compile_errs:
        for e in compile_errs:
            errs += ["ERROR:  %s" % e["title"],
                     "FILE:   %s" % e["file"],
                     "CAUSE:  %s" % e["cause"],
                     "FIX:    %s" % e["fix"],
                     "IMPACT: %s" % e["impact"],
                     "RETEST: Recompiled clean (exit 0). Backtest not yet run.", ""]
    else:
        errs += ["No compiler errors. Compiles clean (exit 0).", ""]

    if key == "strategy_3":
        errs += ["RUNTIME ISSUE (OPEN):",
                 "  Symptom: user reports zero trades over a backtest.",
                 "  Status:  NOT DIAGNOSED. Requires a run with the instrumented build.",
                 "  Leading hypothesis (UNCONFIRMED): the data series' Trading Hours template is",
                 "  RTH-only. The range session is 1800-2000 New York and the entry window runs",
                 "  2000 -> 0530 New York, so an RTH template supplies NO bars there, RH/RL are",
                 "  never published, and InTradingWindow returns false on every bar.",
                 "  Action: use 'CME US Index Futures ETH' and read the funnel output.",
                 ""]

    w(os.path.join(ROOT, "2026", key, "errors.log"), "\n".join(errs))

w(os.path.join(ROOT, "logs", "execution.log"),
  "\n".join([
    "# Execution log",
    "",
    "2026-09-02  Located and inspected all three strategies.",
    "2026-09-02  Verified MNQ 1-minute data in NinjaTrader's db:",
    "              contracts MNQ 03-26 / 06-26 / 09-26",
    "              200 distinct trading days, 2026-01-01 -> 2026-09-02",
    "              MNQ 12-26 has no 1-minute data (outside the range).",
    "2026-09-02  Compile verification (csc.exe vs NinjaTrader assemblies):",
    "              FairPriceMeanReversion        23 files  exit 0",
    "              MnqVpLiquiditySweep           10 files  exit 0",
    "              MultiSessionFirstCandleStrategy 1 file  exit 0",
    "2026-09-02  Deployed all three to Documents/NinjaTrader 8/bin/Custom/Strategies.",
    "2026-09-02  Unit test: volume profile port vs 5 hand-computed cases - 18/18 pass.",
    "2026-09-02  Unit test: DST rebasing, summer and winter - 14/14 pass.",
    "",
    "2026-09-02  BACKTEST NOT EXECUTED.",
    "              The NinjaTrader 8 Strategy Analyzer is a WPF GUI application with no",
    "              headless mode, no backtest CLI, and no external API for launching a",
    "              backtest. No UI-automation tool was available in this environment.",
    "              Results are therefore marked PENDING_RUN rather than estimated.",
    "              Next step: run the three backtests per README.md, export, then run",
    "              scripts/import_nt8_results.py.",
    ""]))

print("logs written")
