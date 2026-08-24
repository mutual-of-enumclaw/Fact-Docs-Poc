"""Rule-by-rule comparison of the vectors each render actually draws.

WHY THIS EXISTS. `nontextink.py` rasterizes both renders, subtracts a text mask, and compares
pixels. That works for shading and artwork but is the wrong instrument for RULES, in two ways
that matter:

  * It rewards over-drawing on recall. Four box edges are guaranteed to cover legacy's one
    underline, so drawing a box where Documaker draws a rule scores 100%. Products caught
    exactly that on M7902AA by eye, after every gate had passed it.
  * Its precision half is contaminated. On G2032C the dilated text mask removes 98.9% of
    LEGACY's rule ink but only 72.9% of ours -- legacy's rules sit within a pixel of the text
    boxes and ours land 0.3pt clear of them -- so the form scores 4.6% precision while the
    renders are far closer than that. Chasing that tail would be chasing the mask.

So this reads the PDF vectors instead. No raster, no threshold, no text mask, nothing to tune.
Both renders are reduced to a canonical set of axis-aligned EDGES, and coverage is measured as
LENGTH in both directions:

    recall     how much of legacy's rule length we draw     -- catches dropped edges
    precision  how much of our rule length legacy draws     -- catches invented edges

Length rather than edge COUNT, because the two renders legitimately segment the same rule
differently: Documaker draws one filled bar per edge, Chromium emits a whole bordered box as a
single stroked path, and a grid's shared horizontal may be one long rect on one side and four
per-cell tops on the other. Coverage by length is blind to all of that and still exact about
what ink exists.

Excluded deliberately: filled blocks thicker than THIN_PT on both axes (that is shading, which
`nontextink.py` measures), white fills (the page ground), and curves (`M,I` bullets).

Usage:  python tools/vectorrules.py [FORM ... | @file-of-form-names]
"""
import csv
import pathlib
import statistics
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
OUTDIR = pathlib.Path(__file__).resolve().parent.parent / "output"

THIN_PT = 2.5       # a filled rect thinner than this on one axis is a RULE; thicker on both is shading
POS_TOL = 2.0       # pt; two edges this close on the cross axis are the same edge
MIN_LEN = 2.0       # pt; shorter than this is a corner artefact, not a rule
THRESHOLD_PCT = 90.0
MIN_RULE_LEN = 50.0  # pt of legacy rule; below this there is not enough to judge


def form_args(argv, fallback_csv):
    names = []
    for a in argv:
        if a.startswith("@"):
            names += pathlib.Path(a[1:]).read_text(encoding="utf-8").split()
        else:
            names.append(a)
    if names:
        return names
    if fallback_csv.exists():
        return [r["form"] for r in csv.DictReader(fallback_csv.open(encoding="utf-8"))
                if r["status"] == "ok"]
    return []


def _rect_edges(r, out, page):
    """The four sides of a rectangle, as centreline edges."""
    out.append((page, "H", (r.y0 + r.y1) / 2 if r.height <= THIN_PT else r.y0, r.x0, r.x1))
    if r.height > THIN_PT:
        out.append((page, "H", r.y1, r.x0, r.x1))
        out.append((page, "V", r.x0, r.y0, r.y1))
        out.append((page, "V", r.x1, r.y0, r.y1))


def edges(pdf):
    """-> [(page, 'H'|'V', cross_pos, lo, hi)] for every axis-aligned rule drawn."""
    d = fitz.open(str(pdf))
    out = []
    for page in range(d.page_count):
        for dr in d[page].get_drawings():
            filled = dr["type"] in ("f", "fs")
            stroked = dr["type"] in ("s", "fs")
            # The page ground, painted white by both renders, is not a rule.
            if filled and dr.get("fill") and min(dr["fill"]) > 0.9 and not stroked:
                continue
            for it in dr["items"]:
                if it[0] == "re":
                    r = it[1]
                    thin = min(r.width, r.height) <= THIN_PT
                    if filled and not stroked:
                        if not thin:
                            continue        # a filled block is shading, measured elsewhere
                        if r.width >= r.height:
                            out.append((page, "H", (r.y0 + r.y1) / 2, r.x0, r.x1))
                        else:
                            out.append((page, "V", (r.x0 + r.x1) / 2, r.y0, r.y1))
                    else:
                        _rect_edges(r, out, page)
                elif it[0] == "l":
                    (x0, y0), (x1, y1) = it[1], it[2]
                    if abs(y1 - y0) <= 0.6:
                        out.append((page, "H", (y0 + y1) / 2, min(x0, x1), max(x0, x1)))
                    elif abs(x1 - x0) <= 0.6:
                        out.append((page, "V", (x0 + x1) / 2, min(y0, y1), max(y0, y1)))
    d.close()
    return [e for e in out if e[4] - e[3] >= MIN_LEN]


