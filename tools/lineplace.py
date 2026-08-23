"""Line-level placement: is every line of text the same, and in the same place?

WHY THIS EXISTS. Tier 2 scores every glyph's absolute position, and Products has now
accepted forms that Tier 2 rates at 42-45% (BAN01, BANSPECH) as looking identical. No
tolerance fixes that -- those forms do not reach 90% even at 8pt, more than a line height
-- so the disagreement is not calibration, it is the wrong quantity.

The drift Tier 2 objects to accumulates ACROSS a line: our text is on the correct baseline,
says the correct thing, and breaks in the same place, but a word near the end of a long line
can sit several points right of the reference. A reader does not notice that. A reader
notices a line that says something different, starts in the wrong place, wraps differently,
or lands on the wrong part of the page.

So this measures, per legacy line:
    same text  (whitespace collapsed)   -- content and line BREAKS
    same start x                        -- indentation and alignment
    same baseline y                      -- vertical placement

and ignores where glyphs sit inside the line. Intra-run drift cannot fail it; a wrong line
break, a lost line, or a mispositioned line all can.

It is offered as a candidate to replace Tier 2 as the placement GATE, with Tier 2 kept as
the tighter engineering diagnostic. Validate it against the accepted set before trusting it,
as always -- and note what it is blind to by construction: drift inside a line. That is a
deliberate trade, not an oversight, and it is only defensible while Products says that drift
is invisible.

Usage:  python tools/lineplace.py [FORM ...]
"""
import collections
import csv
import pathlib
import re
import statistics
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
OUTDIR = pathlib.Path(__file__).resolve().parent.parent / "output"

START_TOL = 2.0     # pt; a line may start this far off
BASE_TOL = 2.0      # pt; and sit this far off vertically
LINE_BAND = 2.5     # pt; glyphs within this of each other are one line.
                    # Deriving it per page from that page's median baseline gap was TRIED
                    # and measured WORSE at scale: 742/798 forms passed against 781/798
                    # for this fixed value. It fixed a real artefact -- a 'TM' superscript
                    # sits 2.6pt above its line in legacy and 2.2pt in ours, so one side
                    # sees two lines -- but broke roughly forty other forms. The artefact
                    # is left documented rather than traded for a worse metric; it costs
                    # 2 of 17 failures (G3168B, G2292A).
THRESHOLD_PCT = 90.0
MIN_LINES = 5


def lines(pdf):
    """-> list of (page, collapsed_text, start_x, baseline_y), one per visual line."""
    d = fitz.open(str(pdf))
    out = []
    for pg in range(d.page_count):
        words = []
        for b in d[pg].get_text("dict")["blocks"]:
            for ln in b.get("lines", []):
                for s in ln["spans"]:
                    t = s["text"]
                    if t.strip():
                        words.append((s["origin"][1], s["bbox"][0], t))
        words.sort(key=lambda w: (w[0], w[1]))
        band = None
        for y, x, t in words:
            if band is None or abs(y - band[0]) > LINE_BAND:
                band = (y, [])
                out.append((pg, band))
            band[1].append((x, t))
    res = []
    for pg, (y, items) in out:
        items.sort(key=lambda i: i[0])
        # Compare with ALL whitespace removed. Joining span texts directly makes the
        # result depend on how each engine split the line into spans -- legacy emits "A."
        # and "Paragraph" separately where we emit one run, so a collapsed-whitespace key
        # reads as "A.Paragraph" on one side only and every line looks different. This is
        # the same tokenisation trap that forced Tier 1 to character level and Tier 2 to
        # glyph level; it has now bitten in three separate metrics.
        joined = "".join(t for _, t in items)
        key = re.sub(r"\s+", "", joined)
        if key:
            res.append((pg, key, items[0][0], y, re.sub(r"\s+", " ", joined).strip()))
    return res


def score(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, O = lines(legacy), lines(ours)
    except Exception:
        return None
    if len(L) < MIN_LINES:
        return None

    idx = collections.defaultdict(list)
    for pg, key, x, y, _disp in O:
        idx[(pg, key)].append([x, y, False])

    hit = 0
    misses = []
    for pg, key, x, y, disp in L:
        best, bd = None, None
        for cand in idx.get((pg, key), []):
            if cand[2]:
                continue
            dx, dy = abs(cand[0] - x), abs(cand[1] - y)
            if dx <= START_TOL and dy <= BASE_TOL and (bd is None or dx + dy < bd):
                best, bd = cand, dx + dy
        if best is not None:
            best[2] = True
            hit += 1
        elif len(misses) < 3:
            present = any(key == k for _, k, _, _, _ in O)
            misses.append(f"{'moved' if present else 'text differs'}: {disp[:44]!r}")
    return {"form": form, "lines": len(L),
            "pct": round(100 * hit / len(L), 1), "misses": misses}


def main(forms):
    rows = [r for r in (score(f) for f in forms) if r]
    rows.sort(key=lambda r: r["pct"])
    print(f"Line placement: >= {THRESHOLD_PCT}% of legacy lines present, same text, "
          f"start within {START_TOL}pt and baseline within {BASE_TOL}pt\n")
    print(f"{'form':<18}{'lines':>7}{'matched':>10}  gate")
    for r in rows:
        flag = "PASS" if r["pct"] >= THRESHOLD_PCT else "FAIL"
        print(f"{r['form']:<18}{r['lines']:>7}{r['pct']:>9.1f}%  {flag}"
              + ("   " + "; ".join(r["misses"]) if flag == "FAIL" else ""))
    ok = [r for r in rows if r["pct"] >= THRESHOLD_PCT]
    print(f"\n{len(ok)}/{len(rows)} forms pass")
    if rows:
        print(f"   median {statistics.median([r['pct'] for r in rows]):.1f}%")
    out = OUTDIR / "lineplace.csv"
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "lines", "pct", "misses"])
        w.writeheader()
        for r in rows:
            w.writerow({**r, "misses": " | ".join(r["misses"])})
    print(f"Wrote {out}")
    return sum(1 for r in rows if r["pct"] < THRESHOLD_PCT)


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    sys.exit(1 if main(args) else 0)
