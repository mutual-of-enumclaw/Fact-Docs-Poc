"""Zoom to the region a gate objects to, and stack legacy above ours.

A full-page side-by-side is nearly useless for judging a 2pt defect: the eye has to
find the difference before it can judge it. This locates the region that actually
disagrees and crops both renders to it at high DPI.

Legacy is stacked ABOVE ours rather than beside it, deliberately -- differences in
horizontal position and in line breaks are far easier to see when the two share an x
axis than when they are separated by a page width.

How the region is chosen depends on what failed:
  line   the baseline of the first legacy line whose text or position we did not match
  ink    the bounding box of the legacy non-text ink we did not reproduce
  else   the top of page 1

Usage:  python tools/defectzoom.py FORM [--reason line|ink] [--dpi 220] [--pad 40]
"""
import pathlib
import sys

import fitz
import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import lineplace      # noqa: E402
import nontextink     # noqa: E402
import parity         # noqa: E402

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
OUT = pathlib.Path(r"C:\src\fact-pdf-tools\output\zoom")


def line_region(form):
    """(page, y) of the first legacy line we failed to match, or None."""
    L = lineplace.lines(WORK / f"{form}.PDF")
    O = lineplace.lines(WORK / f"{form}_ours.pdf")
    ours = {}
    for pg, key, x, y, _d in O:
        ours.setdefault((pg, key), []).append((x, y))
    for pg, key, x, y, _d in L:
        hits = ours.get((pg, key), [])
        if not any(abs(ox - x) <= lineplace.START_TOL and abs(oy - y) <= lineplace.BASE_TOL
                   for ox, oy in hits):
            return pg, y
    return None


def ink_region(form):
    """(page, y0, y1, x0, x1) of the legacy non-text ink we did not reproduce."""
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    npages = fitz.open(str(legacy)).page_count
    best = None
    for p in range(npages):
        ra, _ = parity.raster(str(legacy), p)
        oa, _ = parity.raster(str(ours), p)
        if ra is None or oa is None:
            continue
        h, w = max(ra.shape[0], oa.shape[0]), max(ra.shape[1], oa.shape[1])
        ra, oa = parity.pad_to(ra, h, w), parity.pad_to(oa, h, w)
        mask = parity.dilate(
            nontextink.text_mask(legacy, p, (h, w)) | nontextink.text_mask(ours, p, (h, w)), 1)
        rb = (ra <= parity.INK) & ~mask
        ob = (oa <= parity.INK) & ~mask
        un = rb & ~parity.dilate(ob, nontextink.TOL_PX)
        if un.sum() < 20:
            continue
        ys, xs = np.nonzero(un)
        k = parity.DPI / 72.0
        cand = (int(un.sum()), p, ys.min() / k, ys.max() / k, xs.min() / k, xs.max() / k)
        if best is None or cand[0] > best[0]:
            best = cand
    return best[1:] if best else None


def stack(form, page, clip, dpi, note):
    a = fitz.open(str(WORK / f"{form}.PDF"))[page].get_pixmap(dpi=dpi, clip=clip)
    b = fitz.open(str(WORK / f"{form}_ours.pdf"))[page].get_pixmap(dpi=dpi, clip=clip)
    LBL = 22
    w = max(a.width, b.width)
    h = a.height + b.height + LBL * 2 + 8
    doc = fitz.open()
    pg = doc.new_page(width=w, height=h)
    pg.draw_rect(fitz.Rect(0, 0, w, h), color=None, fill=(1, 1, 1))
    pg.insert_text((4, 15), f"LEGACY (Documaker)  —  {form} p{page+1}  —  {note}",
                   fontsize=11, color=(0.65, 0.1, 0.1))
    pg.insert_image(fitz.Rect(0, LBL, a.width, LBL + a.height), pixmap=a)
    y2 = LBL + a.height + 8
    pg.insert_text((4, y2 + 15), f"OURS (Form Studio)  —  {form} p{page+1}",
                   fontsize=11, color=(0.1, 0.45, 0.15))
    pg.insert_image(fitz.Rect(0, y2 + LBL, b.width, y2 + LBL + b.height), pixmap=b)
    pg.draw_line(fitz.Point(0, y2 + 3), fitz.Point(w, y2 + 3), color=(0.75, 0.75, 0.75))
    OUT.mkdir(parents=True, exist_ok=True)
    out = OUT / f"{form}_zoom.png"
    pg.get_pixmap(dpi=96).save(str(out))
    print(f"  {out}")
    return out


def main(form, reason, dpi, pad):
    legacy = WORK / f"{form}.PDF"
    if not legacy.exists():
        sys.exit(f"no cached legacy render for {form}")
    pw = fitz.open(str(legacy))[0].rect.width

    if reason in (None, "line"):
        r = line_region(form)
        if r:
            pg, y = r
            clip = fitz.Rect(0, max(0, y - pad), pw, y + pad)
            return stack(form, pg, clip, dpi, f"unmatched line at y={y:.0f}pt")
    if reason in (None, "ink"):
        r = ink_region(form)
        if r:
            pg, y0, y1, x0, x1 = r
            clip = fitz.Rect(max(0, x0 - pad / 2), max(0, y0 - pad),
                             min(pw, x1 + pad / 2), y1 + pad)
            return stack(form, pg, clip, dpi, f"missing non-text ink y={y0:.0f}-{y1:.0f}pt")
    print(f"  {form}: nothing localised; showing the top of page 1")
    return stack(form, 0, fitz.Rect(0, 0, pw, 220), dpi, "no region localised")


if __name__ == "__main__":
    a = sys.argv[1:]
    def opt(n, d, cast=int):
        return cast(a[a.index(n) + 1]) if n in a else d
    forms = [x for i, x in enumerate(a)
             if not x.startswith("--") and (i == 0 or a[i - 1] not in ("--reason", "--dpi", "--pad"))]
    for f in forms:
        main(f, opt("--reason", None, str), opt("--dpi", 220), opt("--pad", 40))
