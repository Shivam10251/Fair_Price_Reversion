# Multi-Session First Candle Strategy — NinjaTrader 8

Companion documentation for `MultiSessionFirstCandleStrategy.cs`.

---

## 1. What the strategy does

For each of up to three independently configured session windows, it isolates the **first 1-minute candle** of that window, waits for it to close, and trades its direction:

- Close > Open → **long**
- Close < Open → **short**
- Close == Open → **doji**, behaviour selectable (No Trade / Long / Short)

Optional EMA and VWAP filters can be layered on top. Every trade gets a fixed stop loss and take profit expressed in points or ticks.

---

## 2. Installation

1. In NinjaTrader 8: **New → NinjaScript Editor**.
2. In the editor's left panel, right-click **Strategies → New Strategy**, click **Next**, name it exactly `MultiSessionFirstCandleStrategy`, **Next**, **Finish**. This creates the file in the right folder.
3. Delete everything in the generated file and paste the full contents of `MultiSessionFirstCandleStrategy.cs`.

Alternative (equivalent): copy the `.cs` file directly into
`Documents\NinjaTrader 8\bin\Custom\Strategies\` while NinjaTrader is closed, then reopen NinjaTrader.

---

## 3. Compiling

Press **F5** in the NinjaScript Editor (or right-click → Compile). Watch the **Errors** tab at the bottom.

A clean compile prints nothing. If NinjaTrader reports a duplicate-type error on one of the `Msfc*` enums, you already have another copy of this file in your Strategies folder — delete the older one.

---

## 4. Adding it to an MNQ 1-minute chart

1. **New → Chart**, instrument `MNQ 12-26` (or your active front month).
2. Set the data series to **1 Minute** — this is mandatory. The strategy refuses to run on any other period and logs an error, because "first candle" is only defined against 1-minute bars.
3. Trading Hours template: **CME US Index Futures ETH** (see §11 on time zones).
4. Right-click the chart → **Strategies** → select `MultiSessionFirstCandleStrategy` → configure → **OK**.
5. Set **Enabled = True** in the strategies window when you actually want it live.

---

## 5. Configuring the three sessions

Times are entered as integers in **HHmm** form, interpreted in **US Eastern time** by default.

| Parameter | Example | Meaning |
|---|---|---|
| Enable Session 1 | True | Turns the window on |
| Session 1 Start | `930` | 09:30 ET |
| Session 1 End | `1600` | 16:00 ET |
| Session 1 Max Trades | `1` | Cap for this window |

Defaults ship as 09:30–16:00, 18:00–23:00, 00:00–08:00, one trade each, three per day. All of it is editable.

**Windows that cross midnight** are supported: set Start > End (e.g. `2200` → `200`). The strategy treats `[22:00 .. 24:00)` on day D plus `[00:00 .. 02:00)` on day D+1 as one occurrence.

**Strict First Candle** (default True): the first candle must open *exactly* on the session start minute. If that minute is missing from the data (holiday, thin session, data gap), the session is skipped for that occurrence and a `[SKIP]` line is printed. Set it to False if you'd rather take the first available in-window bar instead.

**Overlap behaviour** — documented and configurable:

- Sessions are evaluated in order 1, 2, 3.
- A single bar can be the first candle of more than one session only if two sessions share a start minute.
- `FirstMatchingSessionOnly` (default): the lowest-numbered session owns that candle. The others mark their first candle as consumed so they cannot fire later on a different bar of the same occurrence. **No duplicate trades.**
- `AllMatchingSessions`: each session is allowed to attempt an entry; the flat-position guard means only the first fills. This mode exists so the behaviour is a deliberate choice, not an accident.

---

## 6. Configuring SL and TP

| Parameter | Notes |
|---|---|
| Distance Unit | `Points` or `Ticks` |
| Stop Loss Value | e.g. `20` with Points |
| Take Profit Value | e.g. `40` with Points |

Conversion uses the instrument's real `TickSize`. For MNQ (TickSize 0.25), 20 points = 80 ticks and 40 points = 160 ticks. Nothing is hard-coded, and the same settings work on ES, NQ, CL, GC etc. without change.

Long: SL = fill − distance, TP = fill + distance.
Short: SL = fill + distance, TP = fill − distance.

Brackets are submitted with `CalculationMode.Ticks`, which anchors them to the **actual average fill price** rather than the assumed candle close (see §11 — this matters).

Zero or negative values are rejected at startup with an error in the Log tab.

### Position sizing — $100 risk per trade, ±$30 buffer

| Parameter | Default |
|---|---|
| Use Risk-Based Position Sizing | True |
| Risk Per Trade ($) | 100 |
| Risk Buffer +/- ($) | 30 |
| Max Contracts | 10 |
| Contracts | 1 — used **only** when risk-based sizing is off |

Per-contract dollar risk = stop distance in points × the instrument's real `PointValue`. MNQ is **$2 per point**, so a 20-point stop risks **$40 per contract**. The same code sizes NQ ($20/pt), ES ($50/pt), CL and GC correctly with no changes.

The accepted band is target ± buffer, so by default **$70 to $130**. Selection:

1. Ideal quantity = $100 ÷ risk-per-contract.
2. Test the floor and ceiling of that ideal, clamped to 1…Max Contracts.
3. Discard any whose total risk falls outside the band.
4. Take the survivor closest to $100. **On an exact tie, take the smaller quantity** — under-risking is the cheaper error.
5. If nothing fits the band, the **trade is skipped** and the Output window says why. Nothing is forced through at the wrong size.

Worked cases on MNQ:

| Stop | Risk/contract | Ideal | Result |
|---|---|---|---|
| 20 pts | $40 | 2.50 | 2 ($80) and 3 ($120) both fit; exact tie → **2 contracts** |
| 15 pts | $30 | 3.33 | 3 = $90, 4 = $120 → **3 contracts** ($90 is closer to $100) |
| 25 pts | $50 | 2.00 | **2 contracts**, exactly $100 |
| 60 pts | $120 | 0.83 | 1 contract = $120, inside the band → **1 contract** |
| 80 pts | $160 | 0.63 | 1 contract = $160, above $130 → **trade skipped** |

Because the stop is a fixed point distance known before the order is sent, the dollar risk of a given quantity is exact and does not depend on the fill price. A `[SIZING]` line prints at startup showing the quantity the current settings will trade, so you see it before any money is committed. `Risk Buffer` must be smaller than `Risk Per Trade`, or the lower edge of the band collapses to zero and every size would pass — that's rejected at startup.

Note that this is **price risk only**. Commission and slippage are excluded, so a 2-contract MNQ trade budgeted at $80 actually costs a few dollars more round-trip.

---

## 7. EMA and VWAP

| Parameter | Default | Effect |
|---|---|---|
| EMA 1 Period / EMA 2 Period | 9 / 21 | Fully configurable |
| Show EMA 1 / Show EMA 2 | True | Visual only |
| Use EMA Filter | **False** | Off by default — EMAs do not affect entries |
| EMA Filter Mode | PriceVsEma1 | `PriceVsEma1`, `PriceVsEma2`, `Ema1VsEma2`, `PriceVsBothEmas` |
| Show VWAP | True | Visual only |
| Use VWAP Filter | **False** | Off by default |

Filter semantics: long requires price/EMA relationship on the bullish side, short on the bearish side. With `Ema1VsEma2`, long requires EMA1 > EMA2. VWAP filter: long requires the first candle's close above VWAP, short below.

Filters combine with AND. Both off → pure candle colour. The logic lives in `PassesEMAFilter()` and `PassesVWAPFilter()`; add a new mode by extending the enum and the switch, nothing else changes.

**VWAP note:** it is a session-anchored VWAP computed from the primary 1-minute series as `sum(typicalPrice × volume) / sum(volume)`, reset at each NinjaTrader session start. No secondary data series is added. This is deliberate — a tick-level VWAP would require a second series and slow backtests substantially, while the 1-minute approximation on MNQ differs by a fraction of a tick. It also avoids depending on the Order Flow VWAP indicator, which isn't available on every licence tier.

---

## 8. Visual TP/SL zones

| Parameter | Default |
|---|---|
| Show Trade Zones (master switch) | True |
| Show Entry Line / SL Line / TP Line | True |
| Show Risk Zone / Reward Zone | True |
| Zone Opacity | 15 |
| Max Drawn Trades | 50 |
| Draw On Historical Bars | True |
| Entry / Stop Loss / Take Profit Colour | Blue / Firebrick / SeaGreen |

Each element toggles independently. While a trade is open, the five objects are *updated in place* using the same tags, so an open trade costs a fixed five objects however long it lasts. On exit, the zones freeze at the exit bar and a `TP` / `SL` / `EXIT` text stamp is added — that's how you tell a closed trade from a live one.

Object count is capped by **Max Drawn Trades**; older trades' objects are removed automatically. For long optimisations, set **Draw On Historical Bars = False** or **Show Trade Zones = False** to eliminate drawing overhead entirely.

---

## 9. Running it in Strategy Analyzer

1. **New → Strategy Analyzer**.
2. Left panel: select `MultiSessionFirstCandleStrategy`.
3. Instrument: `MNQ ##-##`. Use a **continuous contract** (Merge Policy: Merge Back Adjusted) for multi-quarter runs.
4. Data series: **1 Minute**, Trading Hours **CME US Index Futures ETH**.
5. Set the date range, configure the parameters, click **Run**.
6. Open the **Output** window (New → Output) to see `[SIGNAL]`, `[ENTRY]`, `[EXIT]`, `[SKIP]` traces and the custom metrics summary printed at the end.

