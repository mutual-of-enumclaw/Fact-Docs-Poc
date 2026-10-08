"""Debug: outline of a converted template's top-level block tree. usage: gd_outline.py PACKAGE FORM [MAXLINES]"""
import sys
sys.path.insert(0, __file__.rsplit('\\', 1)[0])
import gd2designer as g

conv = g.Converter(sys.argv[1])
tree = conv.convert(sys.argv[2])
lines = g.outline(tree['doc'])
for l in lines[:int(sys.argv[3]) if len(sys.argv) > 3 else 200]:
    print(l)
for k, s in tree['sinks'].items():
    print('SINK', k, len(s['kids']) if s else None)
for b in tree['doc']['kids'][-6:]:
    print('TOP', b['k'], [x['k'] for x in b.get('kids', [])][:12])


def find(n, pred):
    if pred(n):
        return n
    for k in n.get('kids', []):
        r = find(k, pred)
        if r:
            return r


def brief(n, d=0):
    lab = n['k'] + ' ' + str(n.get('kind', '')) + ' ' + str(n.get('cond') or n.get('collection') or '')[:60]
    if n['k'] == 'run':
        lab += repr(n['t'][:40])
    print('  ' * d + lab)
    if n['k'] not in ('table',):
        for k in n.get('kids', []):
            brief(k, d + 1)


if len(sys.argv) > 4:
    def walk(n):
        if n.get('kind') == sys.argv[4]:
            brief(n)
            print('-----')
        for k in n.get('kids', []):
            walk(k)
    walk(tree['doc'])
