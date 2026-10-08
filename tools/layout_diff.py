"""layout_diff -- where the HTML render puts things differently from GhostDraft (positions, font sizes, rules).

    python tools/layout_diff.py CASE [--all]      one case, prints the moved words and missing rules
    (import) compare(gd_pdf, html_pdf) -> dict   used by side_by_side.py

Words: both PDFs' words (text, box, font size, bold) are paired in reading order with difflib; a pair is 'moved' when
its top-left differs by more than TOL pt, 'resized' when the font size differs by more than 0.5pt.
Rules: horizontal/vertical strokes from page.get_drawings() (lines and thin rects, box edges); a GhostDraft rule with no
HTML rule within TOL along the same axis is 'missing', the reverse 'extra'.
"""

from __future__ import annotations

import difflib
import os
import sys

import fitz

TOL = 2.0


def words(page):
    """Words from the characters: left edge of the first glyph, baseline, font size, bold. Pieces that touch
    (a word drawn as 'N' + 'amed') are one word, like on paper."""
    out = []
    d = page.get_text('rawdict')
    for b in d['blocks']:
        for ln in b.get('lines', []):
            for sp in ln['spans']:
                size, bold = round(sp['size'], 1), bool(sp['flags'] & 16) or 'bold' in sp['font'].lower()
                cur = None
                for ch in sp['chars']:
                    if ch['c'].isspace():
                        cur = None
                        continue
                    x0, _, x1, _ = ch['bbox']
                    if cur is None:
                        prev = out[-1] if out else None
                        if (prev and prev['open'] and abs(prev['y'] - ch['origin'][1]) < 0.6
                                and 0 <= x0 - prev['r'] < 0.15 * size):
                            cur = prev
                        else:
                            cur = {'t': '', 'x': x0, 'y': ch['origin'][1], 'size': size, 'bold': bold, 'open': True}
                            out.append(cur)
                    cur['t'] += ch['c']
                    cur['r'] = x1
                if out:
                    out[-1]['open'] = cur is not None      # a span ending in a space closes its last word
    out.sort(key=lambda w: (round(w['y'] / 3), w['x']))
    return out


def rules(page):
    """Horizontal and vertical strokes as (axis, fixed coord, start, end)."""
    out = []
    for dr in page.get_drawings():
        width = dr.get('width') or 0
        for item in dr['items']:
            if item[0] == 'l':
                p, q = item[1], item[2]
                out.extend(_seg(p.x, p.y, q.x, q.y, width))
            elif item[0] == 're':
                r = item[1]
                if r.width <= 3 or r.height <= 3:          # a filled thin rect is a line
                    out.extend(_seg(r.x0, (r.y0 + r.y1) / 2, r.x1, (r.y0 + r.y1) / 2, r.height) if r.width > r.height
                               else _seg((r.x0 + r.x1) / 2, r.y0, (r.x0 + r.x1) / 2, r.y1, r.width))
                elif dr.get('color') is not None:           # stroked box: four edges
                    out.extend(_seg(r.x0, r.y0, r.x1, r.y0, width) + _seg(r.x0, r.y1, r.x1, r.y1, width) +
                               _seg(r.x0, r.y0, r.x0, r.y1, width) + _seg(r.x1, r.y0, r.x1, r.y1, width))
            elif item[0] == 'qu':
                q = item[1].rect
                out.extend(_seg(q.x0, q.y0, q.x1, q.y0, width) + _seg(q.x0, q.y1, q.x1, q.y1, width) +
                           _seg(q.x0, q.y0, q.x0, q.y1, width) + _seg(q.x1, q.y0, q.x1, q.y1, width))
    return merge(out)


def _seg(x0, y0, x1, y1, w):
    if abs(y1 - y0) < 0.5 and abs(x1 - x0) > 2:
        return [('h', round((y0 + y1) / 2, 1), min(x0, x1), max(x0, x1))]
    if abs(x1 - x0) < 0.5 and abs(y1 - y0) > 2:
        return [('v', round((x0 + x1) / 2, 1), min(y0, y1), max(y0, y1))]
    return []