---

## 10. Recommended Strategy Analyzer settings for realistic MNQ

| Setting | Value | Why |
|---|---|---|
| Order Fill Resolution | **High, 1 Tick** | Set in code. Resolves the "SL and TP both inside the same 1-minute bar" ambiguity using real tick sequence instead of assuming the worst case. Requires tick data for the range — if you don't have it, change `OrderFillResolution` to `Standard` in `State.SetDefaults` and accept the pessimistic assumption. |
| Slippage | **1 tick** (0.25 pts) | Market-order entries on the 09:30 candle close cross the spread. 2 ticks is a fair stress test. |
| Commission | Attach a commission template ≈ **$0.35–$1.24 round turn** per MNQ contract | Depends on broker + exchange/NFA fees. Set it under Tools → Options → Commissions, then select the template in the Analyzer. |
| Account size | Realistic for your prop account | Affects drawdown percentages. |
| Min bars required | 20 (default) | Warm-up for the EMAs. |
| Include commission in stats | On | Otherwise a 1-contract, 40-point-target system looks better than it is. |

Bar-magnifier / high-resolution data is the single biggest driver of backtest realism here, because the whole trade lifecycle happens inside 1-minute bars.

---

## 11. Limitations you need to know about

**Candle-close execution.** NinjaTrader's backtest engine cannot fill an order at the closing price of the bar that generated it. The strategy runs on `Calculate.OnBarClose`, submits a market order at the close of the first candle, and NinjaTrader fills it at the **open of the next bar**. So a 09:30 signal fills at roughly 09:31:00, not 09:30:59.999.

