# Backtest results — MNQ 2026

## Status: NO BACKTEST HAS BEEN RUN

FairPriceMeanReversion is **inspected, compiled and deployed**. It has not been
backtested, because the NinjaTrader 8 Strategy Analyzer could not be driven from
the assistant's environment:

- NinjaTrader 8 is a WPF desktop application. It has **no headless mode and no
  backtest CLI**.
- Its external API (ATI / NTDirect) handles live order routing only. It cannot
  launch or read a Strategy Analyzer run.
- Desktop UI-automation tooling *is* available, but NinjaTrader is granted at a
  **read-only tier**: the assistant can take screenshots and read the Analyzer's
  settings, results grid and log, but cannot click or type into it. The tier is
  assigned per application by the automation host because the same process can
  route live orders; it is not a local setting and cannot be raised for a
  session. So the assistant can verify a configuration and read a completed run,
  but a human has to press Run.

Every performance field is therefore `null` and every `summary.json` carries
`"status": "PENDING_RUN"`. Nothing has been estimated, simulated or inferred.

Running the backtest is a manual step. Everything on either side of it —
inspection, compilation, configuration, import, comparison, ranking — is done or
automated.

---

## What HAS been verified

**Compilation** — compiles clean (exit 0) using the Microsoft C#
compiler against `NinjaTrader.Core.dll`, `NinjaTrader.Gui.dll` and
`NinjaTrader.Custom.dll`. NinjaTrader's own NinjaScript compiler (F5) remains
authoritative and has not been run.

| Strategy | Files | Result |
|---|---|---|
| FairPriceMeanReversion | 23 | exit 0 |

**Historical data** — read directly from
`Documents/NinjaTrader 8/db/minute/`:

| Contract | 1-minute data |
|---|---|
| MNQ 03-26 | 2025-12-11 → 2026-03-20 |
| MNQ 06-26 | 2026-03-12 → 2026-06-18 |
| MNQ 09-26 | 2026-06-12 → 2026-09-02 |
| MNQ 12-26 | none |

**200 distinct trading days with 1-minute data, 2026-01-01 → 2026-09-02.**
Sufficient for the requested range. 2026-12-31 has not occurred, so the end date
is the latest available data.

**Unit tests** (assistant-run, not NinjaTrader):
- DST rebasing, summer and winter — 14/14 assertions pass

---

## How to run the backtest

### 1. Compile in NinjaTrader

Open the NinjaScript Editor and press **F5**. The strategy is already deployed to
`Documents\NinjaTrader 8\bin\Custom\Strategies\`.

### 2. Configure the Strategy Analyzer

| Setting | Value |
|---|---|
| Instrument | **MNQ** (the continuous symbol, not a specific contract) |
| Bars type / value | **Minute / 1** |
| **Trading Hours** | **CME US Index Futures ETH** |
| From / To | **2026-01-01** → **2026-09-02** |
| Order Fill Resolution | **Standard** — see the tick-coverage note below |
| Slippage | 0 (or your own figure — record it) |

**Local tick history is incomplete, so tick-precision fills are not viable over
the full window.** `db/tick/` holds 149 of the 200 trading days: March 2026 stops
at the 14th, and April and June 2026 have one day each. Strategy 1 ships with
`Use tick precision = true`, which forces `OrderFillResolutionType = Tick`; over
the missing spans that is unreliable. Set **`Use tick precision = false`** and
leave Order Fill Resolution at **Standard** to get all 200 days. Same-bar TP/SL
is then resolved by `SameBarPriority = SlFirst` — the stop is assumed to hit
first — so reported net profit is a conservative floor, not a midpoint. If you
would rather have exact intrabar fills, leave tick precision on and run only
2026-01-01 → 2026-03-14 and 2026-06-30 → 2026-09-02, then merge the two results.

Set the **global** merge policy once, in
*Tools → Options → Market Data → Merge policy* → **Merge Non Back Adjusted**.
Then use the plain `MNQ` symbol; NinjaTrader splices the contracts itself. Do not
select `MNQ 03-26` / `06-26` / `09-26` individually.

**Trading Hours is the setting most likely to produce a silently empty run.**
An RTH-only template supplies no bars outside US cash hours, so a session window
that falls there never produces a setup.

### 3. Leave the strategy parameters at their defaults

The defaults are recorded in `config.json` under `parameters_as_run`. If you
change any, update that file so the run stays reproducible.

### 4. Run, then export

After the run completes:

- **Trades** tab → right-click → *Export* → save as
  `backtest_results/raw/strategy_1_trades.csv`
- **Summary** tab → right-click → *Export* → save as
  `backtest_results/raw/strategy_1_summary.csv`

### 5. Import

```
python backtest_results/scripts/import_nt8_results.py
```

This fills in `summary.json` and `trades.csv`, flips the status to
`COMPLETED`, and builds `comparison/comparison.{json,csv,md}`. Any
metric NinjaTrader did not export stays `null` rather than being guessed at.

---

## Strategy index

| N | NinjaTrader name | Source |
|---|---|---|
| 1 | `FairPriceMeanReversion` | `src/NinjaTrader8/Strategies/` |

Full logic breakdown is in its `config.json`.

---

## Directory layout

```
backtest_results/
├── README.md                  this file
├── scaffold.py                regenerates the config/summary skeletons
├── gen_logs.py                regenerates changes.md and errors.log
├── raw/                       <- drop NinjaTrader exports here
├── scripts/
│   └── import_nt8_results.py  raw exports -> summary.json + trades.csv + comparison
├── 2026/
│   └── strategy_1/  config.json  summary.json  trades.csv  errors.log  changes.md
├── comparison/                built by the importer
└── logs/
    └── execution.log
```
