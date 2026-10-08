"""peek_lines.py CASE PAGE [XMAX] - baseline y of each text line (left column) in GhostDraft.pdf vs Html.pdf."""
import sys
import fitz

case, page = sys.argv[1], int(sys.argv[2])
xmax = float(sys.argv[3]) if len(sys.argv) > 3 else 200
base = rf'C:\src\fact-poc\output\ghostdraft'


def lines(path):
    d = fitz.open(path)
    out = []
    for b in d[page - 1].get_text('dict')['blocks']:
        for l in b.get('lines', []):
            sp = [s for s in l['spans'] if s['text'].strip() and s['bbox'][0] < xmax]
            if sp:
                out.append((round(sp[0]['origin'][1], 1), round(sp[0]['bbox'][0], 1), ''.join(s['text'] for s in sp)[:40]))
    return sorted(set(out))


g = lines(rf'{base}\serverxml\{case}\GhostDraft.pdf')
h = lines(rf'{base}\html-snapshots\{case}\Html.pdf')
for i in range(max(len(g), len(h))):
    a = g[i] if i < len(g) else ('', '', '')
    b = h[i] if i < len(h) else ('', '', '')
    dy = round(b[0] - a[0], 1) if a[0] != '' and b[0] != '' else ''
    print(f'{a[0]!s:>7} {a[2]:<40} | {b[0]!s:>7} {b[2]:<40} {dy}')
