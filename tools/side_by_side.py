"""side_by_side -- visual comparison of GhostDraft (TST) vs designer HTML renders for the golden cases.

    python tools/side_by_side.py [--dpi 72]

Reads   output/ghostdraft/serverxml/{case}/GhostDraft.pdf   (fact-docgen FormRenderTests, SERVERXML_DUMP_DIR)
        output/ghostdraft/html-snapshots/{case}/Html.pdf    (FaCT.DocDesigner.POC.Tests)
        output/ghostdraft/html-snapshots/{case}/Diff.txt, summary.csv
Writes  output/ghostdraft/html-snapshots/index.html          all cases, worst score first
        output/ghostdraft/html-snapshots/{case}/compare.html page by page: GhostDraft | HTML | overlay
Overlay: black = both, red = GhostDraft only, cyan = HTML only.
"""

from __future__ import annotations

import argparse
import csv
import html
import os
import re

import fitz  # PyMuPDF
from PIL import Image, ImageChops, ImageOps

import layout_diff

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SNAPS = os.path.join(ROOT, 'output', 'ghostdraft', 'html-snapshots')
SERVERXML = os.path.join(ROOT, 'output', 'ghostdraft', 'serverxml')

CSS = """
body{font-family:Segoe UI,Arial,sans-serif;margin:16px;background:#f4f4f4;color:#222}
a{color:#0b5cad}
table.list{border-collapse:collapse;background:#fff}
table.list td,table.list th{border:1px solid #ccc;padding:3px 8px;text-align:left;font-size:13px}
tr.ok td{background:#e6f4ea}
.nav{margin:8px 0}
.row{display:flex;gap:10px;align-items:flex-start;margin:6px 0 18px}
.col{background:#fff;border:1px solid #bbb;padding:4px}
.col h3{margin:0 0 4px;font-size:13px;font-weight:600}
.col img{display:block;width:100%;height:auto;border:1px solid #ddd}
.empty{width:100%;aspect-ratio:8.5/11;display:flex;align-items:center;justify-content:center;color:#999;border:1px dashed #bbb}
pre{background:#fff;border:1px solid #ccc;padding:8px;font-size:12px;overflow:auto;max-height:420px}
label{font-size:13px;margin-right:12px}
body.no-overlay .ov{display:none}
"""


def rasterize(pdf_path, stem, dpi):
    if not os.path.exists(pdf_path):
        return []
    out = []
    with fitz.open(pdf_path) as doc:
        for i, page in enumerate(doc):
            png = f'{stem}-p{i + 1}.png'
            page.get_pixmap(dpi=dpi).save(png)
            out.append(png)
    return out


def overlay(gd_png, html_png, out_png):
    gd = ImageOps.grayscale(Image.open(gd_png))
    ht = ImageOps.grayscale(Image.open(html_png))
    if ht.size != gd.size:
        ht = ht.resize(gd.size)
    # R from HTML, G/B from GhostDraft: ink only in GhostDraft -> red, only in HTML -> cyan, both -> black.
    Image.merge('RGB', (ht, gd, gd)).save(out_png)
    diff = ImageChops.difference(gd, ht).point(lambda v: 255 if v > 64 else 0)
    return sum(diff.histogram()[255:]) / (gd.size[0] * gd.size[1])


