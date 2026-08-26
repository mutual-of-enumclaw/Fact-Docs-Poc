"""Extract every data binding from the `.gd` templates of a GhostDraft package
and resolve it, through `model.xml`, to a Server XML path.

This is the instrument for the matched-pair question in HANDOFF.md: "how does a
field become a binding". It answers it from the 491 production templates rather
than from a design guess.

What a production template actually contains
--------------------------------------------
`<content><rtf>` is the LAYOUT, and carries `%[ID]` markers.
`<markup><markup><instructions>` is the LOGIC, a tree of typed instructions
keyed by the same IDs. Five types exist in the Commercial Auto package:

    fillPointType             14107   emit a value          -> has <path>
    conditionalInstructionType 10362  if/else               -> parts carry <path>
    subscriptionType            885   include another .gd   -> document name
    listInstructionType         691   iterate               -> has <pathToList>
    annotationType               10   reviewer note

Every `<path>` is a GUID tuple against a concept library, never a string path.
`gdmodel.Model` turns it into the Server XML path -- that projection is the
package's own, so this is a reading of the authority, not an inference.

Scope: a `listInstructionType` declares `<iterator>`/`<iteratorGuid>`; fill
points nested inside it root their paths at that guid. The walk therefore
carries a scope map guid -> (xml_path, type_id).

Usage:
    python tools/gdbindings.py <package-dir> [--out output/gdbindings.csv]
    python tools/gdbindings.py <package-dir> --form "CA 21 34 10 13 Schedule"
"""

from __future__ import annotations

import argparse
import csv
import glob
import os
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from dataclasses import dataclass, field

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdmodel import Model, Resolved   # noqa: E402

XSI = '{http://www.w3.org/2001/XMLSchema-instance}type'


def _local(tag: str) -> str:
    return tag.rsplit('}', 1)[-1]


def _child(e, name):
    for c in e:
        if _local(c.tag) == name:
            return c
    return None


def _text(e, name, default=''):
    c = _child(e, name)
    return (c.text or default).strip() if c is not None else default


def _path_tuple(pe) -> tuple[str, str, list[tuple[str, str]], str]:
    """(root_name, root_guid, [(node_name, node_guid)], conceptLibrary)"""
    if pe is None:
        return '', '', [], ''
    lib = pe.get('conceptLibrary', '')
    root_name = _text(pe, 'rootNode')
    root_guid = _text(pe, 'rootguid')
    nodes = []
    pn = _child(pe, 'pathNodes')
    if pn is not None:
        for n in pn:
            if _local(n.tag) == 'node':
                nodes.append((n.get('name', ''), n.get('guid', '')))
    return root_name, root_guid, nodes, lib


@dataclass
class Binding:
    form: str
    instr_id: str
    kind: str                # fillpoint | condition | list | subscription | annotation
    description: str
    concept_path: str        # human path as the template states it
    xml_path: str            # resolved Server XML path
    xml_type: str
    leaf_kind: str
    filters: str             # selector predicates in force
    depth: int
    resolved: bool
    reason: str


@dataclass
class FormResult:
    form: str
    title: str
    bindings: list[Binding] = field(default_factory=list)
    subscriptions: list[str] = field(default_factory=list)
    rtf_ids: list[str] = field(default_factory=list)
    domainmodels: list[str] = field(default_factory=list)


def _concept_path(root_name: str, nodes: list[tuple[str, str]]) -> str:
    return ' > '.join([root_name] + [n for n, _ in nodes]) if root_name else ''


