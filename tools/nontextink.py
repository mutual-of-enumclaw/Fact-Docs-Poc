"""Non-text ink gate: is the legacy's artwork present in our render?

WHY THIS EXISTS. Tier 1 compares characters and Tier 2 compares glyph positions, so
both are text-only -- and `emit-html` does not emit FAP `G,` image records at all. The
result is a blind spot: every image-bearing form in the sweep PASSES Tier 2, several at
99%+, while its logo is simply missing. A gate that cannot see a whole missing logo is
not measuring "indistinguishable".

WHAT IT MEASURES. Rasterize both renders, mask out the text on both sides, and ask what
fraction of the legacy's remaining ink we reproduce. That covers logos AND rules/boxes,
which is the right scope: anything on the page that is not a glyph.

The text mask is the UNION of both documents' text boxes, dilated. Masking only the
legacy's text would leave our text unmasked, and our glyphs would then be scored as if
they were artwork -- inflating the result. Union-masking is the conservative choice.

Recall is deliberately measured with a tolerance dilation, because artwork that is
present but a pixel or two off is a placement issue for Tier 2 to talk about, not a
missing-image defect.

BOTH DIRECTIONS ARE REPORTED. Recall gates; precision -- how much of OUR non-text ink
the legacy also has -- is reported alongside because recall alone is blind to ink we
INVENT. Spurious box edges, or artwork FAP2PDF omits, cannot move a recall score at all,
so without precision a change that draws more than Documaker is unmeasurable.

Usage:  python tools/nontextink.py [FORM ...]     (default: every form in the last sweep)
"""
import collections
import csv
import pathlib
import sys

import fitz
import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import parity  # noqa: E402  (raster / pad_to / dilate)

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")
FORMS = pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS")

THRESHOLD_PCT = 90.0   # gate: this much of the legacy's non-text ink must be present
TOL_PX = 2             # placement slack, in raster pixels at parity.DPI
MIN_INK = 200          # below this many non-text pixels the page says nothing useful


def text_mask(pdf, page, shape, dpi=parity.DPI):
    """Boolean mask of every text bounding box on the page, in raster pixels."""
    m = np.zeros(shape, dtype=bool)
    d = fitz.open(str(pdf))
    if page >= d.page_count:
        return m
    k = dpi / 72.0
    for b in d[page].get_text("dict")["blocks"]:
        for line in b.get("lines", []):
            for s in line["spans"]:
                x0, y0, x1, y1 = s["bbox"]
                m[max(0, int(y0 * k) - 1):min(shape[0], int(y1 * k) + 2),
                  max(0, int(x0 * k) - 1):min(shape[1], int(x1 * k) + 2)] = True
    return m


def image_boxes(form, page):
    """Rects of the FAP's G, image placements, in points.

    Excluded from the PRECISION side only. FAP2PDF embeds no images at all (section 28),
    so every pixel of correctly-rendered artwork would otherwise count against us -- the
    lowest-precision forms were almost entirely image-bearing ones being penalised for
    drawing the logo. Legacy provably has nothing there to compare with, the same reason
    it is not a valid oracle for fragments.
    """
    p = FORMS / f"{form}.FAP"
    if not p.exists():
        return []
    S = 72.0 / 2400.0
    out, pg = [], -1
    for raw in p.read_bytes().decode("cp1252", errors="replace").splitlines():
        t = raw.strip()
        if t.startswith("H,"):
            pg += 1
        elif t.startswith("G,") and pg == page:
            try:
                n = [int(x) for x in t.split("(")[1].split(")")[0].split(",")[:4]]
                out.append((n[1] * S, n[0] * S, n[3] * S, n[2] * S))
            except Exception:
                pass
    return out


def has_images(form):
    """Does the FAP declare a G, image record? N, records are developer notes."""
    p = FORMS / f"{form}.FAP"
    if not p.exists():
        return None
    for raw in p.read_bytes().decode("cp1252", errors="replace").splitlines():
        if raw.startswith("G,"):
            return True
    return False


