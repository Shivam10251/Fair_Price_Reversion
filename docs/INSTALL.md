# Install and run

## Layout

```
src/NinjaTrader8/Strategies/
  FairPriceMeanReversion.cs           # lifecycle + per-bar orchestration
  FairPriceMeanReversion.Setup.cs     # Configure / DataLoaded / reconciliation
  FairPriceMeanReversion.Trading.cs   # entry gating, orders, exits, diagnostics
  FairPriceMeanReversion.Params.cs    # every user input
  FPMR/
    Core/       FpmrEnums, TimeZoneRegistry, SessionWindow, SessionManager,
                PivotDetector, StructureEngine, FairPriceEngine, RiskSizer,
                ExtendedTpEngine, SessionVwap, TradeRecord, RejectionReporter
    News/       NewsEvent, MiniJson, NewsCalendarLoader, NewsFairPriceResolver
    Rendering/  VisualEngine
```

The `FPMR` namespace holds no NinjaScript objects — just plain classes, most of
them with no NinjaTrader dependency at all (`StructureEngine`, `PivotDetector`,
`RiskSizer`, `SessionManager`, `NewsCalendarLoader` and `ExtendedTpEngine` are
pure C# and can be unit tested outside the platform).

## Deploy

NinjaTrader compiles every `.cs` file under `bin/Custom` recursively, so the
folder structure is preserved as-is.

```bash
./scripts/deploy_to_ninjatrader.sh
# or
NT8_CUSTOM="/path/to/NinjaTrader 8/bin/Custom" ./scripts/deploy_to_ninjatrader.sh
```

Then open the NinjaScript Editor in NinjaTrader and press **F5**.

Manual equivalent: copy `FairPriceMeanReversion*.cs` into
`Documents\NinjaTrader 8\bin\Custom\Strategies\` and the whole `FPMR` folder into
`Documents\NinjaTrader 8\bin\Custom\Strategies\FPMR\`.

## First run

1. MNQ, 1-minute chart.
2. `VerboseLogging = true`, `ShowStatePanel = true`, `ShowFullVisuals = true`.
3. Check the load line prints the expected tick size, point value and time zones.
4. Work through `BACKTEST_NOTES.md`.
5. Turn the visuals back off for real backtests — drawing is the slow part.

`UseTickPrecision = true` needs 1-tick history for the whole backtest range. If
you have not downloaded it, either download it (Tools → Historical Data) or set
`UseTickPrecision = false` and read `DESIGN_DECISIONS.md §2` for what you lose.

## News calendar file

Both formats are accepted; the parser detects which from the first character.

**JSON** — the ForexFactory weekly feed shape (`config/news_sample.json`):

```json
[ { "title": "Core CPI m/m", "country": "USD",
    "date": "2024-05-15T08:30:00-04:00", "impact": "High" } ]
```

The UTC offset in `date` is authoritative.

**CSV** — the ForexFactory download shape (`config/news_sample.csv`):

```csv
Title,Country,Date,Time,Impact,Forecast,Previous
"Core CPI m/m",USD,05-15-2024,8:30am,High,0.3%,0.4%
```

CSV rows carry **no** timezone, so `News file timezone` applies to all of them.

Column names are matched case-insensitively with aliases:

| Field | Accepted headers |
|---|---|
| title | `title`, `event`, `name`, `event name` |
| currency | `currency`, `country`, `ccy`, `curr` |
| impact | `impact`, `importance`, `impact level` |
| date | `date` |
| time | `time` |
| combined stamp | `datetime`, `date_time`, `timestamp`, `released`, `start` |

Impact values understood: `High`/`Medium`/`Low`, `3`/`2`/`1`, `red`/`orange`/`yellow`.
Rows with no clock time (`All Day`, `Tentative`, blank) are skipped and counted.

**If your export does not match either shape, send a sample rather than reshaping
it by hand** — the parser is easy to extend and guessing a column layout is how
you end up trading the wrong reference price.
