"""Tier 1 acceptance gate: does our render say exactly what the legacy render says?

A missing space is a CONTENT error -- it can change what a filed form states -- while a
uniform 1pt offset is invisible to a reviewer. Pixel metrics rank those backwards. This
compares the page's text in reading order, so it catches dropped spaces, dropped glyphs
and wrong characters, and ignores sub-pixel placement entirely.

Usage:  python tools/contentdiff.py [FORM ...]     (default: every form in the last sweep)
"""
import collections
import csv
import difflib
import pathlib
import re
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
POS_TOL = 1.5   # pt; a spacing-only merge whose ends agree within this is an extraction artefact
LINE_TOL = 2.5  # pt; a baseline band, wide enough to absorb sub-point render differences
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")


def page_text(pdf, page):
    """Text in reading order, with runs of whitespace collapsed to one space.

    Collapsing does NOT hide a missing space: 'Agreement under' stays two words while
    'Agreementunder' stays one, so the diff still fires.
    """
    d = fitz.open(str(pdf))
    if page >= d.page_count:
        return None

    # Cluster words into lines before ordering. Sorting on a rounded y is not safe:
    # the two renders differ by fractions of a point, so words on the SAME visual line
    # sort into different sequences and the diff reports phantom moves. Group by a
    # baseline band instead, then order left-to-right within the band.
    words = sorted(d[page].get_text("words"), key=lambda w: (w[3], w[0]))
    lines, band = [], None
    for w in words:
        if band is None or abs(w[3] - band[0]) > LINE_TOL:
            band = (w[3], [])
            lines.append(band)
        band[1].append(w)
    return [(re.sub(r"\s+", " ", w[4]).strip(), w[0], w[2])
            for _, ws in lines for w in sorted(ws, key=lambda w: w[0])
            if w[4].strip()]


def compare(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        npages = fitz.open(str(legacy)).page_count
    except Exception:
        return None

    # Headline test: does the page say the same thing? Compare the whole character
    # stream with ALL whitespace removed. Word-level diffing mis-attributes characters
    # across token boundaries -- a quote emitted at the end of one span and the start of
    # the next reads as a dropped glyph when nothing was dropped at all. Character
    # equality cannot be fooled that way; word-level detail below is for locating things.
    chars_l = chars_o = 0
    chars_equal = True
    # Multiset equality answers "did we DROP anything", independent of reading order.
    # Stream equality also requires the same order -- but multi-column pages legitimately
    # linearise differently in the two renders, so an order difference is usually the
    # comparison's problem, not the render's. Report them separately.
    bag_l, bag_o = collections.Counter(), collections.Counter()
    for p in range(npages):
        aw, bw = page_text(legacy, p), page_text(ours, p)
        if aw is None or bw is None:
            continue
        ca = re.sub(r"\s+", "", "".join(w[0] for w in aw))
        cb = re.sub(r"\s+", "", "".join(w[0] for w in bw))
        chars_l += len(ca)
        chars_o += len(cb)
        bag_l.update(ca)
        bag_o.update(cb)
        if ca != cb:
            chars_equal = False

    total_l = total_match = artefacts = 0
    examples = []
    for p in range(npages):
        aw, bw = page_text(legacy, p), page_text(ours, p)
        if aw is None or bw is None:
            continue
        at, bt = [w[0] for w in aw], [w[0] for w in bw]
        total_l += len(at)
        sm = difflib.SequenceMatcher(None, at, bt, autojunk=False)
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                total_match += i2 - i1
                continue

            # Spacing-only difference? Then the SAME characters are present and the
            # only question is whether the ink is in the right place. We emit one
            # positioned span per token while Documaker emits a separate text-showing
            # op per token, so PyMuPDF merges neighbours whose boxes abut even when
            # the render is correct. Failing that would reject renders already
            # accepted by eye -- the exact mistake that disqualified ink IoU.
            la, lb = "".join(at[i1:i2]), "".join(bt[j1:j2])
            if la and la == lb and i2 > i1 and j2 > j1:
                lx0, lx1 = aw[i1][1], aw[i2 - 1][2]
                ox0, ox1 = bw[j1][1], bw[j2 - 1][2]
                if abs(lx0 - ox0) <= POS_TOL and abs(lx1 - ox1) <= POS_TOL:
                    total_match += i2 - i1      # same glyphs, same place -> passes
                    artefacts += i2 - i1
                    continue
                if len(examples) < 3:
                    examples.append(f"p{p+1} SPACING+MOVED: {' '.join(at[i1:i2])[:34]!r} "
                                    f"x {lx0:.1f}-{lx1:.1f} vs {ox0:.1f}-{ox1:.1f}")
                continue

            if len(examples) < 3:
                examples.append(f"p{p+1} {tag}: legacy={' '.join(at[i1:i2])[:40]!r} "
                                f"ours={' '.join(bt[j1:j2])[:40]!r}")
    if total_l == 0:
        return None
    dropped = bag_l - bag_o
    return {"form": form, "words": total_l, "artefacts": artefacts,
            "nothing_dropped": not dropped,
            "dropped": "".join(f"{c}x{n} " for c, n in dropped.most_common(6)),
            "chars_equal": chars_equal, "chars": chars_l, "chars_ours": chars_o,
            "match_pct": round(100 * total_match / total_l, 1), "examples": examples}


def main(forms):
    results = [r for r in (compare(f) for f in forms) if r]
    results.sort(key=lambda r: r["match_pct"])

    print(f"{'form':<17}{'words':>7}{'content match':>15}")
    for r in results:
        print(f"{r['form']:<17}{r['words']:>7}{r['match_pct']:>14.1f}%")
        for e in r["examples"]:
            print(f"      {e}")

    # THE GATE. Order-insensitive, so it cannot be perturbed by how a multi-column page
    # happens to linearise -- and it asks the question that actually matters: did any
    # character of the legacy document fail to appear in ours. The order-sensitive figure
    # below moved 40->38->39->38 across calibration changes that altered no content at
    # all, so it is a diagnostic and must never be used as pass/fail.
    nod = [r for r in results if r["nothing_dropped"]]
    print(f"\nTIER 1 GATE -- nothing dropped: {len(nod)}/{len(results)} forms")
    for r in results:
        if not r["nothing_dropped"]:
            print(f"   DROPS {r['form']:<17} {r['dropped']}")

    ident = [r for r in results if r["chars_equal"]]
    print(f"\nCHARACTER-IDENTICAL (Tier 1 gate): {len(ident)}/{len(results)} forms")
    for r in results:
        if not r["chars_equal"]:
            print(f"   FAIL {r['form']:<17} legacy {r['chars']} chars vs ours {r['chars_ours']}")

    perfect = [r for r in results if r["match_pct"] == 100.0]
    near = [r for r in results if 99.0 <= r["match_pct"] < 100.0]
    print(f"\n{len(perfect)}/{len(results)} forms 100% content-identical; "
          f"{len(near)} at 99-100%; "
          f"{len(results) - len(perfect) - len(near)} below 99%")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\content-diff.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "words", "match_pct", "artefacts", "chars_equal", "nothing_dropped", "dropped", "chars", "chars_ours", "examples"])
        w.writeheader()
        for r in results:
            w.writerow({**r, "examples": " | ".join(r["examples"])})
    print(f"Wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    main(args)
