# MNQ Volume Profile Liquidity Sweep Strategy

Build a fully automated, backtestable trading strategy for **MNQ (Micro E-mini Nasdaq-100 futures)** using the supplied volume-based indicator.

The indicator provides five dynamic horizontal levels:

* **VAH** = Value Area High
* **VAL** = Value Area Low
* **RH** = Range High
* **RL** = Range Low
* **POC** = Point of Control

Use the actual levels produced by the supplied indicator. Do not independently recreate or approximate the indicator's calculations.

---

# 0. LEVEL LIFECYCLE — WHEN THE LEVELS ARE FIXED

This governs everything below it. The five levels are not live values that drift
during the day; they are fixed once per cycle and then held.

## The two session windows

Both are user-configurable clock windows, typed in a user-selected timezone:

* **Profile session** — the window whose volume builds **VAH**, **POC** and **VAL**.
* **Range session** — the window whose high and low become **RH** and **RL**.

## The freeze

**When the range session closes, all five levels become fixed and cannot move
until the next range session closes.**

At that instant the strategy takes VAH/POC/VAL from the most recently *completed*
profile session and RH/RL from the range that has just ended, and commits them as
one set. A profile session that closes later in the day updates a staging copy
only — it must not move VAH or VAL under a live setup.

Every sweep test, every target selection and every break-even line for the whole
trading window is therefore measured against exactly the numbers that existed the
moment the window opened.

## The trading window

Entries are permitted from the range-session close until a user-configured
**"no entries after"** clock time. There is deliberately no separate start time:
the floor is the range close, because that is when the level set becomes complete.

An already-open trade continues to be managed after the cutoff.

Before the first range close of a run there are no fixed levels, and no trade is
possible.

---

# 1. SHORT SETUPS

Short trades can originate from either:

* **VAH**
* **RH**

The strategy should look for a liquidity sweep at these high-side levels.

## Liquidity Sweep

A short setup occurs when price trades above VAH or RH, sweeps the liquidity around that level, and then rejects the level.

The strategy should recognize the supplied indicator's actual level values and candle behavior.

### Short Confirmation — The Red Rejection Candle

A short entry is confirmed by a candle that does **both** of the following:

1. It closes **RED** — `close < open`.
2. It closes **back below** the swept level — `close < VAH` or `close < RH`.

Both conditions are required. Neither alone is a confirmation:

* A red candle that still closes **above** the level is continuation, not rejection.
* A green candle closing just under the level is buyers holding it, not rejection.

The full sequence:

1. Price sweeps above VAH or RH — the candle's **high** trades through the level.
2. The sweep creates the liquidity event.
3. A candle closes red **and** back below that level.
4. Enter SHORT on the close of that candle.

### Which candle may confirm

**The sweep candle itself counts** when it satisfies both conditions. A candle
that wicks above VAH and closes red back inside is confirmed on the spot — do not
force a wait for the next candle.

Otherwise the swept level stays armed for a configurable number of following
candles, and the first of them to satisfy both conditions confirms the setup.

A candle **after** the sweep candle must additionally still have traded within a
configurable proximity band of the swept level, so that an unrelated red candle
later in the window cannot confirm the setup. The sweep candle traded through the
level by definition and is never proximity-tested.

There is only ONE confirmation method. Engulfing patterns are not used.

The important sequence is:

**High-side liquidity sweep → red candle closes back below the level → SHORT**

---

# 2. LONG SETUPS

Long trades can originate from either:

* **VAL**
* **RL**

The strategy should look for a liquidity sweep at these low-side levels.

## Liquidity Sweep

A long setup occurs when price trades below VAL or RL, sweeps the liquidity around that level, and then rejects the level.

### Long Confirmation — The Green Rejection Candle

The exact mirror of the short rule. A long entry is confirmed by a candle that
does **both** of the following:

1. It closes **GREEN** — `close > open`.
2. It closes **back above** the swept level — `close > VAL` or `close > RL`.

