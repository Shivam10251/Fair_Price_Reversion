# Fair Price Mean Reversion · BOS/CHoCH/Displacement — MVP docs

Companion to `FairPriceMeanReversion_MVP.pine` (Pine Script v6, `strategy`).
No first-candle-colour entry exists anywhere in the script. The opening 1-minute
candle only sets Fair Price.

---

## A. Architecture

Everything runs as one pass per bar, in this fixed order:

1. **Session management** — three independent windows, evaluated in the chosen timezone.
2. **Fair Price** — pulled from the 1-minute series via `request.security`, one value per session.
3. **Fair Price zone + drawings** — dynamic-extension line/box, pruned history.
4. **Swing detection** — `ta.pivothigh` / `ta.pivotlow`, confirmed only.
5. **Structure state machine** — one active high, one active low, a bull/bear state flag.
6. **Break detection** — CHoCH vs BOS decided by the state at the moment of the break.
7. **Filters** — EMA and VWAP, each independently on/off.
8. **Entry validation** — session, zone, direction, limits, filters.
9. **Order placement** — entry at the displacement candle's close, bracket exit.
10. **Exit detection** — read back from the broker emulator, so drawings match fills.
11. **Trade visualisation engine** — one create/update/close path for every trade.
12. **Alerts** and **debug panel**.

`process_orders_on_close = true` is set so an entry signalled at a candle close is
filled at *that* close rather than the next bar's open. That is what the spec asks for.

---

## B. Assumptions (the §59 list, answered)

1. **Confirmed HH/HL/LH/LL.** A pivot high is `ta.pivothigh(pivotLeft, pivotRight)`: a bar
   whose high exceeds the highs of `pivotLeft` bars before it and `pivotRight` bars after it.
   It becomes known `pivotRight` bars late, and the script only ever reacts to it from that
   bar forward. The label (HH vs LH) comes from comparing it to the **previous confirmed
   pivot high**; lows compare to the previous confirmed pivot low. First pivot after a reset
   is labelled from the current structure state.
2. **Active structure level.** Exactly one active high and one active low at any time.
   A newly confirmed pivot replaces the previous one of the same side. Two modes:
   *Latest swing* (default — newest confirmed pivot always wins) and *Role-valid only*
   (a new active low is accepted only when it is a genuine HL in bullish state / LL in
   bearish state; mirrored for highs).
3. **CHoCH.** A break of the active level **against** the current structure state:
   bullish state + break of the active low = bearish CHoCH; bearish state + break of the
   active high = bullish CHoCH.
4. **BOS.** A break of the active level **in the direction of** the current state:
   bearish state + break of the active low = bearish BOS; bullish state + break of the
   active high = bullish BOS.
5. **Displacement.** The candle that performs the break, and nothing else. Size, colour,
   body ratio, and candle-count patterns are never consulted. Break confirmation defaults to
   **close** beyond the level (`Wick` is selectable). Once broken, the level is consumed
   (set to `na`) so the same level can never fire twice.
6. **Fair Price.** For each enabled session, the source (default `close`) of the **first
   1-minute candle**, published to the chart once that candle has closed.
7. **Fair Price zone.** `FP ± zone distance` (default 20 points; ticks selectable). Drawn as
   a shaded box that extends forward while the session runs, then freezes.
8. **No-trade zone.** While the bar's close sits inside the zone, no new entry may be
   created. Open trades are never touched by this rule.
9. **SL.** Short: displacement candle's **high** (+ optional tick buffer).
   Long: displacement candle's **low** (− buffer).
10. **TP.** `risk = |entry − SL|`, `TP = entry ∓ risk × RR` (default RR 1.5).
11. **EMA filter.** Long requires `EMA1 > EMA2`, short requires `EMA1 < EMA2`. Off ⇒ the
    condition is hard-coded `true` and cannot affect entries. Display toggles are separate.
12. **VWAP filter.** Session-anchored VWAP (anchored to session start / new day). Long requires
    close above it, short requires close below it. Off ⇒ no effect. If the instrument has no
    volume the filter passes and the panel says so.
