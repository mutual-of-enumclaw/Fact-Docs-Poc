"""POST html/css to the designer's /api/render and print each page's words with positions."""
import json, sys, urllib.request
import fitz

html = open(sys.argv[1], encoding='utf-8').read()
css = open(sys.argv[2], encoding='utf-8').read() if len(sys.argv) > 2 else ''
req = urllib.request.Request('http://localhost:5199/api/render', data=json.dumps({'html': html, 'css': css}).encode(),
                             headers={'Content-Type': 'application/json'})
pdf = urllib.request.urlopen(req).read()
open(sys.argv[1] + '.pdf', 'wb').write(pdf)
doc = fitz.open(stream=pdf, filetype='pdf')
for i, p in enumerate(doc):
    print('--- page', i + 1)
    for b in p.get_text('dict')['blocks']:
        for l in b.get('lines', []):
            for s in l['spans']:
                print(f"  x={s['bbox'][0]:6.1f} y={s['bbox'][1]:6.1f} base={s['origin'][1]:6.1f} {s['size']:4.1f} {s['font']:<18} {s['text']!r}")
