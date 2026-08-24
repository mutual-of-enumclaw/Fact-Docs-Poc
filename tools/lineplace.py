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

Usage:  python tools/lineplace.py [FORM ... | @file-of-form-names]
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
COL_GAP = 72.0      # pt (one inch); a horizontal gap this large inside a baseline band is a COLUMN
                    # break, not a word space. Without it a two-column page merges a
                    # left-column line with a right-column line whenever their baselines
                    # land within LINE_BAND, and only on one side -- which read as a
                    # changed line on M7350A and FL1012A when the renders are identical.
SUP_MAX_CHARS = 3   # items this short sitting just off a line (a 'TM' superscript) are
                    # absorbed into it on BOTH sides. Legacy separates G3168B's 'TM' from
                    # its line by 2.6pt and we by 2.2pt, straddling LINE_BAND, so one side
                    # saw two lines and the other one.
SUP_BAND = 5.0
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


def form_args(argv, fallback_csv):
    """Form names from the command line, an @file, or the sweep CSV.

    @file exists because passing a few hundred names through Git Bash silently delivers
    only the last one -- a subset run reported "1/1 forms pass" and read as a clean sweep.
    """
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


def collapse(joined):
    """Comparison key: whitespace removed, leader runs shortened to a fixed marker.

    A dot or underscore leader's LENGTH is decorative fill, not content, and it depends
    on sub-point width accumulation -- BPT0003A's table of contents differs from legacy
    by one or two dots per line, which changed every line key while being invisible.
    Collapsing to a fixed marker keeps a MISSING leader detectable: many dots against
    none still differs.
    """
    return re.sub(r"([._\-])\1{2,}", lambda m: m.group(1) * 3,
                  re.sub(r"\s+", "", joined))


def lines(pdf):
    """-> (lines, spans).

    lines: (page, collapsed_text, start_x, baseline_y, display_text) per visual line.
    spans: (page, baseline_y, start_x, raw_text) per SPAN, UNGROUPED -- the grouping
    fallback in score() needs the spans themselves rather than this function's guess at
    how they band together, because that guess is exactly what it is compensating for.
    """
    d = fitz.open(str(pdf))
    out = []
    spans = []
    for pg in range(d.page_count):
        words = []
        for b in d[pg].get_text("dict")["blocks"]:
            for ln in b.get("lines", []):
                for s in ln["spans"]:
                    t = s["text"]
                    if t.strip():
                        words.append((s["origin"][1], s["bbox"][0], t, s["bbox"][2]))
        words.sort(key=lambda w: (w[0], w[1]))
        band = None
        for y, x, t, x1 in words:
            if band is None or abs(y - band[0]) > LINE_BAND:
                band = (y, [])
                out.append((pg, band))
            band[1].append((x, t, x1))
            spans.append((pg, y, x, t))

    # Absorb short superscript-ish items into the line they sit beside, so both renders
    # group them the same way regardless of which side of LINE_BAND they landed on.
    merged = []
    for pg, (y, items) in out:
        if (sum(len(t.strip()) for _, t, _ in items) <= SUP_MAX_CHARS and merged
                and merged[-1][0] == pg and abs(merged[-1][1][0] - y) <= SUP_BAND):
            merged[-1][1][1].extend(items)
            continue
        merged.append((pg, (y, list(items))))
    out = merged

    res = []
    for pg, (y, items) in out:
        items.sort(key=lambda i: i[0])
        # Split the band at column breaks so both sides segment a multi-column line
        # identically.
        seg, segs = [], []
        for it in items:
            # Measure the gap from the END of the previous item to the START of this
            # one. Comparing start-to-start split a TOC row -- 'LOSS CONDITIONS' at x=54
            # and its adjacent dot leader at x=149 read as 95pt apart when they touch.
            if seg and it[0] - seg[-1][2] > COL_GAP:
                segs.append(seg); seg = []
            seg.append(it)
        if seg:
            segs.append(seg)
        for sg in segs:
            joined = "".join(t for _, t, _ in sg)
            key = collapse(joined)
            if key:
                res.append((pg, key, sg[0][0], y,
                            re.sub(r"\s+", " ", joined).strip()))
    return res, spans