13. **Multiple sessions.** Up to three, independently enabled; overlapping windows resolve to
    the lowest-numbered enabled session. Each session sets its own Fair Price and resets
    structure and the per-session trade counter.
14. **Multiple trades.** Each entry gets a sequential id (`T1`, `T2`, …) and its own
    drawings, which are never overwritten. By default only one trade is open at a time
    (togglable), capped by max-per-day and optional max-per-session.
15. **Setup invalidation.** Three ways: (a) the active level expires after
    `Setup validity` bars from its confirmation bar, (b) price flips to the other side of
    the Fair Price zone, which resets the whole structure, (c) a new session starts.
    Nothing stale is carried forward.
16. **Repainting.** Pivots are used only after `pivotRight` confirmation bars; Fair Price is
    taken from a *closed* 1-minute candle; breaks are evaluated on bar close; no
    `lookahead_on`, no negative offsets, no future references. The delay is accepted rather
    than engineered away.
17. **Same-candle TP/SL.** See section I — this is the one place where TradingView cannot
    give a truthful answer from 1-minute OHLC alone, and the script does not pretend otherwise.

---

## C. Inputs

### 1 · Sessions
| Input | Meaning |
|---|---|
| Session timezone | Timezone all three windows are evaluated in. `Chart / Exchange` uses `syminfo.timezone`. |
| Session 1/2/3 + window | Enable flag and `HHMM-HHMM` window. Defaults `0930-1000`, `1030-1130`, `1400-1500`; only Session 1 is on. |

### 2 · Fair Price
| Input | Meaning |
|---|---|
| Fair Price source | Which price of the opening candle. Default `Close`. |
| Reference candle timeframe | Default `1` (minute). Fair Price is always taken from this series, whatever the chart timeframe. |
| Zone distance unit / distance | `Points` (default, 20) or `Ticks`. |
| Line forward extension | How many bars ahead of the current bar the line/box is drawn. Default 5, extended each bar. |
| Drawings kept | How many past sessions' Fair Price drawings stay on the chart. Default 5. |

### 3 · Market Structure
| Input | Meaning |
|---|---|
| Pivot left / right bars | Swing sensitivity. Default 3 / 2. Right bars = confirmation delay. |
| Structure break confirmation | `Close` (default) or `Wick`. |
| Active level update mode | `Latest swing` (default) or `Role-valid only`. |

### 4 · Trade Management
| Input | Meaning |
|---|---|
| Risk / Reward ratio | Default 1.5. |
| Max trades per day | Default 3, `0` = unlimited. Day boundary is the calendar day in the session timezone. |
| Max trades per session | Default 0 = unlimited within a session (the daily cap still applies). |
| Setup validity (bars) | Default 30. An active level unbroken for this many bars since confirmation is discarded. `0` disables expiry. |
| Only one open trade at a time | Default on. |
| Take CHoCH / BOS entries | Independently disable either event type. |
| SL buffer (ticks) | Extra distance beyond the displacement candle's extreme. Default 0. |
| Minimum stop distance (ticks) | Skip degenerate setups with a tiny displacement candle. Default 0 = off. |
| Close trades at session end | Default off. |
| Same-candle TP/SL priority | `SL first (conservative)` default. See section I. |
| Lower timeframe for same-candle check | Only used by the `Lower timeframe check` option. |

### 5 · Filters
`Use EMA filter` (off), `EMA 1 length` (9), `EMA 2 length` (21), `Use VWAP filter` (off).
Logic toggles are separate from the display toggles in group 6.

### 6 · Visualisation
Independent toggles for Fair Price line, Fair Price zone, HH, HL, LH, LL, BOS, CHoCH, DISP,
Entry, SL, TP, Risk zone, Reward zone, Exit labels, EMA 1, EMA 2, VWAP, plus
`Trade drawings kept` (default 30) for object-count control. None of these touch the
trading logic.

### 7 · Debug & Alerts
`Show debug panel`, `Fire alert() calls`.

---

## D. BOS / CHoCH / displacement logic

```
break source = (Close mode) close        (Wick mode) low / high
bearBreak = active low  exists AND break source < active low
bullBreak = active high exists AND break source > active high
```

