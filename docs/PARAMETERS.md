# Complete parameter list

Defaults are the shipping defaults in `ApplyDefaults()`. Everything is
user-editable from the strategy dialog.

## 1 · Sessions

| Parameter | Default | Meaning |
|---|---|---|
| Session timezone | `America/New_York` | Zone all session windows and the news file are evaluated in. IANA or Windows id; an unresolvable value stops the strategy. |
| Bar timezone override | *(blank)* | Blank = assume bar timestamps are in the data series' trading-hours zone. Set it if your install shows bar times in a different zone. |
| Session 1 enabled | `true` | |
| Session 1 window | `0930-1000` | `HHMM-HHMM`, end exclusive. Windows crossing midnight are supported. |
| Session 2 enabled | `false` | |
| Session 2 window | `1030-1130` | |
| Session 3 enabled | `false` | |
| Session 3 window | `1400-1500` | Overlapping windows resolve to the lowest-numbered enabled session. |

## 2 · Fair Price

| Parameter | Default | Meaning |
|---|---|---|
| Fair Price source | `Close` | Which price of the session's first reference candle becomes Fair Price. |
| Reference candle minutes | `1` | Timeframe the opening candle is read from, independent of the chart timeframe. |
| Zone distance unit | `Points` | Unit for the zone distance and the extended-TP offset. |
| Zone distance | `20` | Half-width of the no-new-entry zone around Fair Price. |
| Line forward extension (bars) | `5` | How far past the current bar the Fair Price line is drawn. |
| Fair Price drawings kept | `5` | Older sessions' Fair Price drawings are pruned beyond this. |

## 3 · Market Structure

| Parameter | Default | Meaning |
|---|---|---|
| Pivot left bars | `3` | Bars required to the left of a swing. |
| Pivot right bars | `2` | Confirmation delay. A swing is only usable this many bars after it printed — this is what prevents repainting. |
| Break confirmation | `Close` | `Close`: the candle must close through the level. `Wick`: a wick through it is enough. |
| Active level update mode | `LatestSwing` | `LatestSwing`: the newest confirmed pivot always becomes the active level. `RoleValidOnly`: only a role-correct swing may replace it. |

## 4 · Trade Management

| Parameter | Default | Meaning |
|---|---|---|
| Risk / Reward ratio | `1.5` | TP = entry ± risk × RR, unless the extended-move override fires. |
| Max trades per DAY | `3` | 0 = unlimited. Day boundary is in the session timezone. |
| Max trades per SESSION | `0` | 0 = unlimited. |
| Setup validity (bars) | `30` | Bars from the *confirmation* bar of the active level. 0 = never expires. |
| Only one open trade at a time | `true` | |
| Max concurrent entries per direction | `3` | Used only when the above is off. NT8 needs an explicit ceiling where Pine used pyramiding. |
| Take CHoCH entries | `true` | |
| Take BOS entries | `true` | |
| SL buffer (ticks) | `0` | Extra ticks beyond the displacement candle's extreme. |
| Use fallback SL on tight candles | `false` | Replaces the displacement candle's stop with a fixed distance instead of skipping the trade. |
| Fallback when candle stop is under (points) | `5` | Measured entry → candle extreme, including the SL buffer. |
| Fallback SL distance (points) | `10` | The stop used instead. TP is this × the Risk/Reward ratio, so R:R is unchanged. |
| Close trades at session end | `false` | |
| Min bars before first trade | `3` | Warm-up after the trading start point. Structure and Fair Price still run; only entries are blocked. |
| Same-candle TP/SL report | `SlFirst` | Reporting only. The real fill comes from the order fill resolution. |

## 4b · Extended-move setup

Once price has stretched X% from Fair Price the move is treated as over-extended.
While the setup is **armed**, two things change:

1. **Only a BOS running back toward Fair Price may open a trade.** A CHoCH may not,
   and the *Take CHoCH / Take BOS* switches above do not apply — this rule replaces them.
2. That trade **targets Fair Price** rather than the RR multiple.

It stays armed through any number of trades, and ends only when price **closes on the
far side of Fair Price** (the reversion has happened), at session start, or if Fair
Price is lost. There is no trade counter.

