# PROMPT — Port the Fair Price Mean Reversion strategy to NinjaTrader 8

> Paste this together with the Pine Script file `FairPriceMeanReversion_MVP.pine`.

---

You are writing a **NinjaTrader 8 (NinjaScript, C#) strategy**. A working TradingView Pine
Script version of this strategy is attached. It has been tested and its logic is approved.

**Use the Pine file as the authoritative specification of the trading logic, not as something
to translate line by line.** Pine and NinjaScript differ fundamentally in execution model,
order handling and drawing. Write idiomatic NinjaScript. Where the two platforms disagree,
follow the *intent* described below and tell me what you changed and why.

Primary instrument: **MNQ (Micro E-mini Nasdaq-100)**, 1-minute chart. Do not hardcode tick
size, point value or margin — read them from `Instrument.MasterInstrument` so the strategy
works on other instruments.

---

## 1. What the strategy does

Fair Price mean reversion using market structure. There is **no** "first candle colour" entry
anywhere — the opening candle only establishes Fair Price. One trading engine only:

1. Each enabled session's first 1-minute candle sets **Fair Price** (default: its close).
2. A **zone** of ±N points around Fair Price is a **no-new-entry** area.
3. Above the zone the strategy hunts **shorts**; below it, **longs**. Never the reverse.
4. Market structure is tracked with confirmed swing pivots. Exactly **one active high and one
   active low** exist at any moment; a newer confirmed pivot replaces the older one.
5. A candle that **closes through** the active level is the **displacement candle**. It is a
   **CHoCH** if the break is against the current structure state, a **BOS** if with it.
6. Entry is at the **close of that displacement candle**. No confirmation candle.
7. **SL = the displacement candle's extreme** (high for shorts, low for longs).
8. **TP = risk × RR** (default 1.5), with an optional Fair-Price-targeting override.

Read the attached Pine file for the exact structure state machine, the active-level
replacement rules, setup invalidation, session handling and filter logic. Reproduce all of it.

---

## 2. Position sizing — this is new and is NOT in the Pine version

The Pine version trades a fixed 1 contract. The NT8 version must size each trade so the dollar
risk lands near a target.

```
riskPoints       = |entryPrice - stopPrice|
riskPerContract  = riskPoints * Instrument.MasterInstrument.PointValue
qty              = Round(RiskTargetUSD / riskPerContract)     // nearest
qty              = Max(qty, 1)
qty              = Min(qty, MaxContracts)
resultingRisk    = qty * riskPerContract

while (resultingRisk > RiskHardCapUSD && qty > 1)
    qty--                                                     // step down until within the cap

if (qty * riskPerContract > RiskHardCapUSD)                   // even 1 contract is too big
    skip the trade entirely, log the reason
```

Inputs, all user-editable:

| Input | Default | Meaning |
|---|---|---|
| `RiskTargetUSD` | 100 | The number we aim for. |
| `RiskToleranceUSD` | 20 | Acceptable band: 80–120 is "on target". Used for logging/reporting only. |
| `RiskHardCapUSD` | 150 | **Never exceeded.** If one contract risks more than this, the trade is skipped. |
| `MaxContracts` | 10 | Upper bound on size. |

Log every entry with: stop distance in points, risk per contract, chosen quantity, resulting
dollar risk, and whether it fell inside the tolerance band. Log every skip with the reason.

Note for you to verify and report: on MNQ the point value is $2, so a 10-point displacement
candle gives 5 contracts and a stop wider than 75 points is skipped. Confirm those numbers
against `PointValue` at runtime rather than assuming them.

---

## 3. News-day Fair Price — also new, not in the Pine version

**Rule.** If a qualifying economic news event occurs within **X hours before a session's open**,
then for that session:

- Fair Price = the **OPEN of the 1-minute candle in which the news was released**.
- The normal session-open Fair Price (close of the first session candle) is **discarded** for
  that session — not averaged, not used as a fallback.
- Applies to **that session only**. Other sessions the same day use the normal rule.
- The entire behaviour sits behind an on/off toggle.

**Data source.** NinjaScript has no API for future economic calendar events. NinjaTrader's own
support confirms real-time news subscription is provider-dependent and there is no built-in way
to fetch calendar events, so the only approach that works in **backtest** is a local file.

I will supply a **ForexFactory export in JSON or CSV**. Build the parser to accept both:

- Read from a user-configurable file path.
- Parse: event datetime, currency, impact, title.
- Handle the ForexFactory timezone convention explicitly and convert to the strategy's
  configured session timezone. State in comments exactly what you assumed about the file's
  timezone, and make it an input if there is any doubt.
- Load once in `State.Configure` or `State.DataLoaded`, never per bar.
- Fail loudly and safely: if the file is missing or unparseable, log an error and fall back to
  normal Fair Price behaviour rather than silently trading a wrong reference price.
- Ask me for a sample file if the format is ambiguous — do not guess the column layout.

**Filters:**

| Input | Default | Meaning |
|---|---|---|
| `UseNewsFairPrice` | false | Master toggle. |
| `NewsLookbackHours` | 1.0 | X — how far before session open to look. |
| `NewsImpactFilter` | High | **Three options: High only / Medium only / Both.** |
| `NewsCurrencyFilter` | USD | Which currencies qualify. |
| `NewsMultipleEventRule` | First | If several qualify in the window: First / Last / Highest impact. |

**Trading start on a news-Fair-Price session** — two options, user-selectable:

| Option | Behaviour |
|---|---|
| `AfterNewsCandle` | Trading may begin from the news candle onward, before the session even opens. |
| `AfterSessionOpen` (default) | Trading only begins once the session window opens, as normal. |

In **both** cases a warm-up applies: `MinBarsBeforeFirstTrade` (default 3, user-editable) bars
must elapse after the trading start point before any entry is allowed. Do not enter on the
first bar of the session. Structure tracking and Fair Price drawing may run during warm-up;
only *entries* are blocked.

---

## 4. NinjaTrader-specific requirements

**Execution**
- `Calculate = Calculate.OnBarClose` for signal generation, so entries occur at the close of the
  displacement candle, matching the tested Pine behaviour.
- Managed order approach. Submit the entry with the computed quantity, then set the bracket by
  price — `SetStopLoss` / `SetProfitTarget` with `CalculationMode.Price`, scoped to the entry
  signal name, called at the correct point in the order lifecycle. If you prefer explicit
  `ExitLongStopMarket` / `ExitLongLimit` orders, that is fine — say which you chose and why.
- `EntriesPerDirection = 1`, `EntryHandling = EntryHandling.AllEntries` unless the "one open
  trade at a time" toggle is off.
- Unique signal names per trade so multiple trades never collide.
- Handle `OnOrderUpdate` / `OnExecutionUpdate` for fill tracking. Do not assume a submitted
  order filled.
- Position and order state must be reconciled correctly on strategy restart and on
  `State.Realtime` transition.

**Backtest fidelity**
- Add a **1-tick secondary data series** and enable **Tick Replay** so same-bar TP/SL is
  resolved from actual tick sequence rather than assumed. This is the fix for the one ambiguity
  the TradingView version could not solve. Put it behind a toggle (`UseTickPrecision`, default
  true) since it slows backtests and requires tick data.
- Never look ahead. Pivots are confirmed only after `PivotRightBars` bars have closed, exactly
  as in the Pine version. Historical and real-time paths must produce identical signals.
- Guard `CurrentBar` against insufficient history.

**Sessions and time**
- The three session windows and their timezone must work independently of the machine's local
  timezone and the chart's session template. Use explicit `TimeZoneInfo` conversion, not
  `Time[0].Hour` on local time.
- Handle session boundaries, DST transitions, and days where a session does not exist.
- Respect `Bars.IsFirstBarOfSession` where relevant but do not rely on the instrument's session
  template to define the user's custom windows.

**Structure**
- Standard `State` machine: `SetDefaults`, `Configure`, `DataLoaded`, `Historical`, `Realtime`.
- Properties grouped with `[NinjaScriptProperty]`, `[Display(Name, GroupName, Order)]`, and
  `[Range]` where appropriate, mirroring the Pine input groups: Sessions, Fair Price, Market
  Structure, Trade Management, Risk Sizing, News, Extended-move TP, Filters, Visualisation,
  Debug.
- Compile clean on NinjaTrader 8.1+ with no external DLLs and no third-party indicators.

**Visuals** — default to minimal, full set behind a flag
- Default: entry/exit markers and the Fair Price line only.
- `ShowFullVisuals` (default false) enables the rest: Fair Price zone, HH/HL/LH/LL labels,
  CHoCH/BOS/DISP markers, the dotted broken-structure-level line, and per-trade risk/reward
  boxes that stop extending exactly on the exit bar.
- Use `Draw.*` with unique tags. Never redraw every object every bar. Remove drawing objects on
  `State.Terminated`.

**Diagnostics** — port these two from the Pine version, they matter
- **Rejection logging**: every displacement candle that did not become a trade must log the
  *first* gate that blocked it — SESSION, NO FP, IN ZONE, SIDE, DAY CAP, SESS CAP, IN TRADE,
  EVT OFF, EMA, VWAP, RISK, RISK CAP, WARMUP. Behind a `VerboseLogging` toggle.
- **State dump**: an optional on-chart panel or `Print()` line showing current session, Fair
  Price, distance from it, structure state, active high/low, trades today, trades this session.

---

## 5. Carry across from the Pine version unchanged

All of these are already specified and tested in the attached file — reproduce their behaviour
exactly:

- Three configurable sessions with independent enable flags and a shared timezone.
- Fair Price source selector, zone distance in points or ticks, no-new-entry zone rule, and the
  rule that an **open trade is never closed just because price re-enters the zone**.
- Pivot left/right bars, `Close` vs `Wick` break confirmation, and the two active-level update
  modes (`Latest swing` / `Role-valid only`).
- Setup validity expiry in bars, measured from the confirmation bar of the active level.
- Structure reset on session start and on price crossing to the other side of the zone.
- A broken level is consumed and cannot fire twice.
- Max trades per day, max trades per session, one-open-trade toggle, CHoCH/BOS entry toggles,
  SL buffer in ticks, minimum stop distance, close-at-session-end toggle.
- EMA filter (long: EMA1 > EMA2; short: EMA1 < EMA2) and session-anchored VWAP filter
  (long: above; short: below), each independently on/off, each with zero effect when off.
- The **extended-move TP override**: when price is X% away from Fair Price, the next Y trades
  target the Fair Price line instead of the RR multiple, with three target modes and an offset.
  Read its exact counter and refill semantics from the Pine file.

---

## 6. Deliverables

1. A single compile-ready `.cs` NinjaScript strategy file.
2. A short list of every design decision where NinjaTrader forced you to deviate from the Pine
   behaviour, and what the practical consequence is.
3. The complete parameter list with defaults and one-line explanations.
4. Notes on what needs verifying in the first backtest — specifically the sizing arithmetic
   against actual `PointValue`, and the news file's timezone handling.

---

## 7. Before you write code

Ask me about anything genuinely ambiguous. In particular I expect questions about the news file
format, the order-management approach, and any place where the Pine logic does not map cleanly
onto NinjaScript's order lifecycle. **Do not guess and do not silently simplify** — if a rule
cannot be reproduced faithfully in NinjaScript, say so plainly rather than shipping something
that approximates it.

Flag honestly anything that will make the backtest optimistic relative to live trading:
fill assumptions, slippage, the bar-close entry model, and the sizing behaviour on
wide-stop trades.

---

## 8. Test scenarios the finished strategy must pass

1. Price above Fair Price, bullish HH/HL structure, newer HL replaces older, price closes below
   the latest HL → bearish CHoCH + DISP + short entry at that close.
2. Three HLs form; only the newest can trigger.
3. Mirror of 1 below Fair Price → bullish CHoCH + long.
4. After a bearish CHoCH, break of the latest LL → bearish BOS + short.
5. After a bullish CHoCH, break of the latest HH → bullish BOS + long.
6. Price inside the Fair Price zone → no new trades.
7. An open trade survives price entering the zone.
8. TP hit → trade closes, visualisation terminates on that exact bar.
9. SL hit → same.
10. Session 1 trade closes; Session 2 setup is still allowed.
11. Daily trade limit reached → no further entries that day.
12. EMA filter off → zero effect on entries.
13. VWAP filter off → zero effect on entries.
14. Both filters on → all conditions must hold.
15. A setup becomes invalid → cancelled, strategy waits for a new one.
16. **Sizing:** a 10-point stop on MNQ produces 5 contracts (~$100). A 60-point stop produces
    1 contract (~$120, within the hard cap). An 80-point stop is **skipped** (one contract
    would risk $160 > $150).
17. **News:** with the toggle on, a High-impact USD event 40 minutes before session open sets
    Fair Price to that news candle's open, and the session-open Fair Price is never used.
18. **News + start mode:** `AfterSessionOpen` takes no trade before the session window opens,
    even though Fair Price already exists.
19. `MinBarsBeforeFirstTrade = 3` → no entry on the first three eligible bars.
20. Toggle the news module off → behaviour is byte-identical to the Pine version's.