At the moment of a break:

| State before | Break | Event | New state |
|---|---|---|---|
| Bullish | active low broken down | **Bearish CHoCH** | Bearish |
| Bearish | active low broken down | **Bearish BOS**   | Bearish |
| Bearish | active high broken up  | **Bullish CHoCH** | Bullish |
| Bullish | active high broken up  | **Bullish BOS**   | Bullish |

The breaking candle is the displacement candle by definition, is labelled `DISP`, and is the
entry candle. A candle cannot break both sides in one bar — bearish is evaluated first and
suppresses the bullish path. After a break the broken level is cleared, so a new swing must
confirm before another event of that type is possible.

Direction gate (spec §44): shorts only when the close is above the zone, longs only when the
close is below it. A bullish BOS above Fair Price prints a label but never trades.

---

## E. Active swing replacement

Only two floats matter: `actHigh` and `actLow`, each with a role tag (HH/LH, HL/LL) and the
bar index on which it was confirmed.

- HL1 confirms → `actLow = HL1`.
- HH2 confirms → `actHigh = HH2`. `actLow` is untouched.
- HL2 confirms → `actLow = HL2`. **HL1 is gone** — it is no longer stored anywhere and can
  never produce a CHoCH.
- HL3 confirms → `actLow = HL3`, HL2 gone.

There is no array of historical swings feeding the break logic, so old structure physically
cannot generate signals. The `Role-valid only` mode adds the extra requirement that the
replacement must carry the correct role for the current state; `Latest swing` accepts any
newer confirmed pivot, which keeps the level fresh when the market chops.

---

## F. Fair Price and the Fair Price zone

The Fair Price engine runs inside a `request.security` call on the 1-minute series:

- session bar 1 → arm
- session bar 2 → `fairPrice := source[1]`, i.e. the value of bar 1 *after it closed*
- session end → `fairPrice := na`

So Fair Price appears one 1-minute bar after the session opens and never changes for the rest
of that session. Each enabled session gets its own; the previous session's value is discarded.

The line is created on the bar Fair Price appears, drawn to `bar_index + 5` (configurable),
and its right edge is pushed forward one bar at a time while the session runs. Nothing is ever
drawn hundreds of bars into the future. Old sessions' drawings are pruned to the configured
history depth.

Zone = `FP ± distance`. Its only trading role: while `fpLower ≤ close ≤ fpUpper`, no new entry
may be created. Structure tracking continues normally inside the zone, and open trades run to
TP or SL untouched.

---

## G. TP / SL visualisation

One engine (`f_openViz` / `f_updViz` / `f_closeViz`), used by every trade:

- **Create** on the entry candle: risk box (entry↔SL), reward box (entry↔TP), entry/SL/TP
  lines, and an `LONG #n` / `SHORT #n` label.
- **Update** every bar while the trade is open: right edge moves to the current bar.
- **Close**: the right edge is set to `strategy.closedtrades.exit_bar_index()` — the actual
  exit candle — and an optional `TP` / `SL` label is added at the actual exit price. After
  that the objects are never modified again.

Because the freeze uses the broker emulator's own record, a box can never extend past the exit
candle, and a trade that lasts 3 bars produces a 3-bar box while one that lasts 50 bars
produces a 50-bar box. Each trade keeps its own objects; nothing is overwritten. The
`Trade drawings kept` input prunes the oldest trades' drawings so the 500-object ceiling is
never hit on long backtests.

---

## H. How repainting is prevented

- Pivots are consumed only after `pivotRight` confirmation bars, and the confirmation bar
  index is stored, so nothing acts on a swing before it was knowable.
- Fair Price uses `close[1]` of the 1-minute session-opening candle inside a
  `barmerge.lookahead_off` request — a closed candle only.
- Break detection uses the current bar's close (or wick), evaluated at bar close with
  `calc_on_every_tick = false`.
- No `security` lookahead, no negative `offset`, no `[-n]` references, no `varip`.
- Historical and real-time paths use the same code — the only difference is that in real time
  you wait for the bar to close, which is exactly what the backtest assumes.