def merge(segs):
    """Join collinear touching pieces (table borders arrive cell by cell)."""
    segs = sorted(segs)
    out = []
    for s in segs:
        if out and out[-1][0] == s[0] and abs(out[-1][1] - s[1]) < 0.6 and s[2] <= out[-1][3] + 1.5:
            a = out[-1]
            out[-1] = (a[0], a[1], a[2], max(a[3], s[3]))
        else:
            out.append(s)
    return [s for s in out if s[3] - s[2] > 4]


def covered(s, others):
    return any(o[0] == s[0] and abs(o[1] - s[1]) <= TOL and o[2] <= s[2] + TOL * 3 and o[3] >= s[3] - TOL * 3
               for o in others)


def compare_page(gp, hp):
    gw, hw = words(gp), words(hp)
    sm = difflib.SequenceMatcher(a=[w['t'] for w in gw], b=[w['t'] for w in hw], autojunk=False)
    moved, resized, paired = [], [], 0
    for a, b, n in sm.get_matching_blocks():
        for i in range(n):
            g, h = gw[a + i], hw[b + i]
            paired += 1
            dx, dy = h['x'] - g['x'], h['y'] - g['y']
            if abs(dx) > TOL or abs(dy) > TOL:
                moved.append((g, h, dx, dy))
            if abs(g['size'] - h['size']) > 0.5 or g['bold'] != h['bold']:
                resized.append((g, h))
    gr, hr = rules(gp), rules(hp)
    missing = [s for s in gr if not covered(s, hr)]
    extra = [s for s in hr if not covered(s, gr)]
    return {'words': len(gw), 'paired': paired, 'moved': moved, 'resized': resized,
            'rules': len(gr), 'missing_rules': missing, 'extra_rules': extra}


def compare(gd_pdf, html_pdf):
    with fitz.open(gd_pdf) as g, fitz.open(html_pdf) as h:
        pages = []
        for i in range(max(len(g), len(h))):
            if i < len(g) and i < len(h):
                pages.append(compare_page(g[i], h[i]))
            else:
                pages.append(None)
        return pages


def summary(pages):
    s = {'words': 0, 'moved': 0, 'resized': 0, 'rules': 0, 'missing_rules': 0, 'extra_rules': 0}
    for p in pages:
        if p:
            for k in ('words',):
                s[k] += p[k]
            s['moved'] += len(p['moved'])
            s['resized'] += len(p['resized'])
            s['rules'] += p['rules']
            s['missing_rules'] += len(p['missing_rules'])
            s['extra_rules'] += len(p['extra_rules'])
    return s


def main():
    root = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'output', 'ghostdraft')
    case = sys.argv[1]
    show_all = '--all' in sys.argv
    pages = compare(os.path.join(root, 'serverxml', case, 'GhostDraft.pdf'),
                    os.path.join(root, 'html-snapshots', case, 'Html.pdf'))
    for n, p in enumerate(pages, 1):
        if not p:
            print(f'--- page {n}: only in one PDF')
            continue
        print(f'--- page {n}: {p["paired"]}/{p["words"]} words paired, {len(p["moved"])} moved, '
              f'{len(p["resized"])} resized; rules {p["rules"]}, missing {len(p["missing_rules"])}, '
              f'extra {len(p["extra_rules"])}')
        lim = None if show_all else 25
        for g, h, dx, dy in p['moved'][:lim]:
            print(f'  moved   {g["t"]!r:22} gd ({g["x"]:6.1f},{g["y"]:6.1f})  html ({h["x"]:6.1f},{h["y"]:6.1f})  '
                  f'd=({dx:+.1f},{dy:+.1f})')
        for g, h in p['resized'][:lim]:
            print(f'  resized {g["t"]!r:22} gd {g["size"]}pt{" B" if g["bold"] else ""}  '
                  f'html {h["size"]}pt{" B" if h["bold"] else ""}')
        for s in p['missing_rules'][:lim]:
            print(f'  missing rule {s[0]} at {s[1]:6.1f} from {s[2]:6.1f} to {s[3]:6.1f}')
        for s in p['extra_rules'][:lim]:
            print(f'  extra rule   {s[0]} at {s[1]:6.1f} from {s[2]:6.1f} to {s[3]:6.1f}')


if __name__ == '__main__':
    main()
