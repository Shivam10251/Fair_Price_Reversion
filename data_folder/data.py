import MetaTrader5 as mt5
import pandas as pd

if not mt5.initialize():
    print("MT5 initialization failed")
    quit()

symbol = "NAS100"

rates = mt5.copy_rates_range(
    symbol,
    mt5.TIMEFRAME_M1,
    pd.Timestamp("2021-09-01"),
    pd.Timestamp("2026-09-05")
)

df = pd.DataFrame(rates)

df["time"] = pd.to_datetime(df["time"], unit="s")

print(df.head())
print(df.tail())
print(len(df))

df.to_csv("NAS100_M1_5Y.csv", index=False)

mt5.shutdown()