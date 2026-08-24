"""Lost whitespace INSIDE a text token -- the one defect class every other gate is blind to.

WHY THIS EXISTS. A FAP token's leading spaces are layout: `M,TT,(...),6,  From` is positioned
at col1 and the two spaces push the 'F' right. Delete them and the word lands half a character
left of where Documaker puts it, on 1,340 of 4,478 forms -- and nothing caught it.

    Tier 1        strips whitespace for its headline character test
    line placement collapses whitespace, and checks the LINE's start x, which is the
                  box edge and therefore still correct
    Tier 2        scores glyph positions, so it sees this -- as more of the intra-run
                  drift Products has said is invisible, i.e. indistinguishably
    ink gates     measure rules, shading and artwork, not glyphs

Tier 1 does report it, as a SPACING+MOVED example: same characters, different extent. But 181
of 913 forms had one and the class was written off as drift. The population splits cleanly on
whether OUR token count is lower than legacy's -- i.e. whether the two renders disagree about
where one word ends and the next begins:

    our tokens FEWER (a merge)   n=483   median extent delta  -3.00pt   <- one space at 10pt
    token count equal            n=653   median extent delta  -0.20pt   <- drift

So this reports the merge population on its own, and summarises it as the count of places our
render is short by at least half a space. That number should be ~0. It is a DIAGNOSTIC, not a
gate: a legitimate extraction merge (we emit one positioned span per token where Documaker
emits a separate text-showing operator, so PyMuPDF joins neighbours whose boxes abut) lands in
the same population with a delta near zero, which is why the magnitude, not the merge, is the
signal.

Usage:  python tools/tokenspace.py [FORM ... | @file-of-form-names]
"""
import collections
import csv
import difflib
import pathlib
import statistics
import sys

import fitz

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import contentdiff  # noqa: E402

WORK = contentdiff.WORK
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
OUTDIR = pathlib.Path(__file__).resolve().parent.parent / "output"

SHORT_PT = 1.4      # pt; half a space at 10pt. Below this a merge is an extraction artefact.


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


def deltas(form):
    """-> (merge_deltas, equal_deltas) in pt, over same-character/different-extent runs."""
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        npages = fitz.open(str(legacy)).page_count
        fields = contentdiff.field_boxes(form)
    except Exception:
        return None
    merge, equal = [], []
    for p in range(npages):
        boxes = fields.get(p, ())
        try:
            aw = contentdiff.page_text(legacy, p, boxes)
            bw = contentdiff.page_text(ours, p, boxes)
        except Exception:
            break
        if aw is None or bw is None:
            break
        at, bt = [w[0] for w in aw], [w[0] for w in bw]
        for tag, i1, i2, j1, j2 in difflib.SequenceMatcher(
                None, at, bt, autojunk=False).get_opcodes():
            if tag == "equal" or i2 <= i1 or j2 <= j1:
                continue
            # Same characters either side: the only question is the extent.
            if "".join(at[i1:i2]) != "".join(bt[j1:j2]):
                continue
            d = (bw[j2 - 1][2] - bw[j1][1]) - (aw[i2 - 1][2] - aw[i1][1])
            (merge if (j2 - j1) < (i2 - i1) else equal).append((d, p + 1, at[i1]))
    return merge, equal


def main(forms):
    rows, all_merge, all_equal = [], [], []
    for form in forms:
        r = deltas(form)
        if r is None:
            continue
        merge, equal = r
        short = [m for m in merge if m[0] <= -SHORT_PT]
        all_merge += [m[0] for m in merge]
        all_equal += [e[0] for e in equal]
        if short:
            rows.append({"form": form, "short": len(short),
                         "worst_pt": round(min(m[0] for m in short), 2),
                         "example": f"p{short[0][1]} {short[0][2]!r}"})
    rows.sort(key=lambda r: r["worst_pt"])

    print(f"Lost token whitespace: merges where OUR extent is >= {SHORT_PT}pt short\n")
    print(f"{'form':<18}{'places':>7}{'worst':>9}  example")
    for r in rows:
        print(f"{r['form']:<18}{r['short']:>7}{r['worst_pt']:>8.2f}pt  {r['example']}")

    for name, xs in (("merged  ", all_merge), ("unmerged", all_equal)):
        if not xs:
            print(f"\n{name}: none")
            continue
        print(f"\n{name}: n={len(xs)}  median={statistics.median(xs):+.2f}pt  "
              f"min={min(xs):+.2f}  max={max(xs):+.2f}")
        h = collections.Counter(round(x * 2) / 2 for x in xs)
        print("   ", " ".join(f"{k:+.1f}:{v}" for k, v in sorted(h.items())))

    print(f"\n{sum(r['short'] for r in rows)} short merges across {len(rows)} of "
          f"{len(forms)} forms")
    out = OUTDIR / "tokenspace.csv"
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "short", "worst_pt", "example"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")
    return len(rows)


if __name__ == "__main__":
    sys.exit(1 if main(form_args(sys.argv[1:], SWEEP)) else 0)