def score(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        npages = fitz.open(str(legacy)).page_count
    except Exception:
        return None

    tot = hit = otot = ohit = 0
    for p in range(npages):
        ra, _ = parity.raster(str(legacy), p)
        oa, _ = parity.raster(str(ours), p)
        if ra is None or oa is None:
            continue
        h, w = max(ra.shape[0], oa.shape[0]), max(ra.shape[1], oa.shape[1])
        ra, oa = parity.pad_to(ra, h, w), parity.pad_to(oa, h, w)

        mask = text_mask(legacy, p, (h, w)) | text_mask(ours, p, (h, w))
        mask = parity.dilate(mask, 1)

        rb = (ra <= parity.INK) & ~mask          # legacy non-text ink
        ob = (oa <= parity.INK) & ~mask          # ours non-text ink
        tot += int(rb.sum())
        hit += int(np.logical_and(rb, parity.dilate(ob, TOL_PX)).sum())
        # PRECISION: how much of OUR non-text ink the legacy also has. Recall alone is
        # blind to ink we invent -- spurious box edges, artwork legacy omits -- so a
        # change that draws MORE than Documaker cannot be detected by it at all.
        ob_p = ob.copy()
        for x0, y0, x1, y1 in image_boxes(form, p):
            k = parity.DPI / 72.0
            ob_p[max(0, int(y0 * k)):int(y1 * k) + 1,
                 max(0, int(x0 * k)):int(x1 * k) + 1] = False
        otot += int(ob_p.sum())
        ohit += int(np.logical_and(ob_p, parity.dilate(rb, TOL_PX)).sum())

    prec = round(100 * ohit / otot, 1) if otot else None
    if tot < MIN_INK:
        return {"form": form, "ink": tot, "pct": None, "precision": prec,
                "img": has_images(form)}
    return {"form": form, "ink": tot, "pct": round(100 * hit / tot, 1),
            "precision": prec, "img": has_images(form)}


def main(forms):
    rows = [r for r in (score(f) for f in forms) if r]
    scored = [r for r in rows if r["pct"] is not None]
    skipped = [r for r in rows if r["pct"] is None]
    scored.sort(key=lambda r: r["pct"])

    print(f"Non-text ink: >= {THRESHOLD_PCT}% of the legacy's non-glyph ink present "
          f"(+-{TOL_PX}px)\n")
    print(f"{'form':<18}{'ink px':>9}{'recall':>9}{'precis':>9}  {'G,':>3}  gate")
    for r in scored:
        pr = "-" if r["precision"] is None else f"{r['precision']:.1f}%"
        print(f"{r['form']:<18}{r['ink']:>9}{r['pct']:>8.1f}%{pr:>9}  "
              f"{'img' if r['img'] else '-':>3}  "
              f"{'PASS' if r['pct'] >= THRESHOLD_PCT else 'FAIL'}")

    ok = [r for r in scored if r["pct"] >= THRESHOLD_PCT]
    print(f"\n{len(ok)}/{len(scored)} forms pass on RECALL (the gate)")
    pv = [r["precision"] for r in scored if r["precision"] is not None]
    if pv:
        import statistics as _st
        low = sorted((r for r in scored if r["precision"] is not None),
                     key=lambda r: r["precision"])[:5]
        print(f"   precision (our non-text ink that legacy also has): median "
              f"{_st.median(pv):.1f}%, NOT gated -- lowest: "
              + ", ".join(f"{r['form']}={r['precision']:.0f}%" for r in low))
    # Never hide what was excluded: a silently-skipped page reads as a pass.
    print(f"   {len(skipped)} form(s) skipped: under {MIN_INK} non-text ink pixels, "
          f"too little to judge")

    img = [r for r in scored if r["img"]]
    if img:
        imgok = sum(1 for r in img if r["pct"] >= THRESHOLD_PCT)
        worst = min(img, key=lambda r: r["pct"])
        print(f"   forms declaring a G, image: {imgok}/{len(img)} pass "
              f"(worst {worst['form']} at {worst['pct']}%)")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\nontextink.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "ink", "pct", "precision", "img"])
        w.writeheader()
        w.writerows(rows)
    print(f"Wrote {out}")
    return sum(1 for r in scored if r["pct"] < THRESHOLD_PCT)


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    sys.exit(1 if main(args) else 0)
