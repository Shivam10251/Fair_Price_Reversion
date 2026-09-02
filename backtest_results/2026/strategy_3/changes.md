# Change log - MnqVpLiquiditySweep

All changes below were made BEFORE any backtest was run. Each entry states
whether trading logic was affected, because that determines whether results
from before and after are comparable.

Compile verification method: Microsoft C# compiler (csc.exe, .NET Framework
4.8) against NinjaTrader.Core.dll / NinjaTrader.Gui.dll / NinjaTrader.Custom.dll.
NinjaTrader's own NinjaScript compiler (F5) remains authoritative.

---

## 1. Strategy authored from specification

- **File:** `strategy_3/MnqVpLiquiditySweep*.cs, strategy_3/VPS/*.cs`
- **Trigger:** User request: build the strategy described in strategy_3/prompt.md.
- **Cause:** N/A - new work.
- **Fix:** Implemented per prompt.md sections 1-15.
- **Logic impact:** N/A - new strategy.
- **Recompiled:** Yes, exit 0 on first attempt
- **Backtest rerun:** N/A
---

## 2. Pine indicator could not be referenced (specification section 14)

- **File:** `strategy_3/VPS/VolumeProfileEngine.cs`
- **Trigger:** Section 14 asks for the supplied indicator's actual outputs.
- **Cause:** strategy_3/asian_volume_profile.md is TradingView Pine v6. Pine cannot be called from NinjaScript and no NT8 build exists.
- **Fix:** Ported f_profile() line for line into C#. Verified against 5 hand-computed cases (18 assertions, all pass), including the three details that are easy to get wrong: VAH/VAL are bin EDGES while POC is a bin MIDPOINT; a POC tie resolves to the LOWEST bin; the value-area walk takes an empty bin when the other side is exhausted.
- **Logic impact:** This is the 'unless absolutely necessary' exception in section 14. The port is exact, not an approximation.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 3. Consolidation detector included the displacement candle

- **File:** `strategy_3/VPS/ConsolidationDetector-equivalent logic`
- **Trigger:** Self-review before delivery.
- **Cause:** The arming bar was fed into the acceptance window.
- **Fix:** OnBar skips barIndex <= armBarIndex.
- **Logic impact:** Correctness fix in an unreleased file.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 4. Sessions moved to IST, entry window added (feature request)

- **File:** `strategy_3/MnqVpLiquiditySweep.Params.cs, .Setup.cs, .Trading.cs, VPS/VpsSession.cs, NEW VPS/VpsDst.cs`
- **Trigger:** User request: entries after the range closes, cutoff at a user-set time, IST with automatic summer/winter.
- **Cause:** N/A - requested change.
- **Fix:** Single SessionTimeZone (Asia/Kolkata) + AutoAdjustForUsDst. Windows typed as SUMMER IST and rebased onto US Eastern. Entry floor tied to the range session close; new EntryCutoff parameter (default 1500). Verified by unit test (14 checks, both seasons).
- **Logic impact:** YES - new entry gating, as requested.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 5. Zero trades reported - diagnostics added

- **File:** `strategy_3/MnqVpLiquiditySweep.cs, .Trading.cs`
- **Trigger:** User reported no trades generated in a backtest.
- **Cause:** NOT YET DIAGNOSED. Root cause unknown pending a run.
- **Fix:** NO LOGIC CHANGE. Added counters for every funnel stage (bars, profile/range sessions built and published, bars with levels, bars where entry was allowed, sweeps, confirmations, entries, and a per-gate rejection breakdown) plus a FirstBlockedStage() diagnosis printed at State.Terminated. The first profile and range publish are printed unconditionally.
- **Logic impact:** NONE - counters and Print only. No entry, exit, sizing or level logic touched.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** PENDING - this is the run that must be done next
