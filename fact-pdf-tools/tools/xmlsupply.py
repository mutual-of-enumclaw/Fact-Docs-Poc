"""Join the DEMAND the ISO templates state to the SUPPLY fact-docgen actually emits.

Demand comes from `gdbindings.py`: the Server XML paths the 491 production
templates bind, each with the number of templates that need it.

Supply comes from Server XML that was really generated -- the `Builder.xml` /
`Service.xml` artefacts the fact-docgen integration tests write for real
policies. That is the authority, not a regex over the section-builder source:
HANDOFF.md records five artefacts in one day traced to reading a proxy instead,
and "CDM properties scraped from builder expressions" was one of them.

READ THIS BEFORE QUOTING A COVERAGE NUMBER. Supply is a FLOOR, for one specific
reason: a policy's XML contains only the sections its own coverages trigger. 65
policies do not exercise 2,140 paths. So a path reported MISSING is missing
*from this sample*, which is evidence about the sample as much as about the
builder. What IS sound:

  * the SUPPLIED set is exact -- if a path appears with a value, the builder can
    produce it, full stop;
  * the RANKING of missing paths by template demand, because demand comes from
    the package and does not depend on the sample at all.

Three states are distinguished, because "present" is not one thing:

    supplied        the element appears with a non-empty value somewhere
    empty-only      the element appears in every case, always empty
    missing         the element never appears in the sample

Usage:
    python tools/xmlsupply.py <bindings.csv> <dir-of-server-xml> [--kind Builder]
    python tools/xmlsupply.py ... --out output/xmlsupply.csv
"""

from __future__ import annotations

import argparse
import collections
import csv
import glob
import os
import xml.etree.ElementTree as ET


