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

# 1. SHORT SETUPS

Short trades can originate from either:

* **VAH**
* **RH**

The strategy should look for a liquidity sweep at these high-side levels.

## Liquidity Sweep

A short setup occurs when price trades above VAH or RH, sweeps the liquidity around that level, and then rejects the level.

The strategy should recognize the supplied indicator's actual level values and candle behavior.

### Short Confirmation — Two Valid Methods

A short entry can be triggered through either of these confirmation methods:

### Method A — Sweep + Rejection

1. Price sweeps above VAH or RH.
2. The sweep candle trades through the level.
3. The candle then rejects the level / closes back below the relevant level.
4. Enter SHORT according to the confirmed sweep.

### Method B — Sweep + Bearish Engulfing

1. Price sweeps above VAH or RH.
2. The sweep creates the liquidity event.
3. The following candle forms a **bearish engulfing candle** at/around the swept level.
4. Enter SHORT on confirmation of the bearish engulfing candle.

A bearish engulfing candle should be treated as a valid confirmation even if the preceding sweep candle itself did not provide the final rejection confirmation.

The important sequence is:

**High-side liquidity sweep → bearish confirmation → SHORT**

Do not require both Method A and Method B. Either valid confirmation method may trigger the trade.

---

# 2. LONG SETUPS

Long trades can originate from either:

* **VAL**
* **RL**

The strategy should look for a liquidity sweep at these low-side levels.

## Liquidity Sweep

A long setup occurs when price trades below VAL or RL, sweeps the liquidity around that level, and then rejects the level.

### Long Confirmation — Two Valid Methods

A long entry can be triggered through either:

### Method A — Sweep + Rejection

1. Price sweeps below VAL or RL.
2. The sweep candle trades through the level.
3. The candle then rejects the level / closes back above the relevant level.
4. Enter LONG according to the confirmed sweep.

### Method B — Sweep + Bullish Engulfing

1. Price sweeps below VAL or RL.
2. The sweep creates the liquidity event.
3. The following candle forms a **bullish engulfing candle** at/around the swept level.
4. Enter LONG on confirmation of the bullish engulfing candle.

The important sequence is:

**Low-side liquidity sweep → bullish confirmation → LONG**

Do not require both Method A and Method B. Either valid confirmation method may trigger the trade.

---

# 3. INITIAL STOP LOSS

The initial stop loss is based on the **liquidity-sweep candle**.

### SHORT

For a short trade:

**Initial SL = High of the liquidity-sweep candle**

### LONG

For a long trade:

**Initial SL = Low of the liquidity-sweep candle**

If the engulfing candle is the confirmation candle, the strategy should still identify the candle responsible for the actual liquidity sweep and use that candle's extreme for the initial SL.

Do not arbitrarily use the engulfing candle's high/low unless that candle is also the actual sweep candle.

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

The strategy must then determine whether the required rejection/engulfing confirmation exists.

---

# 11. ENGULFING CONFIRMATION

Implement engulfing confirmation carefully.

### Bearish Engulfing

For a short setup:

* Previous candle should be bullish (or otherwise satisfy the platform's standard bearish-engulfing definition).
* Current candle should be bearish.
* Current candle's body should engulf the previous candle's body according to the standard engulfing definition.
* The pattern must occur at/around the swept VAH or RH level.
* The liquidity sweep must have occurred before or as part of the setup.

Sequence:

**Sweep high → bearish engulfing at level → SHORT**

### Bullish Engulfing

For a long setup:

* Previous candle should be bearish (or otherwise satisfy the platform's standard bullish-engulfing definition).
* Current candle should be bullish.
* Current candle's body should engulf the previous candle's body according to the standard engulfing definition.
* The pattern must occur at/around the swept VAL or RL level.
* The liquidity sweep must have occurred before or as part of the setup.

Sequence:

**Sweep low → bullish engulfing at level → LONG**

Do not trigger an engulfing trade somewhere unrelated to the swept indicator level.

---

# 12. TRADE SEQUENCE

### SHORT

**VAH/RH liquidity sweep**

↓

**Rejection OR bearish engulfing confirmation**

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

**Rejection OR bullish engulfing confirmation**

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

Expose configurable settings for:

* Contract quantity
* Trading session
* Long enabled/disabled
* Short enabled/disabled
* Slippage
* Commission where supported

Display clear visual markers for:

* Liquidity sweep
* Entry
* Initial SL
* Selected TP
* Intermediate break-even trigger
* Break-even activation
* Final exit

---

# CORE STRATEGY RULE

The most important logic is:

**HIGH-SIDE SWEEP → SHORT**

VAH or RH is swept → bearish rejection OR bearish engulfing → SHORT.

**LOW-SIDE SWEEP → LONG**

VAL or RL is swept → bullish rejection OR bullish engulfing → LONG.

**TP IS DYNAMIC**

For SHORT: choose the **farthest valid lower-side level (VAL/RL)**.

For LONG: choose the **farthest valid upper-side level (VAH/RH)**.

**BREAK-EVEN IS BASED ON INTERMEDIATE LINES**

After entry, if ANY other indicator line between the entry line and selected TP is touched, move SL to the exact entry price.

Do not invent additional filters or change these rules.

If any part of the indicator's implementation or the liquidity-sweep definition is ambiguous, identify the ambiguity before making assumptions.
