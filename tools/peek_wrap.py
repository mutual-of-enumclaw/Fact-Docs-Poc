import sys, fitz
d = fitz.open(r'C:\src\fact-poc\output\ghostdraft\serverxml\CA2016_1120\GhostDraft.pdf')
for b in d[2].get_text('dict')['blocks']:
    for l in b.get('lines', []):
        t = ''.join(s['text'] for s in l['spans'])
        if 'Specified Causes Of Loss' in t:
            print(l['bbox'], repr(t), [(s['font'], s['size']) for s in l['spans']][:2])
for dr in d[2].get_drawings()[:400]:
    for it in dr['items']:
        if it[0] == 'l' and abs(it[1].x - it[2].x) < 0.1 and 300 < it[1].y < 370:
            print('v', round(it[1].x, 2), round(dr.get('width') or 0, 2))
        if it[0] == 're' and it[1].width < 2 and 300 < it[1].y0 < 370:
            print('vr', round(it[1].x0, 2), round(it[1].x1, 2))
