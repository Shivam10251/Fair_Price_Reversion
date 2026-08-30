# What to verify in the first backtest

## 1. The sizing arithmetic against the real `PointValue`

Run with `VerboseLogging = true` and read the first few `ENTRY` lines. Each one
prints stop distance, risk per contract, quantity, resulting dollar risk and
whether it landed inside the tolerance band.

On **MNQ**, `Instrument.MasterInstrument.PointValue` should be **2.0**. The
strategy prints it once at load:

```
FPMR loaded: MNQ 06-24 | tick 0.25 | point value $2 | ...
```

With the defaults (`target $100`, `tolerance $20`, `hard cap $150`, `max 10`):

| Stop distance | Risk / contract | Qty | Resulting risk | Outcome |
|---|---|---|---|---|
| 10 pts | $20 | 5 | $100 | on target |
| 20 pts | $40 | 3 | $120 | on target (2.5 rounds away from zero) |
| 60 pts | $120 | 1 | $120 | on target |
| 75 pts | $150 | 1 | $150 | accepted — exactly at the cap |
| 80 pts | $160 | — | — | **skipped**, logged `RISK CAP` |
| 4 pts | $8 | 10 | $80 | clamped to `MaxContracts`, on target |

**Confirm the printed `point value` before trusting any of that.** If it prints
something other than 2 for MNQ, the instrument definition is wrong, not the code.

## 2. The news file's timezone handling

This is the highest-risk input in the whole port. Get it wrong and every
news-session Fair Price is taken from the wrong candle, silently.

- The **JSON** feed carries an explicit UTC offset per row
  (`2024-05-15T08:30:00-04:00`). That offset wins and `News file timezone` is
  ignored for those rows.
- The **CSV** export carries no zone at all. Its clock times are in whatever zone
  the exporting ForexFactory account was set to. `News file timezone` defaults to
  `America/New_York`, which is FF's default — **verify it matches the account that
  produced your file.**

To check: set `VerboseLogging = true`, `UseNewsFairPrice = true`, and find a day
with a known 08:30 ET release. The log should show

```
2024-05-15 08:30  FAIR PRICE 18432.25  [news: 2024-05-15 08:30 USD [High] Core CPI m/m]
```

If the timestamp is off by a whole number of hours, the file zone is wrong.
Also check the load line for skipped rows:

```
FPMR news calendar: 214 events from ... (csv, dates auto), 6 rows skipped.
```

Skipped rows are `All Day` / `Tentative` / blank-time entries, which is expected.
A large skip count means the date format was mis-detected — set
`News CSV date format` explicitly (e.g. `MM-dd-yyyy`).

## 3. Session windows against the bar timezone

Turn on the state panel and check that `Session: S1 active` appears on the bar you
expect. If the sessions are offset by hours, set `Bar timezone override`. This is
checked once, and then never again.

---

# What will make the backtest optimistic relative to live trading

Listed most to least material.

### Sizing creates a survivorship effect on the equity curve
The `RISK CAP` rule skips any setup whose one-contract risk exceeds the cap — on
MNQ, every displacement candle wider than 75 points. Wide displacement candles
cluster in exactly the high-volatility conditions where this strategy's edge is
least tested. The backtest therefore never shows you what those trades would have
done. That is the intended behaviour, but read the curve knowing it is a
*filtered* sample, and count the `RISK CAP` rejections to see how much was
filtered.

### Wide-stop trades risk up to 1.5× the target
A 60-point stop risks $120 and a 75-point stop risks $150 against a $100 target.
Losses are not uniformly sized; a bad run of wide-stop trades draws down faster
than "3 trades a day at $100 risk" suggests.

### Slippage is zero by default
`Slippage = 0`. Set it to at least one tick for MNQ before drawing any conclusion.
The entry is a market order at the next bar's open, which is where slippage
actually lands.

### Stop fills assume the stop price
In backtest a stop-market order fills at the stop price. Live, a gap through the
stop fills worse. The strategy stops sit at the displacement candle's extreme —
precisely where resting stops cluster.

### The bar-close entry model still costs you a bar
Covered in `DESIGN_DECISIONS.md §1`. NinjaTrader fills at the next bar's open,
which is more honest than TradingView's close fill — so if you compare the two,
the TradingView figures are the optimistic ones, not these.

### Same-bar TP/SL with `UseTickPrecision = false`
Turn it off and same-bar outcomes revert to an assumption. Check the state panel's
`Same-bar hits` count: if it is a meaningful fraction of trades, the tick-precision
run is the only one worth reading.

### The news schedule is treated as known in advance
Release *times* genuinely are published ahead, so this is not look-ahead on price.
But a file exported after the fact reflects the final schedule, including any
event that was rescheduled during the week. For a clean test, use a file exported
before the period you are testing.

### Limit-order fills
`IsFillLimitOnTouch = false`, so the take-profit limit requires the market to
trade *through* the price, not merely touch it. That is the conservative setting
and is left on deliberately.

---

# Test scenarios and where to look

| # | Scenario | How to confirm |
|---|---|---|
| 1–5 | CHoCH / BOS structure entries | `ShowFullVisuals = true`; the dotted broken-level line shows the exact level each entry was measured against |
| 2 | Only the newest HL can trigger | State panel `Active low` only ever holds one value |
| 6 | Inside the zone → no new trades | `ShowRejectionMarks = true`, look for `×IN ZONE` |
| 7 | Open trade survives re-entering the zone | The trade's boxes keep extending through the zone |
| 8–9 | TP / SL close the trade on the exact bar | Boxes stop extending on the exit bar; exit label shows `TP` / `SL` |
| 10 | Session 2 setup allowed after a session 1 trade | `Trades this session` resets, `Trades today` does not |
| 11 | Daily cap | `×DAY CAP` marks |
| 12–14 | Filters | Toggle each off and confirm the trade list is byte-identical |
| 15 | Setup invalidation | `Level age` in the panel exceeding `Setup validity` clears the active level |
| 16 | Sizing | The table in section 1 above |
| 17 | News Fair Price | Log line shows `FAIR PRICE ... [news: ...]` and the session-open value is never printed |
| 18 | `AfterSessionOpen` takes nothing pre-open | Panel shows a Fair Price while `Session: closed`, and no entry until the window opens |
| 19 | `MinBarsBeforeFirstTrade = 3` | `×WARMUP` on the first three eligible bars |
| 20 | News off → identical to Pine | Run with `UseNewsFairPrice = false`; no calendar is read at all |
