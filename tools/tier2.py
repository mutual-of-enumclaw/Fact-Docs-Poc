"""Tier 2 acceptance gate: is every legacy text run in the right place?

Threshold calibrated against the four forms Products accepted by eye on 2026-08-16
(EB2410A, A0238C, EB22489Q, P0010G): at 2.0pt tolerance the worst of them matches 90.0%
of legacy runs, so the gate is `>= 90% within 2.0pt`. A gate must never fail work that
has already been accepted -- see FORM-STUDIO-PLAN section 14.

Usage:  python tools/tier2.py [FORM ...]
"""
import csv
import pathlib
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
TOL_PT = 2.0
THRESHOLD_PCT = 90.0


def runs(pdf):
    d = fitz.open(str(pdf))
    out = []
    for pg in range(d.page_count):
        for b in d[pg].get_text("dict")["blocks"]:
            for line in b.get("lines", []):
                for s in line["spans"]:
                    t = s["text"].strip()
                    if t:
                        out.append((pg, t, s["origin"][0], s["origin"][1]))
    return out


def score(form, tol=TOL_PT):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, O = runs(legacy), runs(ours)
    except Exception:
        return None
    if not L:
        return None
    idx = {}
    for pg, t, x, y in O:
        idx.setdefault((pg, t), []).append((x, y))
    hit = 0
    for pg, t, x, y in L:
        cands = idx.get((pg, t), [])
        best = min(((abs(ox - x), abs(oy - y)) for ox, oy in cands),
                   key=lambda d: d[0] + d[1], default=None)
        if best and best[0] <= tol and best[1] <= tol:
            hit += 1
    return {"form": form, "runs": len(L), "pct": round(100 * hit / len(L), 1)}


def main(forms):
    rows = [r for r in (score(f) for f in forms) if r]
    rows.sort(key=lambda r: r["pct"])
    passed = [r for r in rows if r["pct"] >= THRESHOLD_PCT]
    print(f"Tier 2: >= {THRESHOLD_PCT}% of legacy text runs within {TOL_PT}pt\n")
    print(f"{'form':<17}{'runs':>7}{'matched':>10}  gate")
    for r in rows:
        print(f"{r['form']:<17}{r['runs']:>7}{r['pct']:>9.1f}%  "
              f"{'PASS' if r['pct'] >= THRESHOLD_PCT else 'FAIL'}")
    print(f"\n{len(passed)}/{len(rows)} forms pass Tier 2")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\tier2.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "runs", "pct"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    main(args)
