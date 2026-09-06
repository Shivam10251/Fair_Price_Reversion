import mt5_mac as mt5

print("Starting MT5...")

if not mt5.initialize():
    print("❌ MT5 initialization failed")
    print("Error:", mt5.last_error())
    quit()

print("✅ MT5 connected!")

print("\nAccount information:")
account = mt5.account_info()
print(account)

print("\nGetting symbols...")
symbols = mt5.symbols_get()

if symbols is None:
    print("❌ Could not retrieve symbols")
    print("Error:", mt5.last_error())
else:
    print(f"✅ Found {len(symbols)} symbols")

    print("\nSearching for NAS100-related symbols...")

    for symbol in symbols:
        name = symbol.name.upper()

        if "NAS" in name or "USTEC" in name or "US100" in name:
            print("FOUND:", symbol.name)

mt5.shutdown()