def unmatched(L, O, ospans):
    """Yield the legacy lines that have no counterpart in ours.

    Shared with tools/defectzoom.py so the region it zooms to is by construction the
    region the gate objected to. They were separate before, and the zoom pointed at
    grouping artefacts the gate had already forgiven.
    """
    idx = collections.defaultdict(list)
    for pg, key, x, y, _disp in O:
        idx[(pg, key)].append([x, y, False])

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
            continue
        if _band_match(key, x, y, pg, ospans):
            continue
        yield pg, key, x, y, disp


def _band_match(key, x, y, pg, ospans):
    # GROUPING FALLBACK. Where the two renders band or segment a line differently,
    # the legacy line's text still appears -- merged with a neighbour, or split.
    # Accept it if the legacy text starts at one of OUR spans on that baseline and
    # runs on from there. Content, vertical placement AND start x are all still
    # verified; only our own guess at where the line ENDS is given up.
    #
    # This replaces six rounds of threshold tuning that traded one grouping
    # artefact for another: 18pt column gaps chopped justified prose, 72pt missed
    # a 30pt column gap on M7350A, a per-page band measured worse at scale, and a
    # superscript absorbed into whichever neighbour happened to come first. The
    # grouping is heuristic and always will be, so the MATCH is made tolerant of it
    # rather than the grouping made perfect.
    #
    # It reads the band as SPANS, not as our own already-grouped lines, because
    # grouping first was wrong twice over. A band was keyed by the y of whichever
    # span opened it, so a legacy line 2.55pt from that anchor was rejected even
    # though the span it corresponds to sat 2.25pt away -- that alone accounted for
    # four of the thirteen failures (IM7213OM, M7902AA, FM7902AB, M7902ABa) and the
    # renders are 0.3pt apart. And joining segment keys in band order reverses
    # reading order whenever a run bands away from its neighbours, so a bold word
    # mid-sentence 0.75pt off the baseline made the text look changed
    # ("1. Partial Loss -- If a loss" arrived as "1. -- If a loss" + "Partial
    # Loss"). Sorting the raw spans by x reconstructs the line as it reads.
    #
    # ANCHORING IT AT A SPAN whose x matches is what keeps the fallback honest. An
    # earlier version asked only whether the text appeared anywhere in the band,
    # which threw the start-x check away -- shifting every glyph on a page 3pt
    # right still scored 100% on EB2410A, IM7213OM and M7902AA. Requiring the match
    # to BEGIN at a span sitting within START_TOL of the legacy line's start makes
    # that same shift score 0-2%.
    band = sorted((sp for sp in ospans
                   if sp[0] == pg and abs(sp[1] - y) <= BASE_TOL),
                  key=lambda sp: sp[2])
    for n, sp in enumerate(band):
        if abs(sp[2] - x) > START_TOL:
            continue
        if collapse("".join(t for _p, _y, _x, t in band[n:])).startswith(key):
            return True
    return False


def score(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, _lspans = lines(legacy)
        O, ospans = lines(ours)
    except Exception:
        return None
    if len(L) < MIN_LINES:
        return None

    misses = []
    lost = 0
    for _pg, key, _x, _y, disp in unmatched(L, O, ospans):
        lost += 1
        if len(misses) < 3:
            present = any(key == k for _, k, _, _, _ in O)
            misses.append(f"{'moved' if present else 'text differs'}: {disp[:44]!r}")
    return {"form": form, "lines": len(L),
            "pct": round(100 * (len(L) - lost) / len(L), 1), "misses": misses}


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
    # Report the DENOMINATOR of what was asked for, not only of what scored. A run that
    # silently received one form printed "1/1 forms pass".
    print(f"\n{len(ok)}/{len(rows)} forms pass  "
          f"(asked for {len(forms)}; {len(forms) - len(rows)} unscorable)")
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
    sys.exit(1 if main(form_args(sys.argv[1:], SWEEP)) else 0)
