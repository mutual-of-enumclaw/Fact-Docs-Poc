"""Glyph-shape DIAGNOSTIC: are we drawing the RIGHT GLYPHS, not just the right codes?

WHY THIS EXISTS. Nothing else measures glyph identity, and that let a real defect
through: DocuDings symbols rendered as Latin letters and all three gates passed
(FORM-STUDIO-PLAN section 27). Tier 1 compares character CODES -- identical. Tier 2
compares POSITIONS -- a wrong glyph sits in the right place. The non-text ink gate masks
text out by construction. A wrong font is invisible to every one of them.

HOW IT WORKS. For each glyph Tier 2 can match by (page, char, position), crop it out of
both rasters, reduce each to a 16x16 ink bitmap normalised to its own ink bounding box,
and compare. Normalising away position and size is essential: comparing the rasters where
they sit measures REGISTRATION, and at 200dpi half a point of drift destroys the score --
measured, the accepted forms came out at 6-10% that way.

IT IS A DIAGNOSTIC, NOT A GATE, and deliberately so. Per-glyph agreement is noisy at small
sizes -- on P0010G only 72% of CORRECT glyphs clear the per-glyph cut -- so a pass/fail
built on it would manufacture work, which this project has disqualified metrics for
before. What is reported instead:

  median   per-form median shape agreement. Accepted forms sit at 0.79-0.86; a form
           rendered in the wrong face would sit near the wrong-glyph level, ~0.35.
  suspect  characters whose MEDIAN agreement is below 0.50 over 3+ occurrences. A wrong
           face makes every glyph of that face disagree, so it shows up here even though
           individual comparisons are noisy.

VALIDATED for sensitivity, which is the thing that matters for a shape metric: comparing
each legacy glyph against a DIFFERENT character's crop drops EB2410A from 92.2% to 13.8%.
Run `--selftest` to reproduce that.

TWO KNOWN LIMITATIONS, both measured:
  * A suspect character is a LEAD, not a defect. G2425B flags 'w' at 0.27 over 12
    occurrences, and it is an artefact -- legacy's char bbox is 2.7pt taller than ours, so
    the crop catches ink from neighbouring lines.
  * It cannot see a face used for only one or two glyphs per form; MIN_CHAR is 3. The
    DocuDings defect that motivated this tool is right at that edge.

Usage:  python tools/glyphshape.py [FORM ...]
        python tools/glyphshape.py --selftest FORM   (prove it reacts to shape)
"""
import collections
import csv
import pathlib
import statistics
import sys

import fitz
import numpy as np

OUTDIR = pathlib.Path(__file__).resolve().parent.parent / "output"
WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")

DPI = 200            # finer than the ink gate: a glyph is only a few points across
POS_TOL = 2.0        # pt; must be loose enough to survive the residual drift (section 25)
SHAPE_IOU = 0.55     # a matched glyph counts as the same shape at or above this
THRESHOLD_PCT = 90.0
MIN_GLYPHS = 20      # below this a form says nothing useful
MIN_INK_PX = 5       # ignore glyphs whose ink is smaller than this; a 4px mark
                     # normalised to 16x16 is mostly noise
CHAR_FLOOR = 0.50    # a character whose MEDIAN shape agreement is below this, over at
                     # least MIN_CHAR occurrences, is reported as suspect
MIN_CHAR = 3
INK = 200


def glyphs(pdf):
    d = fitz.open(str(pdf))
    out = []
    for pg in range(d.page_count):
        for b in d[pg].get_text("rawdict")["blocks"]:
            for line in b.get("lines", []):
                for s in line["spans"]:
                    for ch in s.get("chars", []):
                        c = ch["c"]
                        if c.strip() and c >= " ":
                            out.append((pg, c, ch["bbox"]))
    return out


def rasters(pdf, pages):
    out = {}
    d = fitz.open(str(pdf))
    for pg in pages:
        if pg < d.page_count:
            pm = d[pg].get_pixmap(dpi=DPI, colorspace=fitz.csGRAY)
            out[pg] = np.frombuffer(pm.samples, dtype=np.uint8).reshape(pm.height, pm.width)
    return out


def crop(img, bbox, k):
    x0, y0, x1, y1 = (int(round(v * k)) for v in bbox)
    x0, y0 = max(0, x0), max(0, y0)
    x1, y1 = min(img.shape[1], max(x1, x0 + 1)), min(img.shape[0], max(y1, y0 + 1))
    return img[y0:y1, x0:x1]