def paths_in(xml_path: str) -> tuple[dict[str, bool], str]:
    """Every element path in one document -> whether it ever held a value.

    The root is stripped: demand paths are relative to the model root (`Policy`,
    `CAAutoLevelCoverages`, ...) and the artefacts wrap them in one root element.
    """
    seen: dict[str, bool] = {}
    root = ET.parse(xml_path).getroot()

    def walk(e, prefix: str) -> None:
        for c in e:
            tag = c.tag.rsplit('}', 1)[-1]
            p = f'{prefix}/{tag}' if prefix else tag
            has_value = bool((c.text or '').strip())
            seen[p] = seen.get(p, False) or has_value
            walk(c, p)

    walk(root, '')
    return seen, root.tag


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('bindings_csv')
    ap.add_argument('xml_dir')
    ap.add_argument('--kind', default='Builder',
                    help='artefact file name without extension (Builder | Service), '
                         'or a glob such as "*" for a package Test Cases folder')
    ap.add_argument('--out', default=None)
    ap.add_argument('--top', type=int, default=30)
    a = ap.parse_args()

    # `--kind Builder` matches BuilderRenderOutput's fixed file names; a package's
    # `Test Cases/` folder names each instance after the scenario, so accept a
    # glob too ('*' takes every .xml in the tree).
    pat = a.kind if any(c in a.kind for c in '*?[') else a.kind + '.xml'
    if not pat.endswith('.xml'):
        pat += '.xml'
    files = sorted(glob.glob(os.path.join(a.xml_dir, '**', pat), recursive=True))
    if not files:
        print(f'no {pat} under {a.xml_dir}')
        return 1

    # ---- supply
    supplied: set[str] = set()
    present: set[str] = set()
    roots: collections.Counter = collections.Counter()
    top_level: collections.Counter = collections.Counter()
    for f in files:
        try:
            seen, root = paths_in(f)
        except ET.ParseError as e:
            print(f'  !! {f}: {e}')
            continue
        roots[root] += 1
        for p, had in seen.items():
            present.add(p)
            if had:
                supplied.add(p)
            if '/' not in p:
                top_level[p] += 1

    # ---- demand
    rows = list(csv.DictReader(open(a.bindings_csv, encoding='utf-8')))
    # A list SORT KEY is demand too: the list cannot be ordered without it, and
    # 81 templates sort autos by Auto/VehicleNumber. Adornment rows are excluded
    # -- they are formatting, and carry no path.
    fp = [r for r in rows if r['kind'] in ('fillpoint', 'orderby') and r['xml_path']]
    demand: dict[str, set[str]] = collections.defaultdict(set)
    for r in fp:
        demand[r['xml_path']].add(r['form'])

    def state(p: str) -> str:
        if p in supplied:
            return 'supplied'
        if p in present:
            return 'empty-only'
        return 'missing'

    buckets: collections.Counter = collections.Counter()
    weighted: collections.Counter = collections.Counter()   # by template demand
    for p, forms in demand.items():
        s = state(p)
        buckets[s] += 1
        weighted[s] += len(forms)

    total = len(demand)
    tw = sum(len(v) for v in demand.values())
    print(f'artefacts            {len(files)} x {a.kind}.xml   roots {dict(roots)}')
    print(f'distinct paths seen  {len(present)}   with a value {len(supplied)}')
    print()
    print(f'DEMAND: {total} distinct data paths (fill points + sort keys) over {len({r["form"] for r in fp})} templates')
    print(f'{"state":<14}{"paths":>8}{"":>4}{"template-weighted":>20}')
    for s in ('supplied', 'empty-only', 'missing'):
        print(f'{s:<14}{buckets[s]:>8}  {100.0*buckets[s]/total:>5.1f}%'
              f'{weighted[s]:>12}  {100.0*weighted[s]/tw:>5.1f}%')
    print()
    print(f'  ^ MISSING is missing FROM THIS SAMPLE. {len(files)} policies cannot')
    print(f'    exercise {total} paths, so this is a floor on supply. The ranking')
    print('    below does not depend on the sample -- demand comes from the package.')

    gaps = sorted(((len(f), p) for p, f in demand.items() if state(p) == 'missing'),
                  reverse=True)
    print(f'\n=== top {a.top} unmet paths by template demand ===')
    for n, p in gaps[:a.top]:
        print(f'{n:5d}  {p}')

    # A root section the builder NEVER emits is a different claim from a path
    # the sample happens not to reach: the first does not depend on the sample
    # at all. Pooling them into one "missing" number hides the only part of it
    # that is sample-independent, so they are separated here.
    by_root: dict[str, collections.Counter] = collections.defaultdict(collections.Counter)
    for p in demand:
        by_root[p.split('/')[0]][state(p)] += 1
    print('\n=== by root element ===')
    print(f'{"root":<44}{"paths":>7}{"suppl":>7}{"empty":>7}{"miss":>7}  emitted?')
    hard = []
    for root in sorted(by_root, key=lambda r: -sum(by_root[r].values())):
        c = by_root[root]
        n = sum(c.values())
        at_top = top_level.get(root, 0)
        if not at_top:
            hard.append((n, root))
        flag = f'{at_top}/{len(files)}' if at_top else 'NEVER'
        print(f'{root:<44}{n:>7}{c["supplied"]:>7}{c["empty-only"]:>7}'
              f'{c["missing"]:>7}  {flag}')

    if hard:
        tot = sum(n for n, _ in hard)
        print(f'\n=== root sections the builder NEVER emits ({len(hard)}) ===')
        print('    Sample-independent: the element is absent from every artefact,')
        print('    so no policy mix would surface it. This IS a builder gap.')
        for n, root in sorted(hard, reverse=True):
            print(f'{n:5d}  {root}')
        print(f'{tot:5d}  TOTAL  ({100.0 * tot / total:.1f}% of {total} demanded paths)')

    if a.out:
        os.makedirs(os.path.dirname(a.out) or '.', exist_ok=True)
        with open(a.out, 'w', newline='', encoding='utf-8') as fh:
            w = csv.writer(fh)
            w.writerow(['xml_path', 'templates_needing', 'state', 'root'])
            for p, forms in sorted(demand.items(), key=lambda kv: -len(kv[1])):
                w.writerow([p, len(forms), state(p), p.split('/')[0]])
        print(f'\nwrote {a.out}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
