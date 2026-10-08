"""Debug: every picture in a template (body + header/footer sinks) with its EMF+ extraction summary.
usage: gd_imgs.py PACKAGE FORM"""
import sys
sys.path.insert(0, __file__.rsplit('\\', 1)[0])
import gd2designer as g
import emfplus

conv = g.Converter(sys.argv[1])
tree = conv.convert(sys.argv[2])


def walk(n, path):
    if n.get('k') == 'img':
        emf = n.get('emf')
        info = ''
        if emf:
            s = emfplus.extract(emf, n.get('w') or 612, n.get('h') or 792)
            info = f"rules={len(s['rules'])} items={len(s['items'])} first={[i['text'] for i in s['items'][:6]]}"
        print(path, 'img', 'emf' if emf else ('src' if n.get('src') else 'NONE'), n.get('w'), n.get('h'), info)
    for k in n.get('kids', []):
        walk(k, path + '/' + k.get('k', '?'))


walk(tree['doc'], 'doc')
for name, s in tree['sinks'].items():
    if s:
        walk(s, name)