The full sequence:

1. Price sweeps below VAL or RL — the candle's **low** trades through the level.
2. The sweep creates the liquidity event.
3. A candle closes green **and** back above that level.
4. Enter LONG on the close of that candle.

### Which candle may confirm

**The sweep candle itself counts** when it satisfies both conditions. Otherwise
the level stays armed for a configurable number of following candles, and a
confirming candle after the sweep candle must still have traded within the
proximity band of the swept level.

There is only ONE confirmation method. Engulfing patterns are not used.

The important sequence is:

**Low-side liquidity sweep → green candle closes back above the level → LONG**

---

# 3. INITIAL STOP LOSS

The initial stop loss is based on the **liquidity-sweep candle**.

### SHORT

For a short trade:

**Initial SL = High of the liquidity-sweep candle**

### LONG

For a long trade:

**Initial SL = Low of the liquidity-sweep candle**

When the confirming rejection candle is NOT the sweep candle, the strategy must still identify the candle responsible for the actual liquidity sweep and use that candle's extreme for the initial SL.

Do not use the confirming candle's high/low unless that candle is also the actual sweep candle.

A configurable buffer may push the stop a set number of ticks beyond the sweep candle's extreme; zero means exactly the extreme.

---

# 4. DYNAMIC MAXIMUM-DISTANCE TAKE PROFIT

This is a critical part of the strategy.

**Do NOT hard-code the TP relationship as VAH→VAL or RH→RL.**

Instead, after an entry is generated, evaluate all appropriate opposite-side indicator levels and choose the target that provides the **maximum valid distance from the entry**.

## SHORT TP LOGIC

If a SHORT is entered from VAH or RH:

Evaluate the available lower-side levels:

* VAL
* RL

Calculate the distance from the short entry price to each valid lower-side target.

For example:

**Entry at VAH**

Potential targets:

* VAH → VAL
* VAH → RL

If:

`Distance(VAH → RL) > Distance(VAH → VAL)`

then:

**TP = RL**

Even though the trade originated at VAH, it is NOT required to target VAL.

Likewise, if a SHORT originates at RH:

Evaluate both VAL and RL and select whichever provides the **greater valid point distance**.

Therefore:

**SHORT TP = farthest valid lower-side level from the actual entry price.**

---

# 5. LONG TP LOGIC

If a LONG is entered from VAL or RL:

Evaluate the available upper-side indicator levels:

* VAH
* RH

Calculate the distance from the long entry price to each valid upper-side target.

For example:

**Entry at VAL**

Potential targets:

* VAL → VAH
* VAL → RH

If:

`Distance(VAL → RH) > Distance(VAL → VAH)`

then:

**TP = RH**

The same logic applies to a LONG originating at RL.

Therefore:

**LONG TP = farthest valid upper-side level from the actual entry price.**

---

# 6. GENERAL TP RULE

The strategy must dynamically determine the maximum TP distance.

### SHORT

Entry from:

* VAH OR RH

Potential TP:

* VAL
* RL

Choose:

**The lower-side level with the greatest positive distance from the actual entry price.**

### LONG

Entry from:

* VAL OR RL

Potential TP:

* VAH
* RH

Choose:

**The upper-side level with the greatest positive distance from the actual entry price.**

Do not use fixed points, ticks, dollars, or a fixed risk/reward ratio for the primary TP.

The indicator levels determine the TP.

---

# 7. BREAK-EVEN LOGIC

This is another critical part of the strategy.

The five indicator lines are:

* VAH
* RH
* POC
* VAL
* RL

The **line where the trade was entered is the starting/reference line**.

After the entry, look at the selected TP.

Identify every other indicator line that lies **between the entry price and the selected TP**.

If price touches **any one of those intermediate lines**, immediately move the stop loss to **break-even**.

## SHORT EXAMPLE

Suppose:

* Entry = VAH
* Selected TP = RL

