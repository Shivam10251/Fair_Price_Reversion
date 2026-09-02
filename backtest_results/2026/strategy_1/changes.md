# Change log - FairPriceMeanReversion

All changes below were made BEFORE any backtest was run. Each entry states
whether trading logic was affected, because that determines whether results
from before and after are comparable.

Compile verification method: Microsoft C# compiler (csc.exe, .NET Framework
4.8) against NinjaTrader.Core.dll / NinjaTrader.Gui.dll / NinjaTrader.Custom.dll.
NinjaTrader's own NinjaScript compiler (F5) remains authoritative.

---

## 1. Daily P&L limits added (feature request)

- **File:** `FairPriceMeanReversion.Params.cs, .Trading.cs, .cs, .Setup.cs, FPMR/Core/RejectionReporter.cs, FpmrEnums.cs`
- **Trigger:** User request: stop trading for the day once a loss or profit limit is hit.
- **Cause:** No realised-P&L tracking existed.
- **Fix:** Added group '5b Daily Limits' (UseDailyPnlLimits, DailyLossLimitUSD, DailyProfitLimitUSD, FlattenOnDailyLimit), realised-P&L accumulation on exit fills, and two new entry gates.
- **Logic impact:** YES - adds new entry gates. DEFAULT OFF (UseDailyPnlLimits=false), so default behaviour is unchanged.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A - no backtest run yet
---

## 2. Daily P&L limit made preventive rather than reactive

- **File:** `FairPriceMeanReversion.Trading.cs`
- **Trigger:** User reported the limit was not accurate: the day's net exceeded the limit.
- **Cause:** The gate only blocked entries AFTER the limit was breached, so the day overshot by the whole risk of the trade that broke it.
- **Fix:** Added LossHeadroomFor(): an entry is refused if realised P&L minus open-trade risk minus this trade's risk would breach the limit.
- **Logic impact:** YES - fewer trades near a losing day's limit. Only active when UseDailyPnlLimits is on.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 3. Partial exit fills were over-booked

- **File:** `FairPriceMeanReversion.Trading.cs, FPMR/Core/TradeRecord.cs`
- **Trigger:** Found while auditing the P&L limit.
- **Cause:** rec.IsClosed was set on the FIRST exit execution and the FULL entry quantity was booked at that partial's price; remaining exit fills were then ignored. Entry partials were also truncated to the first fill.
- **Fix:** BookExitFill() books each execution at its own price and quantity; entry fills accumulate with a volume-weighted average price. A record is closed only when every filled contract is out.
- **Logic impact:** YES - corrects reported P&L when a bracket fills in pieces. This is a bug fix, not a logic change.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 4. P&L dropped for a trade exiting on the first bar of a new day

- **File:** `FairPriceMeanReversion.Trading.cs, .cs, .Setup.cs`
- **Trigger:** Found while auditing the P&L limit.
- **Cause:** The daily accumulator reset in OnBarUpdate on IsNewDay, but booking happens in OnExecutionUpdate, which fires first for that bar. The exit was booked to the finished day and then wiped by the reset - lost from both days.
- **Fix:** RollPnlDay() keyed on the trading day derived from execution.Time, so the bar loop and the exit path cannot disagree.
- **Logic impact:** Bug fix. Affects the limit only.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 5. Commission included in the daily net

- **File:** `FairPriceMeanReversion.Trading.cs`
- **Trigger:** User asked for NET P&L.
- **Cause:** Only gross price difference was booked.
- **Fix:** Entry and exit commissions are booked as charged.
- **Logic impact:** Makes the limit slightly stricter. No effect on entries or exits themselves.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 6. All chart visuals removed

- **File:** `DELETED FPMR/Rendering/VisualEngine.cs (383 lines); 16 call sites stripped`
- **Trigger:** User request, after per-bar Draw calls were identified as a backtest cost.
- **Cause:** Per-bar Draw.* calls on ~30k bars/month.
- **Fix:** Deleted VisualEngine and the '8 Visualisation' parameter group. Replaced the state panel with a run summary printed at State.Terminated.
- **Logic impact:** NONE - drawing only. No entry, exit or sizing path touched.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 7. News surprise classification (feature request)

- **File:** `FPMR/News/*.cs, FPMR/Core/ConsolidationDetector.cs, FpmrEnums.cs, Params/Setup`
- **Trigger:** User request: classify scheduled news as expected vs unexpected and pick Fair Price accordingly.
- **Cause:** NewsEvent carried no forecast/actual fields and the loader never parsed them.
- **Fix:** Added forecast/actual/previous parsing (CSV and JSON), NewsSurpriseClassifier, ConsolidationDetector (range-compression), and group '6b News Surprise'.
- **Logic impact:** YES when enabled. DEFAULT OFF (UseNewsSurprise=false) AND the parent UseNewsFairPrice is also false by default, so default behaviour is unchanged.
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
---

## 8. Sessions moved to IST with automatic summer/winter handling

- **File:** `FairPriceMeanReversion.Params.cs, .Setup.cs, NEW FPMR/Core/DstAnchor.cs`
- **Trigger:** User request: type summer IST times, have winter derived automatically.
- **Cause:** Windows were typed in America/New_York and did not track a user working from an IST clock.
- **Fix:** Windows are typed in Asia/Kolkata as SUMMER values and rebased once onto US Eastern, where they are fixed all year. Defaults converted to preserve the exact same market hours: 0930-1000 ET -> 1900-1930 IST, etc.
- **Logic impact:** NO CHANGE IN MARKET HOURS TRADED by default - the conversion is exact. Verified by unit test (14 checks, both seasons).
- **Recompiled:** Yes, exit 0
- **Backtest rerun:** N/A
