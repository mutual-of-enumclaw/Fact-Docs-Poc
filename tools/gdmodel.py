"""Read a GhostDraft server model (`model.xml` from a `.gdsp` package) as the
AUTHORITY for the Server XML binding namespace, and resolve the GUID paths that
production `.gd` templates carry into concrete Server XML element paths.

Why this exists
---------------
A production GhostDraft template does not store a data path as a string. It
stores a typed instruction tree whose paths are `(rootNode+rootguid,
pathNodes[name+guid])` tuples against a *concept library*. `model.xml` is the
compiled projection of those concept libraries onto Server XML: every concept
attribute carries the `id` that becomes the XML element name, and every list
carries the `elementName` that becomes the repeated child under `Items`.

So `model.xml` -- not the concept library `.gdm`, and not any name-matching
heuristic -- is what turns a template's GUID path into the Server XML path that
`fact-docgen`'s ISectionBuilders must produce.

Node kinds (measured, not assumed -- see containment below):

    model  -> root(1), class(2381), list(296), renderable(11)
    root   -> attribute(21)                       the 21 top-level XML elements
    class  -> attribute, selector, test, string, mutexTestGroup
    list   -> attribute, test                     list-level, not per-item

A `list` declares `elementType`/`elementName`/`elementId`; its items live at
`<path>/Items/<elementName>` in the Server XML (confirmed against the package
XSD). A `selector` is a boolean element on the ITEM class, so a selector in a
path is a FILTER, not a step: `Auto[AutoswithUIMCoverage-Washington]`.

Usage:
    from gdmodel import Model
    m = Model.load(r'...\\model.xml')
    m.resolve_path(root_guid, root_name, [(node_name, node_guid), ...], scope)
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

NS = '{http://ghostdraft.korbitec.com/server/model-1}'


def _local(tag: str) -> str:
    return tag.rsplit('}', 1)[-1]


@dataclass
class Member:
    """One declared member of a class/list/root: attribute, selector or test."""
    kind: str          # attribute | selector | test | string | mutexTestGroup
    name: str
    id: str
    guid: str
    type_id: str | None = None
    owner_id: str = ''


@dataclass
class TypeNode:
    """A `m:root`, `m:class` or `m:list` -- a complex type in the Server XML."""
    kind: str          # root | class | list
    name: str
    id: str
    element_type: str | None = None   # list only: item type id
    element_name: str | None = None   # list only: DISPLAY name of the item
    element_id: str | None = None     # list only: XML name of the repeated child
    members_by_guid: dict[str, Member] = field(default_factory=dict)
    members_by_name: dict[str, Member] = field(default_factory=dict)

    @property
    def is_list(self) -> bool:
        return self.kind == 'list'


@dataclass
class Step:
    """One resolved hop of a path."""
    kind: str          # attribute | selector | test | list-items | string | unresolved
    name: str
    xml_id: str | None
    type_id: str | None


@dataclass
class Resolved:
    """The outcome of resolving one template path."""
    ok: bool
    xml_path: str            # e.g. CAAutoLevelCoverages/Items/Auto/VehicleDescription
    type_id: str | None      # type of the leaf
    leaf_kind: str           # attribute | selector | test | list | unresolved | ...
    filters: list[str] = field(default_factory=list)   # selector predicates applied
    steps: list[Step] = field(default_factory=list)
    reason: str = ''         # why not ok


# A path node may name something the model does not declare. Three shapes exist
# across the 491 Commercial Auto templates, and all three are GhostDraft
# built-in PREDICATES over the path resolved so far -- they consume no XML
# element of their own, so they must not extend the path:
#
#   is provided                 5742 uses, ONE stable guid, declared nowhere
#   has N or more elements       280 uses, list-count, declared nowhere
#   is First / is Last / ...           list position
#
# `contains the X` looks like a fourth but is not: it is a `<test>` declared in
# the CONCEPT LIBRARY carrying a `selector="<guid>"` attribute, i.e. "any item
# matching selector X". It resolves to that selector's boolean element, which is
# why Model.load takes the `.gdm` files -- see ConceptLibraryIndex.
BUILTIN_TESTS = {
    'is first', 'is last', 'is not first', 'is not last', 'is only',
    'is not only', 'is odd', 'is even',
    'is second last', 'precedes second last',
    'is provided', 'is not provided',
}

_BUILTIN_PATTERNS = (
    re.compile(r'^has \d+ or more elements$', re.I),
    re.compile(r'^has \d+ or fewer elements$', re.I),
    re.compile(r'^has exactly \d+ elements?$', re.I),
    re.compile(r'^has no elements$', re.I),
)


def is_builtin_test(name: str) -> bool:
    n = name.strip()
    return n.lower() in BUILTIN_TESTS or any(p.match(n) for p in _BUILTIN_PATTERNS)


class ConceptLibraryIndex:
    """The `.gdm` concept libraries, read only for what `model.xml` does not
    project: derived tests (`contains the X`) that stand for a selector."""

    def __init__(self) -> None:
        self.test_selector: dict[str, tuple[str, str]] = {}   # test guid -> (name, selector guid)
        self.test_group: dict[str, tuple[str, str]] = {}      # test guid -> (name, group guid)

    @classmethod
    def load(cls, paths: list[str]) -> 'ConceptLibraryIndex':
        self = cls()
        for path in paths:
            root = ET.parse(path).getroot()
            for e in root.iter():
                if _local(e.tag) != 'test':
                    continue
                g = e.get('guid')
                if not g:
                    continue
                if e.get('selector'):
                    self.test_selector[g] = (e.get('name', ''), e.get('selector', ''))
                elif e.get('group'):
                    self.test_group[g] = (e.get('name', ''), e.get('group', ''))
        return self


class Model:
    def __init__(self) -> None:
        self.name = ''
        self.id = ''
        self.version = ''
        self.types: dict[str, TypeNode] = {}
        self.root: TypeNode | None = None
        # guid -> (owner type id, member) for every declared member anywhere
        self.member_by_guid: dict[str, tuple[str, Member]] = {}
        self.renderables: dict[str, str] = {}   # renderable id -> behaviour name
        self.concepts = ConceptLibraryIndex()

    # ------------------------------------------------------------------ load

    @classmethod
    def load(cls, path: str, concept_libs: list[str] | None = None) -> 'Model':
        self = cls()
        if concept_libs:
            self.concepts = ConceptLibraryIndex.load(concept_libs)
        root = ET.parse(path).getroot()
        self.name = root.get('name', '')
        self.id = root.get('id', '')
        self.version = root.get('version', '')

        for node in root:
            kind = _local(node.tag)
            if kind == 'renderable':
                behaviours = [c.get('name', '') for c in node if _local(c.tag) == 'behaviour']
                self.renderables[node.get('id', '')] = behaviours[0] if behaviours else ''
                continue
            if kind not in ('root', 'class', 'list'):
                continue

            tid = node.get('id') or '__root'
            t = TypeNode(
                kind=kind,
                name=node.get('name', ''),
                id=tid,
                element_type=node.get('elementType'),
                element_name=node.get('elementName'),
                element_id=node.get('elementId'),
            )
            for c in node:
                ck = _local(c.tag)
                if ck not in ('attribute', 'selector', 'test', 'string', 'mutexTestGroup'):
                    continue
                m = Member(
                    kind=ck,
                    name=c.get('name', ''),
                    id=c.get('id', ''),
                    guid=c.get('guid', ''),
                    type_id=c.get('type'),
                    owner_id=tid,
                )
                if m.guid:
                    t.members_by_guid[m.guid] = m
                    self.member_by_guid[m.guid] = (tid, m)
                if m.name:
                    t.members_by_name.setdefault(m.name, m)
            self.types[tid] = t
            if kind == 'root':
                self.root = t

        return self

    # -------------------------------------------------------------- helpers

    def type_of(self, type_id: str | None) -> TypeNode | None:
        return self.types.get(type_id) if type_id else None

    def is_leaf_type(self, type_id: str | None) -> bool:
        """A renderable (Text1, Currency2, ...) rather than a complex type."""
        return bool(type_id) and type_id not in self.types

    def list_items_path(self, base: str, list_type: TypeNode) -> str:
        """Server XML puts list items under `<base>/Items/<elementId>`.

        `elementId`, NOT `elementName`: they differ on 117 of the 296 lists
        (`elementName="Additional Insured"` vs `elementId="AdditionalInsured"`),
        and the XSD uses the id. Reading the name produced 698 paths the schema
        rejects.
        """
        el = list_type.element_id or list_type.element_id_fallback()  # type: ignore[attr-defined]
        return f'{base}/Items/{_xml_name(el)}' if base else f'Items/{_xml_name(el)}'

    def _lookup(self, type_id: str | None, guid: str, name: str
                ) -> tuple[Member | None, bool]:
        """Find a member by guid (then name) on `type_id`.

        Returns (member, via_item_class): for a list type the member may live on
        the ITEM class, which means the path descends through `Items/<element>`.
        """
        t = self.type_of(type_id)
        if t is None:
            return None, False
        m = t.members_by_guid.get(guid) or (t.members_by_name.get(name) if name else None)
        if m is not None:
            return m, False
        if t.is_list:
            item = self.type_of(t.element_type)
            if item is not None:
                m = item.members_by_guid.get(guid) or (item.members_by_name.get(name) if name else None)
                if m is not None:
                    return m, True
        return None, False

    # ------------------------------------------------------------- resolving

    def resolve_root(self, root_guid: str, root_name: str,
                     scope: dict[str, tuple[str, str]] | None = None
                     ) -> tuple[str, str | None, str]:
        """Resolve a path's root to (xml_path, type_id, how).

        `scope` maps an in-template iterator guid to (xml_path, type_id); it is
        how a fill point rooted at `Auto` finds the enclosing list instruction.
        """
        scope = scope or {}
        if root_guid in scope:
            p, t = scope[root_guid]
            return p, t, 'iterator'
        assert self.root is not None
        m = self.root.members_by_guid.get(root_guid) or self.root.members_by_name.get(root_name)
        if m is not None:
            return _xml_name(m.id), m.type_id, 'model-root'
        # Some templates root a path at a type by name (rare).
        for t in self.types.values():
            if t.name == root_name:
                return '', t.id, 'type-by-name'
        return '', None, 'unresolved-root'

    def resolve_path(self, root_guid: str, root_name: str,
                     path_nodes: list[tuple[str, str]],
                     scope: dict[str, tuple[str, str]] | None = None) -> Resolved:
        base, type_id, how = self.resolve_root(root_guid, root_name, scope)
        if how == 'unresolved-root':
            return Resolved(False, '', None, 'unresolved',
                            reason=f'root {root_name!r} ({root_guid}) not in model')

        steps: list[Step] = []
        filters: list[str] = []
        path = base
        leaf_kind = 'list' if (self.type_of(type_id) and self.type_of(type_id).is_list) else 'class'

        for name, guid in path_nodes:
            m, via_item = self._lookup(type_id, guid, name)

            if m is None and guid in self.concepts.test_selector:
                # `contains the X`: a concept-library test standing for a
                # selector. model.xml does not project it, so follow the
                # indirection and resolve the SELECTOR it names.
                _tname, sel_guid = self.concepts.test_selector[guid]
                m, via_item = self._lookup(type_id, sel_guid, '')
                if m is None:
                    steps.append(Step('derived-test', name, None, 'xs:boolean'))
                    leaf_kind = 'derived-test'
                    continue

            if m is None:
                if is_builtin_test(name):
                    steps.append(Step('builtin-test', name, None, None))
                    leaf_kind = 'builtin-test'
                    continue
                g = self.member_by_guid.get(guid)
                if g is not None:
                    # Declared elsewhere in the model: the template's path skips
                    # a hop we did not follow. Report rather than guess.
                    owner, mm = g
                    return Resolved(False, path, type_id, 'unresolved', filters, steps,
                                    reason=f'{name!r} is a {mm.kind} on {owner}, not on {type_id}')
                return Resolved(False, path, type_id, 'unresolved', filters, steps,
                                reason=f'{name!r} ({guid}) not declared on {type_id}')

            if via_item:
                lt = self.type_of(type_id)
                assert lt is not None
                path = self.list_items_path(path, lt)
                type_id = lt.element_type

            if m.kind == 'selector':
                filters.append(_xml_name(m.id))
                steps.append(Step('selector', m.name, _xml_name(m.id), 'xs:boolean'))
                leaf_kind = 'selector'
                continue   # a selector filters; it does not advance the path

            if m.kind == 'test' and guid in self.concepts.test_group:
                # 75 of 1905 tests are members of a mutexTestGroup. The GROUP is
                # the XML element -- an enumerated string -- and the test is one
                # of its VALUES, so `Policy > is Renewal` is
                # `Policy/TransactionType == "isRenewal"`, not an element named
                # `isRenewal`. Verified against the XSD simpleType.
                _tn, group_guid = self.concepts.test_group[guid]
                gt = self.type_of(type_id)
                gm = gt.members_by_guid.get(group_guid) if gt else None
                if gm is not None:
                    gpath = f'{path}/{_xml_name(gm.id)}' if path else _xml_name(gm.id)
                    steps.append(Step('mutex-value', m.name, _xml_name(m.id), 'enum'))
                    filters.append(f'{_xml_name(gm.id)}=="{_xml_name(m.id)}"')
                    path = gpath
                    leaf_kind = 'mutex-value'
                    continue

            if m.kind in ('test', 'mutexTestGroup'):
                steps.append(Step(m.kind, m.name, _xml_name(m.id), 'xs:boolean'))
                path = f'{path}/{_xml_name(m.id)}' if path else _xml_name(m.id)
                leaf_kind = m.kind
                continue

            # attribute or string: a real step down the XML tree
            path = f'{path}/{_xml_name(m.id)}' if path else _xml_name(m.id)
            steps.append(Step(m.kind, m.name, _xml_name(m.id), m.type_id))
            type_id = m.type_id
            t = self.type_of(type_id)
            leaf_kind = ('list' if (t and t.is_list) else
                         'class' if t else 'attribute')

        return Resolved(True, path, type_id, leaf_kind, filters, steps)

    def resolve_list(self, root_guid: str, root_name: str,
                     path_nodes: list[tuple[str, str]],
                     scope: dict[str, tuple[str, str]] | None = None) -> Resolved:
        """Resolve a `pathToList`, then descend into `Items/<elementName>` so the
        result is the path an ITERATOR is bound to."""
        r = self.resolve_path(root_guid, root_name, path_nodes, scope)
        if not r.ok:
            return r
        t = self.type_of(r.type_id)
        if t is None or not t.is_list:
            r.reason = f'pathToList leaf {r.type_id} is not a m:list'
            return r
        return Resolved(True, self.list_items_path(r.xml_path, t), t.element_type,
                        'list-item', r.filters, r.steps)


def _xml_name(s: str | None) -> str:
    return s or ''


# `element_id` is present on m:list but `element_name` is what the XSD uses;
# keep a fallback so a malformed list still resolves to something inspectable.
def _element_id_fallback(self: TypeNode) -> str:
    return self.element_id or self.element_name or self.name or 'Item'


TypeNode.element_id_fallback = _element_id_fallback   # type: ignore[attr-defined]


if __name__ == '__main__':
    import sys
    m = Model.load(sys.argv[1])
    print(f'model {m.name!r} v{m.version} id={m.id}')
    print(f'  types: {len(m.types)}  members: {len(m.member_by_guid)}')
    assert m.root is not None
    print(f'  root attributes ({len(m.root.members_by_guid)}):')
    for mem in m.root.members_by_guid.values():
        print(f'    {mem.id:<48} {mem.type_id}')