Between VAH and RL there may be:

* POC
* VAL

If price moves downward and touches POC:

→ Move SL to entry price.

If POC is skipped and price first touches VAL:

→ Move SL to entry price.

Once break-even is activated:

**SL = exact original entry price**

Do not move the stop back to the original sweep-candle stop.

---

# 8. LONG BREAK-EVEN EXAMPLE

Suppose:

* Entry = VAL
* Selected TP = RH

Between VAL and RH there may be:

* POC
* VAH

If price touches POC:

→ Move SL to entry.

If POC is skipped and price first touches VAH:

→ Move SL to entry.

The trade then either:

* reaches the selected maximum-distance TP, or
* returns to entry and exits at break-even.

---

# 9. ONLY INTERMEDIATE LINES COUNT

The line used for entry does NOT trigger break-even.

The final TP line does NOT trigger break-even.

Only **other indicator lines physically located between entry and TP** are break-even triggers.

For example:

**Entry = VAH**

**TP = RL**

If POC and VAL are between VAH and RL:

* Touch POC → BE
* Touch VAL → BE
* Touch RL → TP

Do NOT move to break-even merely because price touches VAH again.

---

# 10. CANDLE / LEVEL INTERACTION

The strategy should evaluate whether price has actually interacted with the relevant indicator level.

A mere candle close near a level is not automatically a sweep.

For a HIGH-side liquidity sweep:

**High of candle must trade above the relevant VAH/RH level.**

For a LOW-side liquidity sweep:

**Low of candle must trade below the relevant VAL/RL level.**

The strategy must then determine whether the required rejection-candle confirmation exists — see section 11.

---

# 11. REJECTION CANDLE CONFIRMATION

There is exactly one confirmation rule. Implement it carefully.

### The rule

| Side | Swept level | Requires |
|---|---|---|
| SHORT | VAH or RH | `close < open` **and** `close < level` |
| LONG  | VAL or RL | `close > open` **and** `close > level` |

Both halves are mandatory. The colour proves the candle was rejected; closing back
through the level proves the rejection happened **at that level**.

### Timing

* The **sweep candle itself** may confirm, if it satisfies both conditions.
* Otherwise the swept level stays armed for a configurable number of following
  candles (the confirmation window). Zero means the sweep candle only; one means
  the sweep candle or the one immediately after it.
* When the window expires without a qualifying candle, the sweep is discarded and
  that liquidity event produces no trade.

### Locality

A confirming candle **after** the sweep candle must have traded within a
configurable proximity band of the swept level. Without this, any red candle
anywhere inside the confirmation window would confirm the setup.

The sweep candle is exempt: it traded through the level by definition.

Do not trigger a trade on a rejection candle unrelated to the swept indicator level.

### Not used

Engulfing patterns play no part in this strategy. The previous candle's body,
open and close are irrelevant to confirmation.

---

# 12. TRADE SEQUENCE

### SHORT

**VAH/RH liquidity sweep**

↓

**Red candle closes back below the swept level**

↓

**Enter SHORT**

↓

**Initial SL = sweep candle high**

↓

**Evaluate VAL and RL**

↓

**Select the farther valid lower-side level as TP**

↓

**Monitor intermediate indicator lines**

↓

**If any intermediate line is touched → SL moves to break-even**

↓

**TP reached → exit**

OR

**Price returns to entry after BE → exit at break-even**

---

### LONG

**VAL/RL liquidity sweep**

↓

**Green candle closes back above the swept level**

↓

**Enter LONG**

↓

**Initial SL = sweep candle low**

↓

**Evaluate VAH and RH**

↓

**Select the farther valid upper-side level as TP**

↓

**Monitor intermediate indicator lines**

↓

**If any intermediate line is touched → SL moves to break-even**

↓

**TP reached → exit**

OR

**Price returns to entry after BE → exit at break-even**

---

# 13. IMPORTANT EDGE CASES