With the feature off, entries and targets behave exactly as they do elsewhere.

| Parameter | Default | Meaning |
|---|---|---|
| Enable extended-move setup | `false` | |
| Trigger: distance from Fair Price (%) | `0.5` | Percentage of Fair Price. At FP 29,300 that is 146.5 points. |
| TP target while armed | `FairPriceAlways` | Or `NearerOfTheTwo` / `FartherOfTheTwo`, compared against the RR target. |
| TP offset from Fair Price | `0` | Same unit as the zone distance. Pulls the target back toward the entry. |

Rejection code `XTP BOS ONLY` means the setup was armed and the break was a CHoCH, or
a BOS running the wrong way.

## 5 · Risk Sizing *(new — not in the Pine version)*

| Parameter | Default | Meaning |
|---|---|---|
| Risk target ($) | `100` | The dollar risk each trade aims for. |
| Risk tolerance ($) | `20` | Reporting band (80–120 is "on target"). Does not change sizing. |
| Risk hard cap ($) | `150` | Never exceeded. If one contract risks more than this the trade is skipped with `RISK CAP`. |
| Max contracts | `10` | Upper bound on quantity. |

## 6 · News Fair Price *(new — not in the Pine version)*

| Parameter | Default | Meaning |
|---|---|---|
| Use news Fair Price | `false` | Master toggle. Off = no calendar is read and behaviour is identical to Pine. |
| News file path | *(blank)* | ForexFactory export, `.json` or `.csv`. Read once at startup. |
| News file timezone | `America/New_York` | Applied only to rows **without** an explicit UTC offset — i.e. every CSV row. FF's own default profile zone. |
| News CSV date format | *(blank)* | Blank = auto-detect. Set a .NET format (e.g. `MM-dd-yyyy`) if auto-detection guesses wrong. |
| News lookback (hours) | `1.0` | How far before session open a release still qualifies. |
| News impact filter | `HighOnly` | `HighOnly` / `MediumOnly` / `Both`. |
| News currency filter | `USD` | Comma separated (`USD,EUR`). Blank = every currency. |
| Multiple event rule | `First` | `First` / `Last` / `HighestImpact` when several qualify in the window. |
| Trading start on a news session | `AfterSessionOpen` | `AfterNewsCandle` allows trading from the news candle, before the session opens. |

## 7 · Filters

| Parameter | Default | Meaning |
|---|---|---|
| Use EMA filter | `false` | Long needs EMA1 > EMA2, short needs EMA1 < EMA2. Off = provably zero effect. |
| EMA 1 length | `9` | |
| EMA 2 length | `21` | |
| Use VWAP filter | `false` | Session-anchored. Long needs close above, short needs close below. Off = zero effect. |

## 8 · Visualisation

| Parameter | Default | Meaning |
|---|---|---|
| Show Fair Price line | `true` | |
| Show full visuals | `false` | Zone box, HH/HL/LH/LL labels, CHoCH/BOS/DISP marks, broken-level line, risk/reward boxes. |
| Trade drawings kept | `30` | Older trades' drawings are pruned beyond this. |

## 9 · Backtest Fidelity

| Parameter | Default | Meaning |
|---|---|---|
| Use tick precision | `true` | `OrderFillResolution.High` at 1 tick, so same-bar TP/SL comes from the real tick sequence. Slower; needs tick history. |

## 10 · Debug

| Parameter | Default | Meaning |
|---|---|---|
| Verbose logging | `false` | Logs every Fair Price change, entry, fill, exit and the first gate that blocked each displacement candle. |
| Mark rejected setups on chart | `false` | Draws `×REASON` on every displacement candle that did not trade. |
| Show state panel | `false` | On-chart dump: session, Fair Price, distance, structure state, active levels, trade counts, filter states. |

---

## Rejection reasons, in evaluation order

`UNRECONCILED` → `SESSION` → `NO FP` → `WARMUP` → `IN ZONE` → `SIDE` →
`DAY CAP` → `SESS CAP` → `IN TRADE` → `EVT OFF` → `EMA` → `VWAP` →
`RISK` → `RISK CAP`

Only the **first** failing gate is reported, so every displacement candle that did
not become a trade has exactly one answer.
