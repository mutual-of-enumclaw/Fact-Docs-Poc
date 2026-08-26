"""Verify resolved Server XML paths against the package XSD -- an authority the
resolver never read.

Why this is separate from `gdbindings.py`
----------------------------------------
`gdbindings.py` resolves template GUID paths through `model.xml`. If that
projection is misunderstood in some systematic way, the resolver produces
plausible paths and reports 100% success -- exactly the failure mode HANDOFF.md
warns about ("a gate that cannot FAIL is worse still", "read the authority, do
not infer from a proxy"). The `.gdsp` ships a GDXSD schema generated from the
same model by a different code path, so walking every resolved path through the
XSD is an independent check with a real chance of failing.

It also has to be able to fail on purpose: `--selftest` mutates known-good
paths (drop a step, rename an element, drop the `Items` hop, swap a type) and
asserts the checker rejects each one.

Usage:
    python tools/gdxsdcheck.py <bindings.csv> <package.xsd>
    python tools/gdxsdcheck.py <bindings.csv> <package.xsd> --selftest
"""

from __future__ import annotations

import argparse
import csv
import sys
import xml.etree.ElementTree as ET
from collections import Counter

XS = '{http://www.w3.org/2001/XMLSchema}'


class Xsd:
    """Just enough XSD to walk element paths: complexType -> {element: type}."""

    def __init__(self, path: str) -> None:
        root = ET.parse(path).getroot()
        self.target_ns = root.get('targetNamespace', '')
        self.types: dict[str, dict[str, str]] = {}
        self.root_elements: dict[str, str] = {}

        for e in root:
            if e.tag == XS + 'element' and e.get('name') and e.get('type'):
                self.root_elements[e.get('name')] = e.get('type')
            elif e.tag == XS + 'complexType' and e.get('name'):
                self.types[e.get('name')] = self._members(e)

    def _members(self, ct) -> dict[str, str]:
        """Children of a complexType, including the anonymous `Items` wrapper.

        `Items` has an inline complexType, so it is registered under a synthetic
        name `<owner>#Items` and its child element resolved from there.
        """
        out: dict[str, str] = {}
        owner = ct.get('name', '')
        for group in ct:
            if group.tag not in (XS + 'all', XS + 'sequence', XS + 'choice'):
                continue
            for el in group:
                if el.tag != XS + 'element':
                    continue
                name = el.get('name')
                if not name:
                    continue
                t = el.get('type')
                if t:
                    out[name] = t
                    continue
                inline = None
                for c in el:
                    if c.tag == XS + 'complexType':
                        inline = c
                if inline is not None:
                    syn = f'{owner}#{name}'
                    inline.set('name', syn)
                    self.types[syn] = self._members(inline)
                    out[name] = syn
                else:
                    out[name] = 'xs:anyType'
        return out

    # ------------------------------------------------------------------ walk

    def walk(self, root_element: str, path: str) -> tuple[bool, str, str]:
        """(ok, leaf_type, reason). `path` is `A/B/Items/C/D` under root_element."""
        t = self.root_elements.get(root_element)
        if t is None:
            return False, '', f'root element {root_element!r} not in schema'
        cur = t
        for step in [s for s in path.split('/') if s]:
            members = self.types.get(cur)
            if members is None:
                return False, cur, f'type {cur!r} is simple; cannot descend to {step!r}'
            nxt = members.get(step)
            if nxt is None:
                return False, cur, f'{step!r} is not an element of {cur!r}'
            cur = nxt
        return True, cur, ''


def check(bindings_csv: str, xsd_path: str) -> int:
    xsd = Xsd(xsd_path)
    root_name = next(iter(xsd.root_elements))
    rows = list(csv.DictReader(open(bindings_csv, encoding='utf-8')))

    checked = 0
    ok = 0
    fails: list[tuple[str, str, str]] = []
    by_kind: Counter = Counter()
    for r in rows:
        p = r['xml_path']
        if not p or r['resolved'] != '1':
            continue
        checked += 1
        good, leaf, why = xsd.walk(root_name, p)
        if good:
            ok += 1
            by_kind[r['kind']] += 1
        else:
            fails.append((r['form'], p, why))

    print(f'schema      {xsd_path}')
    print(f'root        {root_name}')
    print(f'complexTypes {len(xsd.types)}')
    print()
    print(f'paths checked {checked}')
    print(f'in schema     {ok}   ({100.0 * ok / checked:.2f}%)' if checked else 'nothing to check')
    print(f'NOT in schema {len(fails)}')
    if fails:
        print('\nfailures by reason:')
        for why, n in Counter(w for _, _, w in fails).most_common(15):
            print(f'  {n:>6}  {why}')
        print('\nfirst 10:')
        for form, p, why in fails[:10]:
            print(f'  {form}\n      {p}\n      {why}')
    print('\npaths verified by instruction kind:')
    for k, n in by_kind.most_common():
        print(f'  {k:<22} {n}')
    return 0 if not fails else 1


def selftest(bindings_csv: str, xsd_path: str) -> int:
    """A checker that cannot reject a wrong path proves nothing about a right one."""
    xsd = Xsd(xsd_path)
    root_name = next(iter(xsd.root_elements))
    rows = [r for r in csv.DictReader(open(bindings_csv, encoding='utf-8'))
            if r['xml_path'] and r['resolved'] == '1' and r['kind'] == 'fillpoint']
    if not rows:
        print('no fillpoint rows to mutate'); return 1

    # Pick paths deep enough that every mutation is meaningful.
    deep = [r['xml_path'] for r in rows if r['xml_path'].count('/') >= 3]
    sample = sorted(set(deep))[:200]
    print(f'selftest over {len(sample)} distinct deep fillpoint paths\n')

    def mutations(p: str):
        parts = p.split('/')
        yield 'drop-a-middle-step', '/'.join(parts[:1] + parts[2:])
        yield 'rename-leaf', '/'.join(parts[:-1] + [parts[-1] + 'X'])
        yield 'drop-Items-hop', '/'.join(x for x in parts if x != 'Items') if 'Items' in parts else None
        yield 'reverse-path', '/'.join(reversed(parts))
        yield 'extra-step-past-leaf', p + '/Bogus'

    results: Counter = Counter()
    for p in sample:
        good, _, _ = xsd.walk(root_name, p)
        results['baseline-accepted' if good else 'BASELINE-REJECTED'] += 1
        for label, mp in mutations(p):
            if mp is None or mp == p:
                continue
            g, _, _ = xsd.walk(root_name, mp)
            results[f'{label}: {"WRONGLY ACCEPTED" if g else "rejected"}'] += 1

    for k, v in sorted(results.items()):
        print(f'  {v:>6}  {k}')

    bad = sum(v for k, v in results.items()
              if 'WRONGLY ACCEPTED' in k or k == 'BASELINE-REJECTED')
    print()
    if bad:
        print(f'SELFTEST FAIL: {bad} outcomes the checker got wrong')
        print('  (a mutation may coincide with a real path -- inspect before dismissing)')
        return 1
    print('SELFTEST PASS: baseline accepted, every mutation rejected')
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('bindings_csv')
    ap.add_argument('xsd')
    ap.add_argument('--selftest', action='store_true')
    a = ap.parse_args()
    return selftest(a.bindings_csv, a.xsd) if a.selftest else check(a.bindings_csv, a.xsd)


if __name__ == '__main__':
    sys.exit(main())