Handle these cases explicitly:

### If there is no valid opposite-side target

Do not enter the trade if a valid TP cannot be determined.

### If two candidate targets are exactly the same distance

Use a deterministic tie-break rule and expose it clearly in the strategy settings.

### If an intermediate line is touched on the same candle as entry

Do not incorrectly trigger break-even before the trade is actually established.

### If the TP and intermediate level are touched on the same candle

Use conservative backtesting logic unless intrabar data is available. Do not assume a favorable execution order that cannot be proven from the available data.

### If the initial SL and a break-even trigger occur on the same candle

Respect the actual chronological order when intrabar information is available. Otherwise use conservative backtesting assumptions.

### Do not generate duplicate trades

The same liquidity-sweep event must not generate multiple entries.

---

# 14. INDICATOR INTEGRATION

Before writing the strategy:

1. Inspect the supplied indicator source code.
2. Identify exactly how it exposes:

   * VAH
   * VAL
   * RH
   * RL
   * POC
3. Determine whether these are plots, series, buffers, objects, or another platform-specific mechanism.
4. Use those actual outputs directly.
5. Do not recreate the indicator's volume-profile calculations unless absolutely necessary.

---

# 15. BACKTESTING

Make the strategy fully backtestable on MNQ.

### Timeframe

The strategy must run on either a **1 Minute** or a **5 Minute** primary data
series, chosen by the user, and must reject any other series with a clear error
rather than running against it.

The primary series IS the profile's resolution — the volume profile is built from
these bars — so the two timeframes legitimately produce different levels. The
choice is taken from the platform's own data-series setting rather than from a
separate strategy parameter, so the strategy and the chart can never disagree
about which bars built the levels.

### Configurable settings

* Contract quantity, or risk-based sizing with a hard cap
* Profile session window, range session window, and their timezone
* "No entries after" cutoff
* Long enabled/disabled, short enabled/disabled
* Confirmation window (candles after the sweep)
* Level proximity band
* Stop buffer beyond the sweep candle's extreme
* Minimum target distance
* Break-even on/off, and its offset
* Slippage
* Commission where supported

### Visual markers

Display clear markers for:

* The five fixed levels, drawn from the freeze point through the trading window
* Entry
* Initial SL
* Selected TP
* Intermediate break-even trigger
* Break-even activation
* Final exit

Each level must be drawn as **one line with one label per trading window**. A
level can be swept many times in a session, and a mark per sweep buries the chart
in repeated text — so sweeps are not individually labelled.

Visuals must be switchable off: per-bar drawing is the single largest cost in a
backtest.

### Diagnostics

A run that produces no trades must report WHERE it stopped — bars processed, bars
inside each session, levels published, sweeps detected, confirmations, entries,
and a count of each rejection reason. "Nothing happened" is not an acceptable
output.

---

# CORE STRATEGY RULE

The most important logic is:

**LEVELS ARE FIXED AT THE RANGE-SESSION CLOSE**

All five levels are committed when the range session ends and cannot move until
the next range close. Trading runs from that close to the user's cutoff time.

**HIGH-SIDE SWEEP → SHORT**

VAH or RH is swept → a RED candle closes back BELOW that level → SHORT.

**LOW-SIDE SWEEP → LONG**

VAL or RL is swept → a GREEN candle closes back ABOVE that level → LONG.

The sweep candle itself may be the confirming candle.

**TP IS DYNAMIC**

For SHORT: choose the **farthest valid lower-side level (VAL/RL)**.

For LONG: choose the **farthest valid upper-side level (VAH/RH)**.

**BREAK-EVEN IS BASED ON INTERMEDIATE LINES**

After entry, if ANY other indicator line between the entry line and selected TP is touched, move SL to the exact entry price.

Do not invent additional filters or change these rules.

If any part of the indicator's implementation or the liquidity-sweep definition is ambiguous, identify the ambiguity before making assumptions.