Consequence to accept: a swing low that printed at 09:41 is only tradable from 09:43 with
`pivotRight = 2`. Lowering `pivotRight` speeds this up at the cost of noisier swings.

---

## I. TradingView data / backtest limitations

1. **Same-candle TP and SL.** When one candle's range contains both levels, 1-minute OHLC
   cannot say which came first. What the script does:
   - The **actual fill** is decided by TradingView's broker emulator. `use_bar_magnifier`
     is set to **false** in the declaration because it is a **Premium-only** feature —
     on lower plans it throws *"Upgrade to the Premium plan to access higher bar
     detalization"*. On Premium, flip it to `true` and the emulator resolves these bars
     from lower-timeframe data, which is the only real fix inside TradingView.
   - The **reported reason and the exit label** follow your `Same-candle TP/SL priority`
     setting: `SL first (conservative)` (default), `TP first`, `Bar direction heuristic`
     (up bar assumed open→low→high→close, down bar open→high→low→close), or
     `Lower timeframe check` (scans intrabar highs/lows via `request.security_lower_tf`).
   - Every ambiguous bar is counted in the debug panel. If that counter is a meaningful
     fraction of your trade count, the equity curve is not reliable — that is the honest
     reading, and the fix is Bar Magnifier or a tick-level test in NinjaTrader.
2. **Entry fill.** `process_orders_on_close` fills at the signal candle's close. Live, you
   would be filled a few ticks later. Budget ~1 tick of slippage per side plus commission when
   you judge the results; the script ships with commission at zero so you can set your own.
3. **Fair Price on non-1-minute charts.** The 1-minute request works on higher timeframes, but
   the strategy is designed for 1-minute execution. On a 5-minute chart the displacement
   candle is a 5-minute candle and the SL is much wider. Test on 1 minute.
4. **Seconds-based LTF option** requires a plan with seconds data; if the request errors,
   switch the priority setting away from `Lower timeframe check`.
5. **Drawing limits.** 500 boxes/lines/labels. History pruning is configurable; if you run a
   multi-year backtest the oldest trade drawings will disappear (the backtest results are
   unaffected).
6. **Futures continuous contracts** (`MNQ1!`) splice contracts at rollover; a session spanning
   a rollover can produce a spurious structure break. Spot-check rollover days.

---

## J. Judgment calls I had to make (verify these against your intent)

1. **Fair Price appears one 1-minute bar after the session opens.** Taking it at the first
   bar's close in real time would mean reading an unfinished candle. This is the
   non-repainting form of the rule.
2. **Structure resets when price crosses to the other side of the zone**, and above/below the
   zone seeds bullish/bearish state respectively. This is my reading of §8 + §15 + §42. It
   means a whipsaw across Fair Price wipes the active levels and you wait for fresh pivots.
3. **`Latest swing` is the default active-level mode.** A strict "only role-valid swings
   replace the active level" reading can leave a stale level active for a long time when the
   market chops. `Role-valid only` implements the strict reading if you prefer it.
4. **Setup validity is measured in bars from the *confirmation* bar of the active structure
   level**, since your entry is on the displacement candle itself and there is no pending
   order to expire. This is the only interpretation that has teeth.
5. **A broken level is consumed.** Without this, price sitting below a broken HL would fire a
   signal on every subsequent bar.
6. **One trade at a time by default.** The spec caps trades per day but never says whether
   trades may overlap; overlapping would also break the one-active-visualisation model.
   Toggle it off if you want concurrency (you will also need `pyramiding > 0`).
7. **`alertcondition()` is not available in strategy scripts**, so alerts use `alert()`.
   Create one alert on the strategy and pick *Any alert() function call*. If you want the
   dropdown-style alert list, the same file has to be shipped as an `indicator`.

---

## K. Test checklist (§60 mapping)