class TemplateReader:
    def __init__(self, model: Model) -> None:
        self.model = model

    def read(self, path: str) -> FormResult:
        form = os.path.splitext(os.path.basename(path))[0]
        root = ET.parse(path).getroot()
        doc = _child(root, 'document')
        res = FormResult(form=form, title='')

        props = _child(doc, 'properties') if doc is not None else None
        if props is not None:
            for group in props:
                for p in group:
                    if p.get('name') == 'Title':
                        res.title = _text(p, 'value')

        content = _child(doc, 'content') if doc is not None else None
        if content is not None:
            rtf = _child(content, 'rtf')
            if rtf is not None and rtf.text:
                import re
                res.rtf_ids = re.findall(re.escape('%[') + r'([^]]*)]', rtf.text)

        markup_outer = _child(doc, 'markup') if doc is not None else None
        if markup_outer is None:
            return res
        dm = _child(markup_outer, 'domainmodels')
        if dm is not None:
            res.domainmodels = [
                f"{d.get('conceptlibrary', '')}:{d.get('domainmodel', '')}"
                for d in dm if _local(d.tag) == 'domainmodel'
            ]
        for markup in markup_outer:
            if _local(markup.tag) != 'markup':
                continue
            instrs = _child(markup, 'instructions')
            if instrs is not None:
                self._walk(instrs, res, scope={}, filters=[], depth=0)
        return res

    # ---------------------------------------------------------------- walker

    def _walk(self, instructions, res: FormResult,
              scope: dict[str, tuple[str, str]], filters: list[str], depth: int) -> None:
        for instr in instructions:
            if _local(instr.tag) != 'instruction':
                continue
            itype = instr.get(XSI, '')
            iid = instr.get('ID', '')
            desc = _text(instr, 'description')

            if itype == 'fillPointType':
                r, cp = self._resolve(_child(instr, 'path'), scope)
                res.bindings.append(self._row(res.form, iid, 'fillpoint', desc, cp, r,
                                              filters, depth))

            elif itype == 'listInstructionType':
                pe = _child(instr, 'pathToList')
                rn, rg, nodes, _ = _path_tuple(pe)
                r = self.model.resolve_list(rg, rn, nodes, scope)
                cp = _concept_path(rn, nodes)
                res.bindings.append(self._row(res.form, iid, 'list', desc, cp, r,
                                              filters, depth))
                inner_scope = dict(scope)
                itg = _text(instr, 'iteratorGuid')
                if itg and r.ok:
                    inner_scope[itg] = (r.xml_path, r.type_id or '')
                inner_filters = filters + [f'{r.xml_path}[{f}]' for f in r.filters]
                self._walk_parts(instr, res, inner_scope, inner_filters, depth + 1)

            elif itype == 'conditionalInstructionType':
                self._walk_parts(instr, res, scope, filters, depth + 1)

            elif itype == 'subscriptionType':
                sub = _child(instr, 'subscriptionDocumentName')
                name = sub.get('name', '') if sub is not None else ''
                res.subscriptions.append(name)
                res.bindings.append(Binding(res.form, iid, 'subscription', desc,
                                            name, '', '', 'document', '|'.join(filters),
                                            depth, True, ''))

            elif itype == 'annotationType':
                res.bindings.append(Binding(res.form, iid, 'annotation', desc,
                                            '', '', '', 'note', '|'.join(filters),
                                            depth, True, ''))

    def _walk_parts(self, instr, res: FormResult,
                    scope: dict[str, tuple[str, str]], filters: list[str], depth: int) -> None:
        parts = _child(instr, 'parts')
        if parts is None:
            return
        for part in parts:
            if _local(part.tag) != 'part':
                continue
            ptype = part.get(XSI, '')
            pid = part.get('ID', '')
            desc = _text(part, 'description')
            part_filters = filters

            if ptype == 'conditionalPartType':
                r, cp = self._resolve(_child(part, 'path'), scope)
                neg = _text(part, 'isNegative') == 'true'
                res.bindings.append(self._row(res.form, pid,
                                              'condition-not' if neg else 'condition',
                                              desc, cp, r, filters, depth))
                if r.ok and r.xml_path:
                    part_filters = filters + [('!' if neg else '') + r.xml_path]
            elif ptype == 'compositeConditionalPartType':
                paths = _child(part, 'paths')
                for pe in (list(paths) if paths is not None else []):
                    r, cp = self._resolve(pe, scope)
                    res.bindings.append(self._row(res.form, pid, 'condition-composite',
                                                  desc, cp, r, filters, depth))

            inner = _child(part, 'instructions')
            if inner is not None:
                self._walk(inner, res, scope, part_filters, depth)

    # --------------------------------------------------------------- helpers

    def _resolve(self, pe, scope) -> tuple[Resolved, str]:
        rn, rg, nodes, _ = _path_tuple(pe)
        return self.model.resolve_path(rg, rn, nodes, scope), _concept_path(rn, nodes)

    @staticmethod
    def _row(form, iid, kind, desc, concept_path, r: Resolved,
             filters: list[str], depth: int) -> Binding:
        if r.leaf_kind == 'builtin-test':
            # A list-position test (is First / is Last) consumes no data; the
            # owner's path is NOT what this instruction binds to.
            return Binding(form, iid, kind, desc, concept_path, '', '',
                           'builtin-test', '|'.join(filters), depth, True,
                           'list-position builtin, no data required')
        return Binding(
            form=form, instr_id=iid, kind=kind, description=desc,
            concept_path=concept_path, xml_path=r.xml_path,
            xml_type=r.type_id or '', leaf_kind=r.leaf_kind,
            filters='|'.join(filters + [f'[{f}]' for f in r.filters]),
            depth=depth, resolved=r.ok, reason=r.reason,
        )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('package', help='extracted .gdsp directory (contains model.xml)')
    ap.add_argument('--out', default='output/gdbindings.csv')
    ap.add_argument('--form', default=None, help='one template name; prints it instead of CSV')
    args = ap.parse_args()

    libs = sorted(glob.glob(os.path.join(args.package, 'Concept Libraries', '*.gdm')))
    model = Model.load(os.path.join(args.package, 'model.xml'), concept_libs=libs)
    reader = TemplateReader(model)
    tdir = os.path.join(args.package, 'Templates')

    if args.form:
        p = os.path.join(tdir, args.form + '.gd')
        res = reader.read(p)
        print(f'{res.form}   title={res.title!r}')
        print(f'  domain models: {", ".join(res.domainmodels)}')
        print(f'  %[id] markers in rtf: {len(res.rtf_ids)}')
        print()
        for b in res.bindings:
            pad = '  ' * b.depth
            mark = '' if b.resolved else '  !! ' + b.reason
            print(f'{pad}[{b.instr_id:>5}] {b.kind:<20} {b.concept_path}')
            if b.xml_path:
                print(f'{pad}        -> {b.xml_path}   ({b.xml_type}){mark}')
            elif mark:
                print(f'{pad}       {mark}')
        return 0

    files = sorted(glob.glob(os.path.join(tdir, '*.gd')))
    rows: list[Binding] = []
    per_form = []
    for i, f in enumerate(files, 1):
        try:
            res = reader.read(f)
        except Exception as e:                       # a malformed template is data, not a crash
            print(f'  !! {os.path.basename(f)}: {type(e).__name__}: {e}')
            continue
        rows.extend(res.bindings)
        per_form.append((res.form, len(res.bindings), len(res.rtf_ids)))
        if i % 100 == 0:
            print(f'  {i}/{len(files)} templates, {len(rows)} bindings')

    os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
    with open(args.out, 'w', newline='', encoding='utf-8') as fh:
        w = csv.writer(fh)
        w.writerow(['form', 'instr_id', 'kind', 'description', 'concept_path',
                    'xml_path', 'xml_type', 'leaf_kind', 'filters', 'depth',
                    'resolved', 'reason'])
        for b in rows:
            w.writerow([b.form, b.instr_id, b.kind, b.description, b.concept_path,
                        b.xml_path, b.xml_type, b.leaf_kind, b.filters, b.depth,
                        int(b.resolved), b.reason])

    kinds = Counter(b.kind for b in rows)
    unres = [b for b in rows if not b.resolved]
    print()
    print(f'templates read        {len(per_form)}/{len(files)}')
    print(f'bindings extracted    {len(rows)}')
    for k, v in kinds.most_common():
        n_bad = sum(1 for b in unres if b.kind == k)
        print(f'  {k:<22} {v:>7}   unresolved {n_bad}')
    print(f'distinct xml paths    {len({b.xml_path for b in rows if b.xml_path})}')
    print(f'distinct root elements {sorted({b.xml_path.split("/")[0] for b in rows if b.xml_path})}')
    if unres:
        print(f'\nunresolved ({len(unres)}) -- top reasons:')
        for r, n in Counter(b.reason for b in unres).most_common(12):
            print(f'  {n:>6}  {r}')
    print(f'\nwrote {args.out}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
