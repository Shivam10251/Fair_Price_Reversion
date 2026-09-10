# -*- coding: utf-8 -*-
"""
Imports NinjaTrader 8 Strategy Analyzer exports into backtest_results/.

USAGE
    python import_nt8_results.py

It looks for exports dropped in backtest_results/raw/ named:

    raw/strategy_1_trades.csv      Strategy Analyzer -> Trades tab -> right-click -> Export
    raw/strategy_1_summary.csv     Strategy Analyzer -> Summary tab -> right-click -> Export

A missing pair leaves the strategy at PENDING_RUN. Nothing is invented: a
metric NinjaTrader did not export stays null.

It writes:
    2026/<strategy>/summary.json     performance + trade statistics
    2026/<strategy>/trades.csv       normalised trade list
    comparison/comparison.{json,csv,md}
"""
import csv, io, json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RAW  = os.path.join(ROOT, "raw")

STRATS = [("strategy_1", "FairPriceMeanReversion")]

# NinjaTrader's Summary grid labels -> our summary.json keys. NT localises and
# occasionally renames these, so matching is case-insensitive and punctuation
# insensitive, and anything unmatched is preserved under "raw_summary".
LABELS = {
    "totalnetprofit":        ("performance", "net_profit"),
    "netprofit":             ("performance", "net_profit"),
    "grossprofit":           ("performance", "gross_profit"),
    "grossloss":             ("performance", "gross_loss"),
    "profitfactor":          ("performance", "profit_factor"),
    "maxdrawdown":           ("performance", "max_drawdown"),
    "maxdrawdownstrategy":   ("performance", "max_drawdown"),
    "sharperatio":           ("performance", "sharpe_ratio"),
    "sortinoratio":          ("performance", "sortino_ratio"),
    "commission":            ("performance", "commission"),
    "totalcommission":       ("performance", "commission"),
    "totalslippage":         ("performance", "total_slippage"),
    "percentprofitable":     ("performance", "percent_profitable"),
    "avgtrade":              ("performance", "average_trade"),
    "averagetrade":          ("performance", "average_trade"),
    "avgwinningtrade":       ("performance", "average_winning_trade"),
    "avglosingtrade":        ("performance", "average_losing_trade"),
    "largestwinningtrade":   ("performance", "largest_winning_trade"),
    "largestlosingtrade":    ("performance", "largest_losing_trade"),
    "maxconsecutivewinners": ("performance", "max_consecutive_wins"),
    "maxconsecutivelosers":  ("performance", "max_consecutive_losses"),
    "totaltrades":           ("trades", "total"),
    "totaltrades#":          ("trades", "total"),
    "avgtimeinmarket":       ("trades", "average_trade_duration"),
    "averagetimeinmarket":   ("trades", "average_trade_duration"),
}


def norm(label):
    return re.sub(r"[^a-z0-9#]", "", (label or "").lower())


def num(text):
    """Parses NinjaTrader's formatted numbers. Returns None when not numeric."""
    if text is None:
        return None
    t = str(text).strip()
    if t in ("", "-", "n/a", "N/A"):
        return None

    neg = t.startswith("(") and t.endswith(")")
    if neg:
        t = t[1:-1]

    t = t.replace("$", "").replace(",", "").replace("%", "").strip()
    if t.startswith("-"):
        neg, t = True, t[1:]

    try:
        v = float(t)
    except ValueError:
        return None

    return -v if neg else v


def read_rows(path):
    with io.open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        sample = fh.read(4096)
        fh.seek(0)
        try:
            dialect = csv.Sniffer().sniff(sample, delimiters=",;\t")
        except csv.Error:
            dialect = csv.excel
        return [r for r in csv.reader(fh, dialect) if any(c.strip() for c in r)]


def parse_summary(path):
    """NT exports the Summary tab as label/value pairs, sometimes two per row."""
    out = {"performance": {}, "trades": {}}
    raw = {}

    for row in read_rows(path):
        cells = [c.strip() for c in row]
        for i in range(0, len(cells) - 1):
            key, val = norm(cells[i]), cells[i + 1]
            if not key:
                continue
            if key in LABELS and val:
                section, field = LABELS[key]
                v = num(val)
                out[section][field] = v if v is not None else val
            elif val:
                raw.setdefault(cells[i], val)

    out["raw_summary"] = raw
    return out


def parse_trades(path):
    rows = read_rows(path)
    if not rows:
        return [], []
    return rows[0], rows[1:]


def trade_stats(header, rows):
    """Derives long/short and win/loss counts from the Trades grid."""
    hdr = [norm(h) for h in header]

    def col(*names):
        for n in names:
            if n in hdr:
                return hdr.index(n)
        return None

    i_dir  = col("markETposition", "marketposition", "position", "instrumentdirection", "direction")
    i_pnl  = col("profit", "pnl", "netprofit", "profitcurrency", "realizedpl")

    stats = {"total": len(rows), "long": 0, "short": 0, "winning": 0, "losing": 0,
             "winning_long": 0, "winning_short": 0, "losing_long": 0, "losing_short": 0}

    if i_dir is None and i_pnl is None:
        stats["note"] = "Direction and P&L columns not recognised in the export; counts beyond 'total' are unavailable."
        return stats

    for r in rows:
        d = (r[i_dir].strip().lower() if i_dir is not None and i_dir < len(r) else "")
        p = (num(r[i_pnl]) if i_pnl is not None and i_pnl < len(r) else None)

        is_long  = d.startswith("long")
        is_short = d.startswith("short")
        if is_long:  stats["long"] += 1
        if is_short: stats["short"] += 1

        if p is None:
            continue
        if p > 0:
            stats["winning"] += 1
            if is_long:  stats["winning_long"] += 1
            if is_short: stats["winning_short"] += 1
        elif p < 0:
            stats["losing"] += 1
            if is_long:  stats["losing_long"] += 1
            if is_short: stats["losing_short"] += 1

    return stats