| Test | How to verify |
|---|---|
| 1, 3 | Panel `Active low` / `Active high` should show the newest HL/LH; the CHoCH label must sit on the candle that closes through it. |
| 2 | Watch `Active low` change as each new HL confirms; only the last value can trigger. |
| 4, 5 | After a CHoCH the panel `Structure state` flips; the next same-direction break is labelled BOS, not CHoCH. |
| 6 | Panel `Regime` reads *INSIDE zone (no new trades)* — no entry should appear. |
| 7 | Open trade must survive price re-entering the zone. |
| 8, 9 | Exit label sits on the exit candle; both boxes' right edges terminate there. |
| 10 | Set Sessions 1 and 2 on, max/day = 3, confirm a Session 2 entry after a Session 1 exit. |
| 11 | Panel `Trades today` shows `3 / 3` and no further entries appear that day. |
| 12, 13 | Toggle each filter off and confirm the trade list is byte-identical to filters-off baseline. |
| 14 | Both on: entries only where both conditions hold; cross-check with the panel rows. |
| 15 | Set `Setup validity` low (e.g. 5) and confirm stale levels stop producing signals. |
| 16, 17 | Compare box width to the bar count between entry and exit. |

Suggested first run: MNQ1!, 1-minute, `0930-1000` New York, zone 20, RR 1.5, pivots 3/2,
both filters OFF. Get the structure labels looking right before you judge the equity curve.

---

## L. Rejection diagnostics (`Label rejected setups`)

The debug panel is built under `barstate.islast`, so it **always describes the last bar of the
chart** — the live bar — no matter where you have scrolled. It is useless for asking "why was
there no entry on that candle three days ago". Use this instead.

Turn on **Debug & Alerts → Label rejected setups**. Every displacement candle that did not
become a trade gets a `×REASON` tag naming the *first* gate that blocked it, evaluated in the
same order as the entry logic:

| Tag | Meaning |
|---|---|
| `×SESSION` | outside every enabled session window |
| `×NO FP` | Fair Price not yet established for this session |
| `×IN ZONE` | close is inside the Fair Price zone (§6 / §43) |
| `×SIDE` | direction disagrees with the Fair Price side (§44) — a bullish break above Fair Price, or a bearish break below it, is labelled but never traded |
| `×DAY CAP` | max trades per day reached |
| `×SESS CAP` | max trades per session reached |
| `×IN TRADE` | a trade is already open and *one at a time* is on |
| `×EVT OFF` | CHoCH or BOS entries disabled for that event type |
| `×EMA` / `×VWAP` | the corresponding filter rejected it |
| `×RISK` | displacement candle too small — risk ≤ 0 or below the min stop distance |

A candle showing `BOS`/`CHOCH`/`DISP` but no trade will now always carry one of these tags.
If a candle has no tag and no trade, no structure break occurred there at all — whatever you
are looking at is a TradingView native order marker or a hover artefact, not a signal.

---

## M. Extended-move TP override (group 4b)

When price has stretched a long way from Fair Price, a fixed 1.5R target may leave most of the
reversion on the table. This lets the next Y trades aim at the Fair Price line instead.

| Input | Meaning |
|---|---|
| Enable extended-move TP override | Master switch. Off by default — with it off, TP is always `risk × RR`, exactly as before. |
| Trigger: distance from Fair Price (%) | X, as a **percentage of Fair Price**. Default 0.5 → at FP 29,300 that is 146.5 points. |
| Y — trades to apply it to | How many entries the override arms. Default 1. |
| TP target while active | `Fair Price line, always` (default), `Nearer of the two`, `Farther of the two`. |
| TP offset from Fair Price | Pulls the target back toward the entry so you are not queued exactly on the line. Uses the same unit as the zone distance. Default 0. |

### Exact behaviour

- Distance is measured on the current bar's **close**: `|close − FP| / FP × 100`.
- The counter **refills to Y on every bar price is still beyond X%**, and is **cleared to 0 at
  every session start** (Fair Price changes there, so an old arming is meaningless).
  Consequence worth knowing: while price stays stretched, *every* qualifying trade gets the
  override — Y only starts biting once price comes back inside the trigger distance, at which
  point the next Y entries still use it. If you want strictly Y trades per stretch, say so and
  I will change the refill to a rising-edge trigger instead.
- The counter is decremented only when the override is **actually applied** to an entry, not
  merely when a trade is taken.
- `Nearer of the two` takes whichever target price the market reaches first; `Farther of the
  two` takes the more ambitious one. For a short, nearer = the higher price.
