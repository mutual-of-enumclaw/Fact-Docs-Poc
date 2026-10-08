"""peek_pages.py CASE [DPI] - render every page of GhostDraft.pdf and Html.pdf side by side into {case}\pages.png."""
import sys
import fitz
from PIL import Image

case = sys.argv[1]
dpi = int(sys.argv[2]) if len(sys.argv) > 2 else 40
base = r'C:\src\fact-poc\output\ghostdraft'


def imgs(p):
    d = fitz.open(p)
    out = []
    for pg in d:
        pm = pg.get_pixmap(dpi=dpi)
        out.append(Image.frombytes('RGB', (pm.width, pm.height), pm.samples))
    return out


g = imgs(rf'{base}\serverxml\{case}\GhostDraft.pdf')
h = imgs(rf'{base}\html-snapshots\{case}\Html.pdf')
w, hh = g[0].size
n = max(len(g), len(h))
sheet = Image.new('RGB', (n * (w + 6), 2 * hh + 6), 'gray')
for i, im in enumerate(g):
    sheet.paste(im, (i * (w + 6), 0))
for i, im in enumerate(h):
    sheet.paste(im, (i * (w + 6), hh + 6))
out = rf'{base}\html-snapshots\{case}\pages.png'
sheet.save(out)
print(out, len(g), len(h))
