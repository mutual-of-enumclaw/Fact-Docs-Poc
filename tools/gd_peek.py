"""Debug helper: print a .gd template's instruction tree (IDs/types) and the marker sequence in its RTF
with a crude plain-text rendering between markers."""
import re
import sys
import xml.etree.ElementTree as ET

XSI = '{http://www.w3.org/2001/XMLSchema-instance}type'


def local(t):
    return t.rsplit('}', 1)[-1]


def child(e, n):
    for c in e:
        if local(c.tag) == n:
            return c
    return None


def tree(instrs, depth=0):
    for i in instrs:
        if local(i.tag) != 'instruction':
            continue
        d = child(i, 'description')
        print('  ' * depth + f"I{i.get('ID')} {i.get(XSI)} :: {(d.text or '').strip() if d is not None else ''}")
        for extra in ('iterator', 'iteratorGuid', 'displayOnOwnLine'):
            x = child(i, extra)
            if x is not None and (x.text or '').strip():
                print('  ' * depth + f"   {extra}={x.text.strip()}")
        parts = child(i, 'parts')
        for p in (list(parts) if parts is not None else []):
            if local(p.tag) != 'part':
                continue
            pd = child(p, 'description')
            neg = child(p, 'isNegative')
            print('  ' * (depth + 1) + f"P{p.get('ID')} {p.get(XSI)} neg={neg.text if neg is not None else ''} :: {(pd.text or '').strip() if pd is not None else ''}")
            inner = child(p, 'instructions')
            if inner is not None:
                tree(inner, depth + 2)


def plain(rtf):
    s = re.sub(r'\{\\\*[^{}]*\}', '', rtf)
    s = re.sub(r'\\par[d]?\b', '\n', s)
    s = re.sub(r'\\cell\b', ' | ', s)
    s = re.sub(r'\\[a-zA-Z]+-?\d* ?', '', s)
    s = s.replace('{', '').replace('}', '')
    return re.sub(r'[ \t]+', ' ', s)


root = ET.parse(sys.argv[1]).getroot()
doc = child(root, 'document')
mo = child(doc, 'markup')
for m in mo:
    if local(m.tag) == 'markup':
        ins = child(m, 'instructions')
        if ins is not None:
            tree(ins)
rtf = child(child(doc, 'content'), 'rtf').text
print('---- RTF markers ----')
txt = plain(rtf)
for seg in re.split(r'(%\[[^\]]*\])', txt):
    if seg.startswith('%['):
        print(f'<<{seg}>>', end='')
    else:
        t = re.sub(r'\s*\n\s*', ' / ', seg.strip())
        print(t[:160] + ('…' if len(t) > 160 else ''), end='')
print()
