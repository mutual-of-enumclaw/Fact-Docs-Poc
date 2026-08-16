"""Render legacy vs. ours as one side-by-side PNG per form, for human judgement.

The IoU score says how well the ink overlaps; it does NOT say whether a human would
call the output acceptable. This produces the artefact a person can actually judge.

Usage:  python tools/sidebyside.py FORM [FORM ...] [--dpi 150] [--page 1]
"""
import pathlib
import sys

import fitz

OUT = pathlib.Path(r"C:\src\fact-pdf-tools\output\compare")
WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
GAP, LABEL_H = 24, 34


def page_pix(pdf, page, dpi):
    d = fitz.open(str(pdf))
    if page >= d.page_count:
        return None
    return d[page].get_pixmap(dpi=dpi)


def build(form, legacy_pdf, our_pdf, page, dpi):
    a = page_pix(legacy_pdf, page, dpi)
    b = page_pix(our_pdf, page, dpi)
    if a is None or b is None:
        print(f"  {form}: page {page+1} missing")
        return None

    w, h = a.width + GAP + b.width, max(a.height, b.height) + LABEL_H
    doc = fitz.open()
    pg = doc.new_page(width=w, height=h)
    pg.draw_rect(fitz.Rect(0, 0, w, h), color=None, fill=(1, 1, 1))

    pg.insert_text((4, 22), f"LEGACY (Documaker FAP2PDF) — {form} p{page+1}",
                   fontsize=13, color=(0.65, 0.1, 0.1))
    pg.insert_text((a.width + GAP + 4, 22), f"OURS (FAP → HTML → Chromium) — {form} p{page+1}",
                   fontsize=13, color=(0.1, 0.45, 0.15))

    pg.insert_image(fitz.Rect(0, LABEL_H, a.width, LABEL_H + a.height), pixmap=a)
    pg.insert_image(fitz.Rect(a.width + GAP, LABEL_H, a.width + GAP + b.width,
                              LABEL_H + b.height), pixmap=b)
    # divider
    pg.draw_line(fitz.Point(a.width + GAP / 2, LABEL_H),
                 fitz.Point(a.width + GAP / 2, h), color=(0.7, 0.7, 0.7), width=1)

    OUT.mkdir(parents=True, exist_ok=True)
    out = OUT / f"{form}_p{page+1}_compare.png"
    pg.get_pixmap(dpi=96).save(str(out))
    print(f"  wrote {out.name}  ({a.width}x{a.height} vs {b.width}x{b.height})")
    return out


if __name__ == "__main__":
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    dpi = 150
    page = 0
    for i, a in enumerate(sys.argv):
        if a == "--dpi":
            dpi = int(sys.argv[i + 1])
        if a == "--page":
            page = int(sys.argv[i + 1]) - 1
    for form in args:
        legacy = WORK / f"{form}.PDF"
        ours = WORK / f"{form}_ours.pdf"
        if not legacy.exists():
            print(f"  {form}: no legacy render in {WORK}")
            continue
        build(form, legacy, ours, page, dpi)
