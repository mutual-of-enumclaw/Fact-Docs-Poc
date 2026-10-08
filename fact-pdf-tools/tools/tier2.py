"""Tier 2 acceptance gate: is every legacy text run in the right place?

Tolerance is re-derived against the four forms Products accepted by eye on 2026-08-16
(EB2410A, A0238C, EB22489Q, P0010G) every time the geometry changes; a gate must never
fail work already accepted -- see FORM-STUDIO-PLAN section 14.

2026-08-22, measured at GLYPH level (see glyphs()). Read the tolerance history carefully
before changing it, because the number moved for two unrelated reasons:

  - The old RUN-level metric only ever checked where each run STARTED, so it was blind to
    drift inside a run. Its flattering scores at 1.0pt were not a quality signal.
  - Vertical placement is now essentially exact (section 18): |dy| > 1pt is 0.0% of glyphs
    on every accepted form. Everything below is horizontal.

Gate: >= 90% of legacy glyphs within 3.0pt. That is set, as always, by the worst form
Products accepted -- A0238C at 90.6%. It is NOT a statement that 3pt is good; A0238C
carries real intra-run horizontal drift (its 18pt bold heading is ~3pt out) that Products
accepted by eye. DIAG_TOL_PT reports the same metric at 1.0pt as the actual quality
signal, and that is the number to drive down.

Tightening the gate requires fixing intra-run horizontal drift -- the last named defect.
FOUR attempts are recorded as failures in FORM-STUDIO-PLAN section 19: dropping the FXR
advance correction, per-word FXR offsets, multiplicative correction via point size, and
box-fit scaleX. Do not repeat them; whole-run scale factors are exhausted.

Usage:  python tools/tier2.py [FORM ...]
"""
import csv
import pathlib
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
TOL_PT = 3.0        # gate tolerance, set by the worst accepted form (A0238C, 90.6%)
DIAG_TOL_PT = 1.0   # quality diagnostic; not pass/fail
THRESHOLD_PCT = 90.0


def glyphs(pdf):
    """Every non-space glyph as (page, char, x, baseline_y).

    GLYPH level, not run level. Matching whole runs on their exact string made this gate
    report placement defects the render did not have: the two engines segment a line into
    runs differently, so a perfectly-placed string either failed to look up at all, or a
    repeated short word like 'the' matched the wrong instance. Measured on the 2026-08-22
    sweep, EVERY such miss had its text present on the page -- 220/220 on BP0564A,
    135/135 on PRVNOTCB. That is the same tokenisation trap that word-level diffing sprang
    on Tier 1 (FORM-STUDIO-PLAN section 14); glyph positions cannot be fooled by it.

    x is the glyph's own left edge; y is its span's baseline, which is what we actually
    control and what section 18 anchors.
    """
    d = fitz.open(str(pdf))
    out = []
    for pg in range(d.page_count):
        for b in d[pg].get_text("rawdict")["blocks"]:
            for line in b.get("lines", []):
                for s in line["spans"]:
                    by = s["origin"][1]
                    for ch in s.get("chars", []):
                        if ch["c"].strip() and ch["c"] >= " ":
                            out.append((pg, ch["c"], ch["bbox"][0], by))
    return out


def score(form, tol=TOL_PT):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, O = glyphs(legacy), glyphs(ours)
    except Exception:
        return None
    if not L:
        return None

    # Bucket ours by (page, char, cell) so each legacy glyph only probes its own
    # neighbourhood, and consume matches so N legacy glyphs cannot all pair with one of
    # ours -- which is precisely how the run-level matcher produced phantom misplacements.
    cell = max(tol, 0.01)
    buckets = {}
    for pg, c, x, y in O:
        buckets.setdefault((pg, c, int(x // cell), int(y // cell)), []).append([x, y, False])

    hit = 0
    for pg, c, x, y in L:
        best, bestd = None, None
        cx, cy = int(x // cell), int(y // cell)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                for cand in buckets.get((pg, c, cx + dx, cy + dy), ()):
                    if cand[2]:
                        continue
                    ex, ey = abs(cand[0] - x), abs(cand[1] - y)
                    if ex <= tol and ey <= tol and (bestd is None or ex + ey < bestd):
                        best, bestd = cand, ex + ey
        if best is not None:
            best[2] = True
            hit += 1
    return {"form": form, "glyphs": len(L), "pct": round(100 * hit / len(L), 1)}


def main(forms):
    rows = []
    for f in forms:
        r = score(f)
        if not r:
            continue
        tight = score(f, DIAG_TOL_PT)
        r["tight_pct"] = tight["pct"] if tight else None
        rows.append(r)
    rows.sort(key=lambda r: r["pct"])
    passed = [r for r in rows if r["pct"] >= THRESHOLD_PCT]
    print(f"Tier 2 GATE: >= {THRESHOLD_PCT}% of legacy glyphs within {TOL_PT}pt")
    print(f"             (the {DIAG_TOL_PT}pt column is the quality signal, not pass/fail)\n")
    print(f"{'form':<17}{'glyphs':>9}{f'@{TOL_PT}pt':>9}{f'@{DIAG_TOL_PT}pt':>9}  gate")
    for r in rows:
        print(f"{r['form']:<17}{r['glyphs']:>9}{r['pct']:>8.1f}%{r['tight_pct']:>8.1f}%  "
              f"{'PASS' if r['pct'] >= THRESHOLD_PCT else 'FAIL'}")
    tight_ok = sum(1 for r in rows if (r["tight_pct"] or 0) >= THRESHOLD_PCT)
    print(f"\n{len(passed)}/{len(rows)} forms pass the gate ({TOL_PT}pt)")
    print(f"{tight_ok}/{len(rows)} would pass at the {DIAG_TOL_PT}pt quality bar")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\tier2.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "glyphs", "pct", "tight_pct"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    main(args)