def main():
    if not os.path.isdir(RAW):
        os.makedirs(RAW)

    imported, pending = [], []

    for key, nt_name in STRATS:
        base = os.path.join(ROOT, "2026", key)
        s_path = os.path.join(RAW, key + "_summary.csv")
        t_path = os.path.join(RAW, key + "_trades.csv")

        if not os.path.isfile(s_path) and not os.path.isfile(t_path):
            pending.append(key)
            continue

        with io.open(os.path.join(base, "summary.json"), "r", encoding="utf-8") as fh:
            summary = json.load(fh)

        if os.path.isfile(s_path):
            parsed = parse_summary(s_path)
            summary["performance"].update(parsed["performance"])
            summary["trades"].update(parsed["trades"])
            summary["raw_summary"] = parsed["raw_summary"]

        if os.path.isfile(t_path):
            header, rows = parse_trades(t_path)
            summary["trades"].update(trade_stats(header, rows))

            with io.open(os.path.join(base, "trades.csv"), "w", encoding="utf-8", newline="") as fh:
                wtr = csv.writer(fh)
                wtr.writerow(header)
                wtr.writerows(rows)

        summary["status"] = "COMPLETED"
        summary["status_reason"] = "Imported from NinjaTrader 8 Strategy Analyzer export."
        summary.pop("status_reason", None) if False else None

        with io.open(os.path.join(base, "summary.json"), "w", encoding="utf-8") as fh:
            fh.write(json.dumps(summary, indent=2) + "\n")

        imported.append(key)

    build_comparison(imported, pending)

    print("imported: %s" % (", ".join(imported) or "none"))
    print("pending : %s" % (", ".join(pending) or "none"))
    if pending:
        print("\nDrop the missing exports into %s and re-run." % RAW)


def build_comparison(imported, pending):
    cmp_dir = os.path.join(ROOT, "comparison")
    if not os.path.isdir(cmp_dir):
        os.makedirs(cmp_dir)

    cols = ["strategy", "total_trades", "net_profit", "profit_factor", "max_drawdown",
            "win_rate", "average_trade", "long_trades", "short_trades",
            "commission", "sharpe", "sortino", "status"]

    rows = []
    for key, nt_name in STRATS:
        p = os.path.join(ROOT, "2026", key, "summary.json")
        with io.open(p, "r", encoding="utf-8") as fh:
            s = json.load(fh)
        perf, tr = s.get("performance", {}), s.get("trades", {})
        rows.append({
            "strategy": nt_name,
            "total_trades": tr.get("total"),
            "net_profit": perf.get("net_profit"),
            "profit_factor": perf.get("profit_factor"),
            "max_drawdown": perf.get("max_drawdown"),
            "win_rate": perf.get("percent_profitable"),
            "average_trade": perf.get("average_trade"),
            "long_trades": tr.get("long"),
            "short_trades": tr.get("short"),
            "commission": perf.get("commission"),
            "sharpe": perf.get("sharpe_ratio"),
            "sortino": perf.get("sortino_ratio"),
            "status": s.get("status", "PENDING_RUN"),
        })

    with io.open(os.path.join(cmp_dir, "comparison.json"), "w", encoding="utf-8") as fh:
        fh.write(json.dumps({"rows": rows,
                             "imported": imported,
                             "pending": pending}, indent=2) + "\n")

    with io.open(os.path.join(cmp_dir, "comparison.csv"), "w", encoding="utf-8", newline="") as fh:
        wtr = csv.DictWriter(fh, fieldnames=cols)
        wtr.writeheader()
        wtr.writerows(rows)

    def cell(v):
        return "-" if v is None else (("%.2f" % v) if isinstance(v, float) else str(v))

    md = ["# Strategy comparison - MNQ 2026", "",
          "Values below come directly from the NinjaTrader 8 Strategy Analyzer export.",
          "A dash means NinjaTrader did not report that metric, or the run is still pending.", "",
          "| " + " | ".join(c.replace("_", " ").title() for c in cols) + " |",
          "|" + "|".join(["---"] * len(cols)) + "|"]
    for r in rows:
        md.append("| " + " | ".join(cell(r[c]) for c in cols) + " |")

    if pending:
        md += ["", "**Pending:** " + ", ".join(pending) + " - no export found, so no results are shown.",
               "No ranking is produced while any strategy is pending."]

    with io.open(os.path.join(cmp_dir, "comparison.md"), "w", encoding="utf-8") as fh:
        fh.write("\n".join(md) + "\n")


if __name__ == "__main__":
    main()