This is the honest, realistic behaviour and it is why the brackets use `CalculationMode.Ticks`: NinjaTrader anchors SL/TP to the actual fill. If the code had computed absolute prices from `Close[0]`, every bracket would be silently misplaced by the size of the next-bar gap — flattering in backtest, wrong in live. If you want fills closer to the candle close in production, that's a limit-order variant, and it introduces its own no-fill risk. Do not "fix" this by switching to `Calculate.OnEachTick` and entering intra-bar; that reintroduces look-ahead in historical mode.

**No look-ahead exists anywhere.** With `OnBarClose`, `OnBarUpdate` fires exactly once per completed bar and only closed-bar data (`Open[0]`, `Close[0]`, EMA, VWAP) is read. The direction of the first candle is structurally unknowable before it closes.

**Time zone and DST.** NinjaTrader time-stamps a data series in the time zone of its **Trading Hours template**, and it stamps each bar at its **close** — the 09:30:00–09:30:59 candle carries the timestamp 09:31:00. The strategy therefore computes the bar's *open* time (`Time[0] − 1 minute`) and converts it from `Bars.TradingHours.TimeZoneInfo` to US Eastern via `TimeZoneInfo`, so DST is handled by the operating system's tz database rather than a hand-rolled second-Sunday-in-March rule. Subtraction happens before conversion, which keeps the arithmetic monotonic across the transition.

