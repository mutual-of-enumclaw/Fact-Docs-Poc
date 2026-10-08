"""Bisect Chrome shrink-to-fit: render Template.html variants via /api/render and report the scale."""
import json, re, sys, urllib.request, fitz

case = sys.argv[1]
base = rf'C:\src\fact-poc\output\ghostdraft\html-snapshots\{case}'
t = open(base + r'\Template.html', encoding='utf-8').read()
data = json.load(open(base + r'\Data.json', encoding='utf-8'))
css = t[len('<style>'):t.index('</style>')]
html = t[t.index('</style>') + len('</style>'):]


def render(h, c):
    req = urllib.request.Request('http://localhost:5199/api/render', json.dumps({'html': h, 'css': c, 'data': data}).encode(),
                                 {'Content-Type': 'application/json'})
    pdf = urllib.request.urlopen(req).read()
    d = fitz.open(stream=pdf, filetype='pdf')
    sizes = [s['size'] for p in d for b in p.get_text('dict')['blocks'] for l in b.get('lines', []) for s in l['spans']]
    return len(d), sorted(set(round(x, 2) for x in sizes))[:6]


print('full', render(html, css))
for extra in sys.argv[2:]:
    print(extra, render(html, css + extra))