- **Sub-1R targets are taken as-is**, per your instruction. A short entered close to Fair Price
  can therefore run at well under 1R. That is the intended trade-off of targeting the line.
- The one case the override is skipped: the Fair Price target lands on the **wrong side of the
  entry** (at or beyond it in the trade's direction), which would be an instantly-filled or
  negative-reward order. That trade falls back to the RR target. This is mechanical, not a
  policy choice — it can only happen if your offset exceeds the zone distance.
- The override never changes **whether** a trade is taken, never touches the SL, and never
  re-targets a trade already open. Every existing gate still applies first.

### Verifying it

Trades using the override are labelled **`SHORT #n ·FP`** / **`LONG #n ·FP`** on the chart.
The debug panel gains an **FP-TP override** row showing the current distance from Fair Price
as a percentage and how many trades are currently armed.

Expect the win rate to *fall* and the average winner to *rise*. Compare total P&L and max
drawdown against the override off over the same range — a higher win rate here means nothing
on its own.

---

## N. Auditing an older date (the 500-object cap)

TradingView hard-caps every script at **500 labels, 500 lines and 500 boxes**. When the cap is
hit the *oldest* drawings are silently deleted. On 1-minute data this script draws a label on
every confirmed pivot, so the budget is exhausted in roughly a day and a half — which is why an
older date shows entry arrows but no HH/HL/CHoCH/BOS labels. The arrows survive because they
are TradingView's own order markers, not script drawings.

The backtest itself is completely unaffected. Logic, order list, trade list and equity curve
are identical whether an object was drawn or deleted.

### Drawing time window

| Input | Meaning |
|---|---|
| Restrict drawings to a time window | Off by default. Off = every bar may draw and the cap deletes the oldest. On = only bars whose **time of day** falls inside the window draw anything. |
| Draw only during (time of day) | `HHMM-HHMM`, evaluated in the **session timezone**, applied to every day. |

This is a recurring daily filter, not a date range. Set it to the hours you actually study —
typically your session windows plus a little either side — and the same 500 objects stretch
across many more days of history instead of being consumed by overnight bars that can never
produce a trade.

The cap still exists: a narrower window buys proportionally more days, it does not remove the
limit. If you need one specific old day rendered in full, narrow the window hard (e.g. the
30 minutes of Session 1 only) and the history will reach back far enough.

**Standing saving:** **Label swings inside sessions only** (on by default) suppresses pivot
labels outside your session windows. Those pivots can never produce a trade, and they were the
bulk of the label spend.

If you still run out, the remaining levers are: turn off `Show HH/HL/LH/LL` (the event and
broken-level drawings tell you everything the entry depended on), and lower
`Trade drawings kept`.

---

## O. Deferred to the NinjaTrader 8 build — news-day Fair Price

**Not implemented in the Pine MVP.** Pine Script has no access to an economic calendar —
TradingView exposes no event feed, impact rating or release schedule to scripts — so this
cannot be built here without you hand-feeding every news timestamp. Deferred to NT8, where a
calendar feed is available.

### Decided so far

- **Trigger:** high-impact (red) news released within the 1 hour **before** a session opens.
- **Fair Price on such a session:** the **OPEN of the news candle**, replacing the normal
  "close of the first 1-minute session candle".
- **Scope:** that session only. Other sessions the same day use the normal rule.
- **Toggle:** the whole behaviour must be switchable on/off.

### Still to pin down before coding it

1. **Which candle exactly** — the 1-minute candle whose open timestamp equals the release
   time, on the primary chart series?
2. **Multiple releases in the hour** — first one, last one, or highest impact?
3. **Lookback length** — is the 1 hour fixed, or a user input?
4. **Consequence worth noting:** the news candle sits *before* the session opens, so Fair Price
   would be known in advance and would be drawn from outside the session window. That is a
   departure from the current model, where Fair Price cannot exist until the session's first
   candle has closed. It is not a problem, but the drawing and session-reset logic both need
   adjusting for it.
5. **Fallback** — if the feed reports no news, does the session fall back to the normal
   first-candle-close rule? (Assumed yes.)
