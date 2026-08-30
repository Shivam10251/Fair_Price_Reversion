# Where NinjaTrader forced a deviation from the Pine version

Every item below is a place the two platforms genuinely disagree. Nothing here is
a simplification of the trading logic — the state machine, the active-level rules,
the setup invalidation, the session handling and the filters are reproduced as
specified. What changed is *how NinjaTrader executes and draws*.

Ranked by how much it moves backtest results.

---

## 1. Entries fill at the NEXT bar's open, not at the displacement candle's close

**Pine.** `process_orders_on_close = true` fills the entry at the close of the
signal candle. That is what the approved Pine results are built on.

**NinjaTrader.** There is no equivalent. With `Calculate.OnBarClose` the signal is
generated after the bar closes and the market order fills at the **open of the
next bar**. `Calculate.OnEachTick` would let you enter intrabar but would destroy
the "closes through the level" definition of a displacement candle, which is the
signal itself.

**Chosen.** `Calculate.OnBarClose` and accept the next-bar-open fill.

**Consequence.** Every trade carries one bar of gap risk that the TradingView
results do not. On MNQ 1-minute that is usually a tick or two, occasionally much
more on a news candle. Because the SL and TP are absolute prices computed from the
displacement candle, a fill away from the signal price changes the realised R:R —
so the strategy logs, on every fill, the slip in points, the actual dollar risk and
the actual R:R. **NinjaTrader's numbers will be worse than TradingView's, and the
NinjaTrader numbers are the honest ones.** Do not treat a divergence here as a bug.

---

## 2. Tick Replay is not a script property; `OrderFillResolution.High` is used instead

**Asked for.** A 1-tick secondary series plus Tick Replay, behind `UseTickPrecision`.

**Reality.** Tick Replay is a *data series* setting in NinjaTrader (chart data
series dialog, or the Strategy Analyzer), not something a strategy can switch on
from `SetDefaults`. It is also mutually exclusive with high order fill resolution,
because both exist to solve the same problem.

**Chosen.** `UseTickPrecision = true` sets
`OrderFillResolution = OrderFillResolution.High` with `Tick / 1`. This is
NinjaTrader's built-in "replay the real ticks through the fill engine" mechanism —
it *is* the 1-tick series, managed internally. A second, visible 1-tick
`AddDataSeries` on top of it would double the data load and change nothing.

**Consequence.** Same-bar TP/SL is resolved from the real tick sequence, which is
the fix the TradingView version could not have. It needs tick history for the
backtest range and it is slow. With `UseTickPrecision = false` the fill engine
falls back to `Standard`, and same-bar outcomes become an assumption again.
If you want Tick Replay as well (for tick-level `OnBarUpdate` data), enable it in
the data series dialog and set `UseTickPrecision = false`.

---

## 3. Bracket orders: `SetStopLoss` / `SetProfitTarget`, not explicit exit orders

**Chosen.** Managed orders. For each trade, with a unique signal name:

```csharp
SetStopLoss(signal,     CalculationMode.Price, stopPrice, false);
SetProfitTarget(signal, CalculationMode.Price, targetPrice);
EnterShort(qty, signal);          // brackets registered BEFORE the entry
```

with `StopTargetHandling.PerEntryExecution`.

**Why not `ExitLongStopMarket` / `ExitLongLimit`.** Those would work, but I would
have had to hand-roll the OCO pairing, the per-signal scoping, the resubmission
after a partial fill and the restart reconciliation. NinjaTrader already does all
of that for `Set*` brackets, and it keeps them alive across a disconnect.

**Consequence.** Exit reasons are read from the actual order that filled
(`"Stop loss"` / `"Profit target"`), not inferred from the price, so the reported
reason is exact. The Pine build had to compare the exit price against the levels.

---

## 4. `pyramiding` became an explicit input

Pine's `pyramiding = 0` plus "one open trade at a time" maps to
`EntriesPerDirection = 1` and `EntryHandling.AllEntries`. When the toggle is off,
NinjaTrader still needs a number, so **`MaxConcurrentEntriesPerDirection`
(default 3)** was added and `EntryHandling.UniqueEntries` is used. There is no
Pine equivalent — with the toggle on (the default) the input does nothing.

---

## 5. Time zones: IANA ids do not exist on Windows

`TimeZoneInfo.FindSystemTimeZoneById("America/New_York")` **throws** on the .NET
Framework that NinjaTrader 8 runs on. The Pine option list is kept verbatim and
`FPMR/Core/TimeZoneRegistry.cs` translates it to Windows ids
(`"Eastern Standard Time"`, which is DST-aware and becomes EDT in summer).

An unresolvable zone is a **hard configuration error**: the strategy logs it and
refuses to place orders rather than quietly falling back to machine local time.

---

## 6. Bar timestamps: close time in NinjaTrader, open time in TradingView

NinjaTrader stamps a bar with its **close**; TradingView evaluates a session
against the bar's **open**. Session membership here therefore uses
`Time[0] - barLength`, so `0930-1000` means "bars that open at 09:30 through
09:59", identical to Pine.