def _ink_bitmap(crop_img, n=16):
    """Normalise a glyph crop to an n-by-n ink bitmap.

    Crops to the ink's own bounding box first, so the comparison is invariant to
    translation, and rescales to a fixed grid, so it is invariant to the small size
    differences between two renders. What is left is SHAPE, which is the only thing this
    gate is trying to judge -- comparing the rasters where they sit measures registration
    instead, and at 200dpi a half-point of drift destroys the score (measured: accepted
    forms came out at 6-10%).
    """
    ink = crop_img <= INK
    if not ink.any():
        return None
    ys, xs = np.nonzero(ink)
    sub = ink[ys.min():ys.max() + 1, xs.min():xs.max() + 1]
    yi = (np.arange(n) * sub.shape[0] // n).clip(0, sub.shape[0] - 1)
    xi = (np.arange(n) * sub.shape[1] // n).clip(0, sub.shape[1] - 1)
    return sub[yi][:, xi]


def compare_shape(a, b):
    """Shape agreement of two glyph crops, 0..1."""
    if a.size == 0 or b.size == 0:
        return 0.0
    na, nb = _ink_bitmap(a), _ink_bitmap(b)
    if na is None and nb is None:
        return 1.0            # both blank: nothing to disagree about
    if na is None or nb is None:
        return 0.0            # one drew ink and the other did not
    union = np.logical_or(na, nb).sum()
    return float(np.logical_and(na, nb).sum() / union) if union else 1.0


def score(form, shuffle=False):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        L, O = glyphs(legacy), glyphs(ours)
    except Exception:
        return None
    if not L:
        return None

    idx = collections.defaultdict(list)
    for pg, c, bb in O:
        idx[(pg, c)].append(bb)

    pages = sorted({pg for pg, _, _ in L})
    try:
        RL, RO = rasters(legacy, pages), rasters(ours, pages)
    except Exception:
        return None
    k = DPI / 72.0

    same = matched = 0
    worst = []
    per_char = collections.defaultdict(list)
    pairs = []
    for pg, c, bb in L:
        cands = idx.get((pg, c), [])
        best, bd = None, None
        for ob in cands:
            dx, dy = abs(ob[0] - bb[0]), abs(ob[1] - bb[1])
            if dx <= POS_TOL and dy <= POS_TOL and (bd is None or dx + dy < bd):
                best, bd = ob, dx + dy
        if best is None or pg not in RL or pg not in RO:
            continue
        pairs.append((pg, c, bb, best))

    # The self-test compares each legacy glyph against the NEXT pair's crop -- a
    # different character -- which is the only honest way to show the metric reacts to
    # shape rather than to whether a match was found at all.
    for i, (pg, c, bb, ob) in enumerate(pairs):
        if shuffle:
            j = next((n for n in range(1, len(pairs))
                      if pairs[(i + n) % len(pairs)][1] != c), None)
            if j is None:
                continue
            opg, _, _, ob = pairs[(i + j) % len(pairs)]
        else:
            opg = pg
        ca, cb = crop(RL[pg], bb, k), crop(RO[opg], ob, k)
        if min(ca.shape + cb.shape) < MIN_INK_PX:
            continue
        matched += 1
        iou = compare_shape(ca, cb)
        per_char[c].append(iou)
        if iou >= SHAPE_IOU:
            same += 1

    if matched < MIN_GLYPHS:
        return {"form": form, "glyphs": matched, "pct": None,
                "median": None, "suspect": ""}
    # Per-CHARACTER medians are the real signal. A wrong face makes every glyph of that
    # face disagree, which stands out as a low median even though individual glyph
    # comparisons are noisy at small sizes; a single mis-hinted 'e' does not.
    suspect = sorted(
        ((ch, round(statistics.median(v), 2), len(v))
         for ch, v in per_char.items()
         if len(v) >= MIN_CHAR and statistics.median(v) < CHAR_FLOOR),
        key=lambda t: t[1])
    allv = [v for vals in per_char.values() for v in vals]
    return {"form": form, "glyphs": matched,
            "pct": round(100 * same / matched, 1),
            "median": round(statistics.median(allv), 2),
            "suspect": " ".join(f"{ch!r}:{m}(n={n})" for ch, m, n in suspect[:6])}


def selftest(form):
    """Prove the gate can fail: compare each legacy glyph against a DIFFERENT letter.

    If the score stays high under a deliberate character remap, the metric is not
    actually looking at shape and must not be trusted.
    """
    base = score(form)
    bad = score(form, shuffle=True)
    print(f"{form}: normal {base['pct']}%   comparing against the WRONG glyph "
          f"{bad['pct'] if bad else '-'}%")
    if bad and bad["pct"] is not None and base["pct"] is not None:
        print("  VERDICT:", "sensitive -- it sees shape"
              if bad["pct"] < base["pct"] - 20 else "NOT SENSITIVE -- do not trust it")


def main(forms):
    rows = [r for r in (score(f) for f in forms) if r]
    scored = [r for r in rows if r["pct"] is not None]
    scored.sort(key=lambda r: (r["median"] if r["median"] is not None else 9, r["pct"]))
    print("Glyph shape DIAGNOSTIC (not a pass/fail gate -- see the module docstring)\n")
    print(f"{'form':<18}{'glyphs':>8}{'median':>8}{'>=cut':>8}   suspect characters")
    flagged = 0
    for r in scored:
        if r["suspect"]:
            flagged += 1
        print(f"{r['form']:<18}{r['glyphs']:>8}{r['median']:>8.2f}{r['pct']:>7.1f}%   {r['suspect']}")
    print(f"\n{len(scored)} forms scored, {len(rows) - len(scored)} skipped "
          f"(under {MIN_GLYPHS} comparable glyphs)")
    print(f"{flagged} form(s) have at least one suspect character "
          f"(median shape agreement below {CHAR_FLOOR} over {MIN_CHAR}+ occurrences)")
    out = pathlib.Path(OUTDIR) / "glyphshape.csv"
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "glyphs", "pct", "median", "suspect"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")
    return 0


if __name__ == "__main__":
    args = sys.argv[1:]
    if args and args[0] == "--selftest":
        selftest(args[1])
        sys.exit(0)
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    sys.exit(main(args))
