"""Quick structural summary of converted templates: page sections, abs items, notes."""
import json, os, sys, glob

BATCH = r'C:\src\fact-poc\output\ghostdraft\batch'


def walk(c, acc, depth=0):
    t = c.get('type') or c.get('tagName')
    acc[t] = acc.get(t, 0) + 1
    for k in c.get('components') or []:
        walk(k, acc, depth + 1)


for pat in sys.argv[1:]:
    for f in glob.glob(os.path.join(BATCH, pat + '*.json')):
        d = json.load(open(f, encoding='utf-8'))
        print('==', os.path.basename(f), d['source'])
        for i, c in enumerate(d['components']):
            acc = {}
            walk(c, acc)
            print('  top', i, c.get('type'), c.get('classes'), {k: v for k, v in acc.items() if k in ('legacy-text', 'table', 'image', 'conditional', 'repeat', 'page-break')})
        for n in d['report']['notes']:
            print('  note:', n)