The zone those timestamps are expressed in is assumed to be the data series'
trading-hours zone (`Bars.TradingHours.TimeZoneInfo`). If your installation shows
bar times differently, set **`Bar timezone override`** — it exists precisely
because this assumption is worth being able to correct without editing code.
On a non-time-based primary series (tick, range, Renko) bar open times cannot be
derived; the strategy logs a warning and uses close times.

---

## 7. The Fair Price reference series is pumped from the primary bar

Pine used `request.security(..., "1", ...)`. Here, when the chart is already the
reference timeframe the primary series is reused and **no extra data is loaded**.
Otherwise a 1-minute series is added.

Either way the Fair Price engine is advanced from inside the *primary* bar handler
by a cursor over the reference series, not from `BarsInProgress == 1`. That makes
the result independent of NinjaTrader's multi-series call ordering, which is the
kind of thing that silently differs between historical and real-time.

---

## 8. Session-anchored VWAP is computed in-strategy

NinjaTrader's bundled VWAP is order-flow licensed and anchors to the instrument's
trading session. The spec wants an anchor at *your* session start or a new day, so
it is accumulated directly from `(H+L+C)/3 * Volume`. Instruments with no volume
yield NaN and the filter passes — matching Pine's `na(vwapVal)` guard.

---

## 9. News module: the news candle becomes the session's logical start

This is the one place the news rules needed a judgement call.

With `NewsTradingStart = AfterNewsCandle`, trading may begin before the session
window opens. Structure resets "on session start" — so does it reset at the news
candle, at the clock open, or both? Resetting twice would wipe the structure built
during exactly the period the mode exists to trade.

**Chosen.** When pre-session news trading is active, the news candle is that
session's logical start: structure resets there, the per-session trade counter
resets there, the warm-up starts there, and the clock open causes **no second
reset**. With `AfterSessionOpen` (the default) nothing changes — Fair Price is
simply visible early, and structure resets at the clock open as normal.

Other news decisions, all deliberate:

- Fair Price goes live **on the news candle** in both start modes. Only *entries*
  are gated by the mode. This is what makes test 18 meaningful.
- If a winning event is found for a session but its candle is **not in the loaded
  data** (data gap, or the backtest starts after the release), the strategy falls
  back to the normal first-candle rule and logs it. A missing candle is not the
  same as "no news", and pretending otherwise would trade a wrong reference price.
- If the news file is missing or unparseable, the whole module is disabled for the
  run with an error in the log, and behaviour reverts to the Pine rule.
- With `UseNewsFairPrice = false`, no calendar is read and no code path differs
  from the Pine version.

---

## 10. Visuals were collapsed, and the Pine drawing-window input was dropped

Pine needed `showHH` / `showHL` / `showLH` / `showLL` / `showBOS` / … and a
"restrict drawings to a time window" input purely to survive TradingView's hard
cap of 500 labels / lines / boxes. NinjaTrader has no such cap.

Per the port brief the individual switches are replaced by **`ShowFullVisuals`**
(default off) on top of the always-available entry/exit markers and Fair Price
line. History pruning is kept (`FairPriceHistory`, `TradeDrawingHistory`) because
unbounded drawing objects still cost memory. Every tag is tracked and removed on
`State.Terminated`.

---

## 11. Alerts became log output

Pine's `alert()` calls have no NinjaTrader equivalent that is useful in a
backtest. Entries, fills, exits, skips and rejections all go to the output window
behind `VerboseLogging`; hard errors go to the Log tab via `Log(..., LogLevel)`.
If you want push alerts in live trading, `Alert()` calls slot straight into
`SubmitEntry` and `OnExecutionUpdate`.

---

## 12. Restart and real-time reconciliation

`StartBehavior.WaitUntilFlat` plus an explicit check on the
`Historical -> Realtime` transition:

- live position with no matching trade record → **entries are blocked** and the
  rejection reason `UNRECONCILED` is reported until the position is flat;
- trade records with a flat live account → the stale records are dropped.

Order fills are never assumed: `OnExecutionUpdate` is what marks a trade filled,
and `OnOrderUpdate` un-counts a rejected or cancelled entry from the day and
session trade caps.

---

## 13. Smaller notes

- `FpTF` (a Pine timeframe string) became `FairPriceReferenceMinutes`, an int.
- `MaximumBarsLookBack.Infinite` — the reference-bar cursor and the broken-level
  line both reach further back than 256 bars.
- The rejection ladder gained `WARMUP` (after `NO FP`) and `RISK CAP` (last),
  which are NT8-only gates. The rest of the order is unchanged from Pine.
- `SameBarPriority` is **reporting only**, exactly as in Pine. With
  `UseTickPrecision` on, the real answer comes from the ticks and the setting only
  relabels; `TickSequence` tells the strategy to trust the fill engine and not
  relabel at all.
- Position sizing uses the **signal** price, so `riskPerContract` is the intended
  stop distance. The realised risk after the fill is logged separately.
