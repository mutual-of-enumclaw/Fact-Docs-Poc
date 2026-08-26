"""Diff the extracted bindings against GhostDraft's OWN Integration Specification
-- a third authority, independent of both `model.xml` and the package XSD.

The Integration Specification zip ships one `.txt` per template listing its
"Used Domain Paths". That is GhostDraft telling us, in its own words, which
domain nodes a template touches. The XSD check (`gdxsdcheck.py`) only proves a
resolved path EXISTS; this proves we found the RIGHT SET of paths per template,
which is the claim `gdbindings.py` actually makes.

What is compared, and why not the literal strings
-------------------------------------------------
The two notations differ on purpose and matching them character-for-character
would be overfitting to a text format:

    spec:  CAAutoLevelCoverages.Items[].VehicleDescription
    ours:  CAAutoLevelCoverages/Items/Auto/VehicleDescription

and the spec drops a selector from the paths of its descendants (it is a filter,
not a step -- the same conclusion `gdmodel` reaches for the XML path) while
listing it in its own right. So both sides are canonicalised to a token tuple:

  * split on `.` or `/`
  * drop the `Items` wrapper and the list element name -- a list hop carries no
    identity in the spec's notation
  * drop trailing PREDICATE segments: built-ins (`is provided`, `is First`,
    `has 2 or more elements`) and selector/test ids, which the model declares,
    so they are recognised rather than pattern-guessed

What remains on both sides is the set of DATA-BEARING element paths, which is
what a binding is.

Transitivity: the spec's per-template list includes the paths of every document
the template SUBSCRIBES to (that is how `Policy.usesoverflowscheduleforadditional
insureds` reaches `CA 21 34 10 13 Washington Underinsured Motorists Coverage`
from its header/footer). So our side is compared as a transitive closure over
`subscriptionType` edges.

Usage:
    python tools/gdspeccheck.py <bindings.csv> <package-dir> <spec.zip>
    python tools/gdspeccheck.py ... --form "CA 21 34 10 13 Schedule"
"""

from __future__ import annotations

import argparse
import collections
import csv
import os
import sys
import zipfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdmodel import Model, is_builtin_test   # noqa: E402


def read_spec(zip_path: str) -> dict[str, list[str]]:
    """template name -> the raw 'Used Domain Paths' lines."""
    out: dict[str, list[str]] = {}
    with zipfile.ZipFile(zip_path) as z:
        for n in z.namelist():
            if not n.lower().endswith('.txt') or not n.startswith('Templates/'):
                continue
            name = os.path.splitext(os.path.basename(n))[0]
            text = z.read(n).decode('utf-8', 'replace')
            if 'Used Domain Paths:' not in text:
                out[name] = []
                continue
            body = text.split('Used Domain Paths:', 1)[1]
            paths = []
            for line in body.splitlines():
                t = line.strip()
                if not t or t.startswith('*'):
                    continue
                paths.append(t.rstrip('*'))
            out[name] = paths
    return out