Configure the Trading Hours template as **CME US Index Futures ETH**. If that template is already Eastern, the conversion is a no-op and costs nothing. If your NinjaTrader is set to a non-Eastern zone, the conversion does the work. Set **Session Time Zone Mode = UseChartTimeZone** only if you deliberately want your HHmm inputs read in the chart's own zone.

**Trading-day definition.** Default `NinjaTraderTradingDay` uses `Bars.GetTradingDayFromLocal(Time[0])`, i.e. the trading date from your Trading Hours template. For CME ETH this groups the 18:00 ET Sunday open with Monday, so an 18:00–23:00 session and a 00:00–08:00 session belong to the **same** trading day and share the daily limit. Switch to `EasternCalendarDate` if you'd rather the counter roll at Eastern midnight. Per-session state resets on each new session *occurrence*, independently of the daily reset — that's deliberate, so a day rollover inside a live overnight session can't re-arm the first-candle detector.

**Custom metrics.** Session/long/short/TP/SL counts and max consecutive win/loss streaks are printed to the Output window at the end of a run. They are not added as Strategy Analyzer *columns* — that requires a separate `PerformanceMetric` add-on class, and NinjaTrader's built-in engine already provides Net Profit, Gross Profit/Loss, Profit Factor, Max Drawdown, Sharpe, Win Rate, trade counts, averages, largest win/loss and consecutive streaks. None of that is duplicated.

**Session-close flattening.** `IsExitOnSessionCloseStrategy = true` with 30 seconds means NinjaTrader flattens any open position before the template's session close (17:00 ET for CME ETH). If you're running a session that butts against that, expect the exit there.

---

## 12. Worked example

Settings: Session 1 = 0930–1600, max 1 trade; SL 20 points, TP 40 points, unit Points; filters off; MNQ.

| Time (ET) | Event |
|---|---|
| 09:30:00 | The 09:30 candle opens at 23,000.00. Nothing happens — the strategy has no opinion yet. |
| 09:30:59 | Candle still forming. Still nothing. |
| 09:31:00 | Bar closes at 23,020.00 and `OnBarUpdate` fires. Session 1's occurrence for today is `2026-08-27 09:30`, its first candle has not been processed, position is flat, daily count 0/3, session count 0/1. Close (23,020) > Open (23,000) → **GREEN → LONG**. `SetStopLoss(80 ticks)` and `SetProfitTarget(160 ticks)` are attached to signal `S1_Long`, then `EnterLong(1, "S1_Long")`. Session 1 is marked consumed; counters go to 1/1 session, 1/3 daily. |
| 09:31:00+ | The market order fills at the open of the 09:31 candle — say 23,021.25. SL is placed at 23,001.25 (fill − 80 ticks), TP at 23,061.25 (fill + 160 ticks). Zones are drawn from the entry bar. |
| … | Price reaches 23,061.25 → profit target fills. `[EXIT] ... via 'Profit target' ... WIN` is printed, the zones freeze with a `TP` stamp, and the stop is cancelled by OCO. |
| Rest of session 1 | No further trades: Session 1's cap is 1 and its first candle is consumed. |
| 18:00 | Session 2's own first candle is evaluated independently. Session 1's outcome does not disable it — only the daily cap of 3 can. |

If `Allow New Trade After Previous Trade Closes` were **OFF**, the 18:00 signal would be skipped because a trade already closed that day, and the Output window would say exactly that.
