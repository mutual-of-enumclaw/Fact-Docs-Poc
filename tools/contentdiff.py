"""Tier 1 acceptance gate: does our render say exactly what the legacy render says?

A missing space is a CONTENT error -- it can change what a filed form states -- while a
uniform 1pt offset is invisible to a reviewer. Pixel metrics rank those backwards. This
compares the page's text in reading order, so it catches dropped spaces, dropped glyphs
and wrong characters, and ignores sub-pixel placement entirely.

Usage:  python tools/contentdiff.py [FORM ...]     (default: every form in the last sweep)
"""
import csv
import difflib
import pathlib
import re
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
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
    ordered = [w[4] for _, ws in lines for w in sorted(ws, key=lambda w: w[0])]
    return re.sub(r"\s+", " ", " ".join(ordered)).strip()


def compare(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        npages = fitz.open(str(legacy)).page_count
    except Exception:
        return None

    total_l = total_match = 0
    examples = []
    for p in range(npages):
        a, b = page_text(legacy, p), page_text(ours, p)
        if a is None or b is None:
            continue
        at, bt = a.split(" "), b.split(" ")
        total_l += len(at)
        sm = difflib.SequenceMatcher(None, at, bt, autojunk=False)
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                total_match += i2 - i1
            elif len(examples) < 3:
                examples.append(f"p{p+1} {tag}: legacy={' '.join(at[i1:i2])[:44]!r} "
                                f"ours={' '.join(bt[j1:j2])[:44]!r}")
    if total_l == 0:
        return None
    return {"form": form, "words": total_l,
            "match_pct": round(100 * total_match / total_l, 1), "examples": examples}


def main(forms):
    results = [r for r in (compare(f) for f in forms) if r]
    results.sort(key=lambda r: r["match_pct"])

    print(f"{'form':<17}{'words':>7}{'content match':>15}")
    for r in results:
        print(f"{r['form']:<17}{r['words']:>7}{r['match_pct']:>14.1f}%")
        for e in r["examples"]:
            print(f"      {e}")

    perfect = [r for r in results if r["match_pct"] == 100.0]
    near = [r for r in results if 99.0 <= r["match_pct"] < 100.0]
    print(f"\n{len(perfect)}/{len(results)} forms 100% content-identical; "
          f"{len(near)} at 99-100%; "
          f"{len(results) - len(perfect) - len(near)} below 99%")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\content-diff.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "words", "match_pct", "examples"])
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
