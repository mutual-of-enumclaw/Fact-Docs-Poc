import sys, fitz
d = fitz.open(sys.argv[1])
for i, p in enumerate(d):
    best = []
    for b in p.get_text('dict')['blocks']:
        for l in b.get('lines', []):
            for s in l['spans']:
                best.append((round(s['bbox'][2], 1), round(s['bbox'][1], 1), s['text'][:50]))
    best.sort(reverse=True)
    print(i + 1, p.rect.width, best[:3])
