"""Render one page of GhostDraft vs HTML at high dpi, cropped, for close inspection.
python tools/peek_case.py CASE PAGE [x0 y0 x1 y1 in pt]"""
import os, sys
import fitz
from PIL import Image

ROOT = r'C:\src\fact-poc\output\ghostdraft'
case, page = sys.argv[1], int(sys.argv[2])
clip = fitz.Rect(*map(float, sys.argv[3:7])) if len(sys.argv) >= 7 else None
out = []
for name, pdf in (('gd', os.path.join(ROOT, 'serverxml', case, 'GhostDraft.pdf')),
                  ('html', os.path.join(ROOT, 'html-snapshots', case, 'Html.pdf'))):
    with fitz.open(pdf) as d:
        pix = d[page - 1].get_pixmap(dpi=110, clip=clip)
        p = os.path.join(ROOT, 'html-snapshots', case, f'zoom-{name}.png')
        pix.save(p)
        out.append(Image.open(p))
w = sum(i.width for i in out) + 10
h = max(i.height for i in out)
im = Image.new('RGB', (w, h), 'white')
x = 0
for i in out:
    im.paste(i, (x, 0)); x += i.width + 10
p = os.path.join(ROOT, 'html-snapshots', case, 'zoom.png')
im.save(p)
print(p)
