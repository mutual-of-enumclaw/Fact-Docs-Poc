"""The complete name -> XML id rule, stated as "make it a valid NCName"."""
import collections, os, re, sys
sys.path.insert(0, 'tools')
from gdmodel import Model

PKG = 'output/iso-packages/commercial-auto-2607'
KEEP = re.compile(r'[^0-9A-Za-z_.-]')

def to_id(name):
    s = KEEP.sub('', name)
    if s and s[0].isdigit():
        s = '_' + s          # an XML NCName may not start with a digit
    return s

def main():
    libs = [os.path.join(PKG, 'Concept Libraries', f)
            for f in os.listdir(os.path.join(PKG, 'Concept Libraries')) if f.endswith('.gdm')]
    m = Model.load(os.path.join(PKG, 'model.xml'), concept_libs=libs)
    members = [(mem.name, mem.id) for _o, mem in m.member_by_guid.values() if mem.name and mem.id]

    res = collections.Counter(); other = []
    for n, i in members:
        base = to_id(n)
        if i == base:
            res['exact'] += 1
        elif re.fullmatch(re.escape(base) + r'[0-9]+', i):
            res['exact + collision suffix'] += 1
        else:
            res['UNEXPLAINED'] += 1
            other.append((n, i, base))
    tot = len(members)
    for k, v in sorted(res.items()):
        print(f'  {k:<28}{v:>7} / {tot}  ({100.0*v/tot:.2f}%)')
    ok = res['exact'] + res['exact + collision suffix']
    print(f'  {"RULE ACCOUNTS FOR":<28}{ok:>7} / {tot}  ({100.0*ok/tot:.2f}%)')
    if other:
        print(f'\nremaining ({len(other)}):')
        for n, i, b in other[:10]:
            print(f'    name={n!r}\n      id={i!r}  rule gave {b!r}')

main()