class Canon:
    """Canonicalise a path from either notation to a comparable token tuple."""

    def __init__(self, model: Model) -> None:
        # Every selector / test id the model declares, so a predicate segment is
        # RECOGNISED rather than guessed from its shape.
        self.predicate_ids: set[str] = set()
        self.list_element_ids: set[str] = set()
        # The spec mostly writes element ids but sometimes writes the DISPLAY
        # name (`CAAutoLevelCoverages.Vehicle Number`). The id is the name with
        # spaces, commas and parentheses stripped -- true for 7,936 of 12,486
        # members and the rest follow the same rule with more punctuation -- but
        # the model states the mapping outright, so use that and keep stripping
        # only as a fallback.
        self.name_to_id: dict[str, str] = {}
        self.known_ids: set[str] = set()
        for _owner, mem in model.member_by_guid.values():
            if mem.kind in ('selector', 'test', 'mutexTestGroup') and mem.id:
                self.predicate_ids.add(mem.id)
            if mem.id:
                self.known_ids.add(mem.id)
            if mem.name and mem.id:
                self.name_to_id.setdefault(mem.name, mem.id)
        for t in model.types.values():
            if t.is_list and t.element_id:
                self.list_element_ids.add(t.element_id)
            if t.is_list and t.element_name and t.element_id:
                self.name_to_id.setdefault(t.element_name, t.element_id)
        # `contains the X` tests live only in the concept library, so model.xml
        # cannot name them. The spec keeps them as a path segment; gdmodel
        # follows the indirection to the selector they stand for. Both are
        # right, and for comparison both are predicates.
        for name in model.concepts.test_names.values():
            if not name:
                continue
            self.predicate_ids.add(name)
            self.predicate_ids.add(name.translate(str.maketrans('', '', ' ,()')))

    def _as_id(self, tok: str) -> str:
        if tok in self.known_ids:
            return tok
        mapped = self.name_to_id.get(tok)
        if mapped:
            return mapped
        return tok.translate(str.maketrans('', '', ' ,()'))

    def __call__(self, path: str) -> tuple[str, ...] | None:
        raw = path.replace('/', '.').split('.')
        toks: list[str] = []
        skip_next_element = False
        for tok in raw:
            tok = tok.strip()
            if not tok:
                continue
            if tok == 'Items[]':
                continue
            if tok == 'Items':
                skip_next_element = True     # our notation names the element
                continue
            if is_builtin_test(tok):
                continue                     # a predicate, not a data element
            tok = self._as_id(tok)
            if skip_next_element:
                skip_next_element = False
                if tok in self.list_element_ids:
                    continue
            if tok in self.predicate_ids:
                continue
            toks.append(tok)
        return tuple(toks) if toks else None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('bindings_csv')
    ap.add_argument('package')
    ap.add_argument('spec_zip')
    ap.add_argument('--form', default=None)
    ap.add_argument('--show', type=int, default=6)
    a = ap.parse_args()

    libs = [os.path.join(a.package, 'Concept Libraries', f)
            for f in os.listdir(os.path.join(a.package, 'Concept Libraries'))
            if f.endswith('.gdm')]
    model = Model.load(os.path.join(a.package, 'model.xml'), concept_libs=libs)
    canon = Canon(model)

    spec = read_spec(a.spec_zip)
    print(f'spec templates      {len(spec)}')

    rows = list(csv.DictReader(open(a.bindings_csv, encoding='utf-8')))
    ours: dict[str, set[tuple]] = collections.defaultdict(set)
    subs: dict[str, set[str]] = collections.defaultdict(set)
    lower = {k.lower(): k for k in spec}
    for r in rows:
        if r['kind'] == 'subscription':
            subs[r['form']].add(r['concept_path'])
            continue
        if r['resolved'] != '1':
            continue
        for p in (r['xml_path'], r['spec_path']):
            c = canon(p) if p else None
            if c:
                ours[r['form']].add(c)

    # transitive closure over subscriptions -- the spec's lists are transitive
    def closure(form: str, seen: set[str] | None = None) -> set[tuple]:
        seen = seen if seen is not None else set()
        if form in seen:
            return set()
        seen.add(form)
        acc = set(ours.get(form, ()))
        for target in subs.get(form, ()):
            t = target if target in ours or target in spec else lower.get(target.lower())
            if t:
                acc |= closure(t, seen)
        return acc

    # The spec documents 1,110 templates; the .gdsp ships 491, all of them a
    # subset of the spec's set. Comparing against all 1,110 would score 619
    # templates we have no .gd for as total misses, which measures the package
    # export, not the extraction. Restrict to the intersection and report it.
    packaged = {os.path.splitext(f)[0]
                for f in os.listdir(os.path.join(a.package, 'Templates'))
                if f.endswith('.gd')}
    if a.form:
        names = [a.form]
    else:
        names = sorted(spec.keys() & packaged)
        print(f'packaged templates  {len(packaged)}   spec-only '
              f'{len(spec.keys() - packaged)} (no .gd in this package, skipped)')
    tot_spec = tot_ours = tot_common = 0
    missed: collections.Counter = collections.Counter()
    extra: collections.Counter = collections.Counter()
    per_form = []
    for name in names:
        if name not in spec:
            print(f'  !! {name!r} not in the spec zip')
            continue
        s = {c for c in (canon(p) for p in spec[name]) if c}
        o = closure(name)
        tot_spec += len(s)
        tot_ours += len(o)
        tot_common += len(s & o)
        for c in s - o:
            missed['.'.join(c)] += 1
        for c in o - s:
            extra['.'.join(c)] += 1
        per_form.append((name, len(s), len(o), len(s & o)))

    exact = sum(1 for _n, ns, no, nc in per_form if ns == nc == no)
    covered = sum(1 for _n, ns, _no, nc in per_form if ns == nc)
    print(f'templates compared  {len(per_form)}')
    print()
    print(f'spec paths (canonical)     {tot_spec}')
    print(f'our paths  (canonical)     {tot_ours}')
    print(f'in both                    {tot_common}')
    print(f'  spec recall              {100.0 * tot_common / tot_spec:.2f}%'
          if tot_spec else '')
    print(f'  our precision            {100.0 * tot_common / tot_ours:.2f}%'
          if tot_ours else '')
    print()
    print(f'templates where we found EVERY spec path   {covered}/{len(per_form)}')
    print(f'templates matching the spec set EXACTLY    {exact}/{len(per_form)}')

    if missed:
        print(f'\n=== spec paths we did NOT find ({sum(missed.values())} '
              f'over {len(missed)} distinct) ===')
        for p, n in missed.most_common(a.show):
            print(f'{n:5d}  {p}')
    if extra:
        print(f'\n=== paths we found that the spec does NOT list '
              f'({sum(extra.values())} over {len(extra)} distinct) ===')
        for p, n in extra.most_common(a.show):
            print(f'{n:5d}  {p}')

    worst = sorted(per_form, key=lambda t: (t[3] - t[1], t[3] - t[2]))[:a.show]
    print('\n=== templates with the largest disagreement ===')
    print(f'{"template":<58}{"spec":>6}{"ours":>6}{"both":>6}')
    for name, ns, no, nc in worst:
        print(f'{name[:57]:<58}{ns:>6}{no:>6}{nc:>6}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