def _merge(spans):
    out = []
    for lo, hi in sorted(spans):
        if out and lo <= out[-1][1]:
            out[-1][1] = max(out[-1][1], hi)
        else:
            out.append([lo, hi])
    return out


def coverage(a, b):
    """Fraction of a's total edge LENGTH that b also draws, and the uncovered pieces."""
    total = covered = 0.0
    gaps = []
    for page, orient, pos, lo, hi in a:
        total += hi - lo
        near = _merge([(x[3], x[4]) for x in b
                       if x[0] == page and x[1] == orient and abs(x[2] - pos) <= POS_TOL])
        got, cur = 0.0, lo
        for s, e in near:
            s, e = max(s, lo), min(e, hi)
            if e > s:
                got += e - s
                if s - cur > 1.0:
                    gaps.append((page, orient, pos, cur, s))
                cur = max(cur, e)
        if hi - cur > 1.0:
            gaps.append((page, orient, pos, cur, hi))
        covered += got
    return (100.0 if total == 0 else 100 * covered / total), total, gaps


def score(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, O = edges(legacy), edges(ours)
    except Exception:
        return None
    rec, ltot, missing = coverage(L, O)
    prec, otot, invented = coverage(O, L)
    if ltot < MIN_RULE_LEN and otot < MIN_RULE_LEN:
        return None

    def where(g):
        if not g:
            return ""
        p, o, pos, lo, hi = max(g, key=lambda x: x[4] - x[3])
        return f"p{p+1} {o}@{pos:.0f} {lo:.0f}-{hi:.0f} ({hi-lo:.0f}pt)"

    return {"form": form, "legacy_pt": round(ltot), "ours_pt": round(otot),
            "recall": round(rec, 1), "precision": round(prec, 1),
            "worst_missing": where(missing), "worst_invented": where(invented)}


def main(forms):
    rows = [r for r in (score(f) for f in forms) if r]
    print(f"Vector rules: coverage by LENGTH, both directions. Edge match within {POS_TOL}pt.\n")
    print(f"{'form':<18}{'legacy pt':>10}{'ours pt':>9}{'recall':>8}{'prec':>7}  worst invented")
    bad = [r for r in rows if min(r["recall"], r["precision"]) < THRESHOLD_PCT]
    for r in sorted(bad, key=lambda r: r["precision"]):
        print(f"{r['form']:<18}{r['legacy_pt']:>10}{r['ours_pt']:>9}{r['recall']:>7.1f}%"
              f"{r['precision']:>6.1f}%  {r['worst_invented'] or r['worst_missing']}")
    if rows:
        print(f"\n{len(rows) - len(bad)}/{len(rows)} forms at >= {THRESHOLD_PCT}% both ways  "
              f"(asked for {len(forms)}; {len(forms) - len(rows)} with too little rule to judge)")
        print(f"   recall    median {statistics.median([r['recall'] for r in rows]):.1f}%  "
              f"below {THRESHOLD_PCT}%: {sum(1 for r in rows if r['recall'] < THRESHOLD_PCT)}")
        print(f"   precision median {statistics.median([r['precision'] for r in rows]):.1f}%  "
              f"below {THRESHOLD_PCT}%: {sum(1 for r in rows if r['precision'] < THRESHOLD_PCT)}")
    out = OUTDIR / "vectorrules.csv"
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "legacy_pt", "ours_pt", "recall",
                                           "precision", "worst_missing", "worst_invented"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")
    return len(bad)


if __name__ == "__main__":
    sys.exit(1 if main(form_args(sys.argv[1:], SWEEP)) else 0)