def compare_page(case, row, dpi):
    d = os.path.join(SNAPS, case)
    # images of an earlier run with more pages would otherwise outlive it (the review page lists every overlay-p*.png)
    for name in os.listdir(d) if os.path.isdir(d) else []:
        if re.fullmatch(r'(gd|html|overlay)-p\d+\.png', name):
            os.remove(os.path.join(d, name))
    gd_pages = rasterize(os.path.join(SERVERXML, case, 'GhostDraft.pdf'), os.path.join(d, 'gd'), dpi)
    html_pages = rasterize(os.path.join(d, 'Html.pdf'), os.path.join(d, 'html'), dpi)
    diff_txt = ''
    if os.path.exists(os.path.join(d, 'Diff.txt')):
        with open(os.path.join(d, 'Diff.txt'), encoding='utf-8') as f:
            diff_txt = f.read()

    pixel = []
    rows = []
    gd_pdf, html_pdf = os.path.join(SERVERXML, case, 'GhostDraft.pdf'), os.path.join(d, 'Html.pdf')
    layout = layout_diff.compare(gd_pdf, html_pdf) if os.path.exists(gd_pdf) and os.path.exists(html_pdf) else []
    for i in range(max(len(gd_pages), len(html_pages))):
        cells = []
        for title, pages in (('GhostDraft (TST)', gd_pages), ('HTML designer', html_pages)):
            img = (f'<img src="{html.escape(os.path.basename(pages[i]))}" loading="lazy">'
                   if i < len(pages) else '<div class="empty">no page</div>')
            cells.append(f'<div class="col" style="flex:1"><h3>{title} &mdash; page {i + 1}</h3>{img}</div>')
        if i < len(gd_pages) and i < len(html_pages):
            ov = os.path.join(d, f'overlay-p{i + 1}.png')
            share = overlay(gd_pages[i], html_pages[i], ov)
            pixel.append(share)
            cells.append(f'<div class="col ov" style="flex:1"><h3>Overlay &mdash; {share:.1%} pixels differ</h3>'
                         f'<img src="{os.path.basename(ov)}" loading="lazy"></div>')
        else:
            cells.append('<div class="col ov" style="flex:1"><h3>Overlay</h3><div class="empty">page count differs</div></div>')
        rows.append('<div class="row">' + ''.join(cells) + '</div>')
        lp = layout[i] if i < len(layout) else None
        if lp and (lp['moved'] or lp['resized'] or lp['missing_rules'] or lp['extra_rules']):
            items = [f'moved {html.escape(g["t"])} ({dx:+.1f}, {dy:+.1f})pt' for g, h, dx, dy in lp['moved']]
            items += [f'size {html.escape(g["t"])} {g["size"]} -> {h["size"]}pt' for g, h in lp['resized']]
            items += [f'missing {s[0]} rule at {s[1]:.1f} ({s[2]:.1f}-{s[3]:.1f})' for s in lp['missing_rules']]
            items += [f'extra {s[0]} rule at {s[1]:.1f} ({s[2]:.1f}-{s[3]:.1f})' for s in lp['extra_rules']]
            rows.append(f'<details><summary>Page {i + 1} layout: {len(lp["moved"])} words moved, '
                        f'{len(lp["resized"])} resized, {len(lp["missing_rules"])} rules missing, '
                        f'{len(lp["extra_rules"])} extra</summary><pre>' + '\n'.join(items) + '</pre></details>')

    score = row.get('score', '')
    body = f"""<!DOCTYPE html><html><head><meta charset="utf-8"><title>{html.escape(case)}</title><style>{CSS}</style></head>
<body><div class="nav"><a href="../index.html">&larr; all cases</a></div>
<h2>{html.escape(case)} &mdash; {'MATCH' if row.get('match') == 'True' else 'different'} (word score {html.escape(score)})</h2>
<label><input type="checkbox" checked onchange="document.body.classList.toggle('no-overlay', !this.checked)"> overlay
(black = both, <span style="color:#d00">red = GhostDraft only</span>, <span style="color:#0aa">cyan = HTML only</span>)</label>
<details><summary>Text diff (Diff.txt)</summary><pre>{html.escape(diff_txt)}</pre></details>
{''.join(rows)}
</body></html>"""
    with open(os.path.join(d, 'compare.html'), 'w', encoding='utf-8') as f:
        f.write(body)
    return max(pixel) if pixel else None, len(gd_pages), len(html_pages), layout_diff.summary(layout)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--dpi', type=int, default=72)
    args = ap.parse_args()

    with open(os.path.join(SNAPS, 'summary.csv'), encoding='utf-8-sig') as f:
        rows = list(csv.DictReader(f))

    lines = []
    for row in rows:
        case = row['case']
        worst, gd_n, html_n, lay = compare_page(case, row, args.dpi)
        cls = ' class="ok"' if row['match'] == 'True' else ''
        lines.append(
            f'<tr{cls}><td><a href="{html.escape(case)}/compare.html">{html.escape(case)}</a></td>'
            f'<td>{"yes" if row["match"] == "True" else ""}</td><td>{float(row["score"]):.1%}</td>'
            f'<td>{gd_n} / {html_n}</td><td>{row["missing"]}</td><td>{row["extra"]}</td>'
            f'<td>{lay["moved"]} / {lay["words"]}</td><td>{lay["resized"]}</td>'
            f'<td>{lay["missing_rules"]} / {lay["rules"]}</td><td>{lay["extra_rules"]}</td>'
            f'<td>{"" if worst is None else f"{worst:.1%}"}</td></tr>')
        print(case, 'pages', gd_n, html_n, lay)

    index = f"""<!DOCTYPE html><html><head><meta charset="utf-8"><title>GhostDraft vs HTML designer</title><style>{CSS}</style></head>
<body><h2>GhostDraft (TST golden cases) vs HTML designer</h2>
<p>Word score = share of words matching the fact-docgen GoldenSnapshot.txt (order-insensitive).
Moved = words more than 2pt from their GhostDraft position; resized = different font size/weight;
rules = GhostDraft lines/borders with no HTML line within 2pt (missing) and the reverse (extra).
Pixel diff = worst page, share of pixels that differ in the overlay. Worst word score first.</p>
<table class="list"><tr><th>Case</th><th>Match</th><th>Word score</th><th>Pages GD / HTML</th><th>Missing</th><th>Extra</th>
<th>Moved words</th><th>Resized</th><th>Missing rules</th><th>Extra rules</th><th>Pixel diff</th></tr>
{''.join(lines)}</table></body></html>"""
    with open(os.path.join(SNAPS, 'index.html'), 'w', encoding='utf-8') as f:
        f.write(index)
    print(os.path.join(SNAPS, 'index.html'))


if __name__ == '__main__':
    main()
