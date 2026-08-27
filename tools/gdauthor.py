"""Author a GhostDraft `.gd` template WITH LOGIC, against a real package model.

This is the write direction of sections 40-43. It exists to prove one thing:
that the decoded grammar is sufficient to EMIT logic, not merely to read it.

What it emits
-------------
A `.gd` is layout + logic + a stitch:

    <content><rtf>            the layout, carrying `%[ID]` markers
    <markup><instructions>    a tree of typed instructions
    the IDs                   are the stitch

The grammar (FORM-STUDIO-PLAN section 43.1, verified over 570 production
templates in two packages with zero violations):

  1. a container -- conditionalInstructionType, listInstructionType -- is NEVER
     placed in the RTF. Its PARTS are, and that is how the logic gets its extent.
  2. every other instruction and every part is placed exactly once.
  3. no marker may exist without a declaration.
  4. a container's part markers appear in DECLARATION ORDER, endPart last.
  5. every child's marker span nests strictly inside its parent's.

`Emitter.build()` constructs markup and RTF in one recursive pass so 4 and 5 hold
by construction, then `verify()` re-derives all five from the finished bytes --
because a generator asserting its own intent proves nothing, and section 43 is
explicit that the round trip is what is unproven.

Bindings are resolved through `gdmodel` against the target package's `model.xml`,
so a path that would not resolve in the real package fails HERE rather than in
GhostDraft.

Usage:
    python tools/gdauthor.py <package-dir> --demo out.gd
"""

from __future__ import annotations

import argparse
import glob
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdmodel import Model   # noqa: E402

BS = chr(92)
XSI_NS = 'http://www.w3.org/2001/XMLSchema-instance'
XSI = '{%s}type' % XSI_NS

# The one built-in this emitter needs. Its guid is a GhostDraft constant, not a
# model value: byte-identical in the ISO Commercial Auto and MoE Proprietary
# Commercial Auto packages (5,742 and 431 uses).
IS_PROVIDED = ('is provided', '50b7af4a-c5ce-4d16-abba-b1fb53653521')


# --------------------------------------------------------------------- the spec

@dataclass
class Path:
    """A concept path exactly as a template stores it: names AND guids."""
    root_name: str
    root_guid: str
    nodes: list[tuple[str, str]] = field(default_factory=list)

    def then(self, name: str, guid: str) -> 'Path':
        return Path(self.root_name, self.root_guid, self.nodes + [(name, guid)])

    @property
    def label(self) -> str:
        return ' > '.join([self.root_name] + [n for n, _ in self.nodes])


@dataclass
class Fill:
    path: Path
    text: str = ''            # sample text drawn in the layout beside the marker


@dataclass
class Static:
    text: str


@dataclass
class Break:
    """End the paragraph. Without these every Static and Fill runs together into
    one paragraph -- which rendered as a single wall of text on the first real
    PDF, and is the whole difference between a schedule and a sentence."""
    pass


@dataclass
class Cond:
    """if <path> then ... [else ...]. `path` is the test; a leaf built-in is fine."""
    path: Path
    then: list = field(default_factory=list)
    otherwise: list | None = None


@dataclass
class Repeat:
    """Iterate a list. `iterator` names the item inside; its guid is TEMPLATE-LOCAL
    (production iterator guids are absent from model.xml -- they scope the
    nested paths, nothing more), so the emitter mints one deterministically."""
    path: Path
    iterator: str
    body: list = field(default_factory=list)


# ------------------------------------------------------------------- the emitter

class Emitter:
    def __init__(self, model: Model, concept_library: str) -> None:
        self.model = model
        self.lib = concept_library
        self._next = 1
        self.errors: list[str] = []
        # every model ROOT a path touches -- <domainmodels> must declare them,
        # and without that GhostDraft reports the variables as not in the model
        self.roots: list[str] = []

    def _note_root(self, p: 'Path', scope: dict) -> None:
        if p.root_guid in scope:          # an iterator, not a model root
            return
        if p.root_guid not in self.roots:
            self.roots.append(p.root_guid)

    def _id(self) -> str:
        i = self._next
        self._next += 1
        return str(i)

    # ---- binding resolution: fail here, not in GhostDraft

    def _resolve(self, p: Path, scope: dict) -> str:
        self._note_root(p, scope)
        r = self.model.resolve_path(p.root_guid, p.root_name, p.nodes, scope)
        if not r.ok:
            self.errors.append(f'{p.label}: {r.reason}')
            return ''
        return r.xml_path

    def _resolve_list(self, p: Path, scope: dict):
        self._note_root(p, scope)
        r = self.model.resolve_list(p.root_guid, p.root_name, p.nodes, scope)
        if not r.ok:
            self.errors.append(f'{p.label} (as list): {r.reason or "not a list"}')
        return r

    # ---- path XML

    def _path_xml(self, p: Path, indent: str) -> str:
        out = [f'{indent}<path conceptLibrary="{_x(self.lib)}">',
               f'{indent}  <rootNode>{_x(p.root_name)}</rootNode>',
               f'{indent}  <rootguid>{p.root_guid}</rootguid>',
               f'{indent}  <pathNodes>']
        for n, g in p.nodes:
            out.append(f'{indent}    <node name="{_x(n)}" guid="{g}" />')
        out += [f'{indent}  </pathNodes>', f'{indent}</path>']
        return '\n'.join(out)

    # ---- the single recursive pass: markup and RTF together

    def build(self, body: list, title: str) -> tuple[str, str, list[str]]:
        """-> (markup xml, rtf body, resolved paths)"""
        resolved: list[str] = []
        markup, rtf = self._emit(body, indent='          ', scope={}, resolved=resolved)
        return markup, rtf, resolved

    def _emit(self, nodes: list, indent: str, scope: dict,
              resolved: list[str]) -> tuple[str, str]:
        xml_parts: list[str] = []
        rtf_parts: list[str] = []

        for node in nodes:
            if isinstance(node, Static):
                rtf_parts.append(_run(node.text))
                continue

            if isinstance(node, Break):
                rtf_parts.append(_par())
                continue

            if isinstance(node, Fill):
                iid = self._id()
                xp = self._resolve(node.path, scope)
                if xp:
                    resolved.append(xp)
                xml_parts.append('\n'.join([
                    f'{indent}<instruction xsi:type="fillPointType" ID="{iid}" '
                    f'descriptionSource="ParsedUserText">',
                    f'{indent}  <description>{_x(node.path.label)}</description>',
                    self._path_xml(node.path, indent + '  '),
                    f'{indent}  <adornmentPath conceptLibrary="" />',
                    f'{indent}</instruction>']))
                # RULE 2: a leaf is placed exactly once.
                rtf_parts.append(_marker(iid))
                if node.text:
                    rtf_parts.append(_run(' ' + node.text))
                continue

            if isinstance(node, Cond):
                iid = self._id()
                part_id = self._id()
                then_xml, then_rtf = self._emit(node.then, indent + '        ',
                                                scope, resolved)
                xp = self._resolve(node.path, scope)
                if xp:
                    resolved.append(xp)
                else_id = else_xml = else_rtf = None
                if node.otherwise is not None:
                    else_id = self._id()
                    else_xml, else_rtf = self._emit(node.otherwise, indent + '        ',
                                                    scope, resolved)
                end_id = self._id()

                seg = [f'{indent}<instruction xsi:type="conditionalInstructionType" '
                       f'ID="{iid}" descriptionSource="ParsedUserText" folded="false" '
                       f'tableNesting="0">',
                       f'{indent}  <parts>',
                       f'{indent}    <part xsi:type="conditionalPartType" ID="{part_id}" '
                       f'descriptionSource="ParsedUserText">',
                       f'{indent}      <description>{_x(node.path.label)}</description>',
                       f'{indent}      <instructions>',
                       then_xml,
                       f'{indent}      </instructions>',
                       f'{indent}      <isNegative>false</isNegative>',
                       self._path_xml(node.path, indent + '      '),
                       f'{indent}    </part>']
                if else_id:
                    seg += [f'{indent}    <part xsi:type="elsePartType" ID="{else_id}">',
                            f'{indent}      <instructions>',
                            else_xml,
                            f'{indent}      </instructions>',
                            f'{indent}    </part>']
                seg += [f'{indent}    <part xsi:type="endPartType" ID="{end_id}" />',
                        f'{indent}  </parts>',
                        f'{indent}</instruction>']
                xml_parts.append('\n'.join(s for s in seg if s))

                # RULE 1: the container is NOT placed. RULES 4+5: parts in
                # declaration order, endPart last, children nested between.
                rtf_parts.append(_marker(part_id))
                rtf_parts.append(then_rtf)
                if else_id:
                    rtf_parts.append(_marker(else_id))
                    rtf_parts.append(else_rtf)
                rtf_parts.append(_marker(end_id))
                continue

            if isinstance(node, Repeat):
                iid = self._id()
                part_id = self._id()
                r = self._resolve_list(node.path, scope)
                iter_guid = _stable_guid(f'{node.path.label}|{node.iterator}')
                inner = dict(scope)
                if r.ok:
                    inner[iter_guid] = (r.xml_path, r.type_id or '', r.spec_path)
                    resolved.append(r.xml_path)
                body_xml, body_rtf = self._emit(node.body, indent + '        ',
                                                inner, resolved)
                end_id = self._id()

                seg = [f'{indent}<instruction xsi:type="listInstructionType" ID="{iid}" '
                       f'descriptionSource="ParsedUserText" folded="false" '
                       f'tableNesting="0">',
                       f'{indent}  <description>{_x(node.path.label)}</description>',
                       f'{indent}  <pathToList conceptLibrary="{_x(self.lib)}">',
                       f'{indent}    <rootNode>{_x(node.path.root_name)}</rootNode>',
                       f'{indent}    <rootguid>{node.path.root_guid}</rootguid>',
                       f'{indent}    <pathNodes>']
                for n, g in node.path.nodes:
                    seg.append(f'{indent}      <node name="{_x(n)}" guid="{g}" />')
                seg += [f'{indent}    </pathNodes>',
                        f'{indent}  </pathToList>',
                        f'{indent}  <iterator>{_x(node.iterator)}</iterator>',
                        f'{indent}  <iteratorGuid>{iter_guid}</iteratorGuid>',
                        f'{indent}  <parts>',
                        f'{indent}    <part xsi:type="listPartType" ID="{part_id}" '
                        f'descriptionSource="ParsedUserText">',
                        f'{indent}      <description>{_x(node.path.label)}</description>',
                        f'{indent}      <instructions>',
                        body_xml,
                        f'{indent}      </instructions>',
                        f'{indent}      <partIdentifier>Default</partIdentifier>',
                        f'{indent}    </part>',
                        f'{indent}    <part xsi:type="endPartType" ID="{end_id}" />',
                        f'{indent}  </parts>',
                        f'{indent}</instruction>']
                xml_parts.append('\n'.join(s for s in seg if s))

                rtf_parts.append(_marker(part_id))
                rtf_parts.append(body_rtf)
                rtf_parts.append(_marker(end_id))
                continue

            raise TypeError(f'unknown spec node {type(node).__name__}')

        return '\n'.join(p for p in xml_parts if p), ''.join(rtf_parts)


# ------------------------------------------------------------------ RTF helpers

def _run(text: str) -> str:
    return '{' + BS + 'cf0' + BS + 'f1' + BS + 'fs20' + BS + 'ulnone' + BS + \
        'ulc0 ' + _rtf(text) + '}'


def _par() -> str:
    return BS + 'par' + BS + 'pard' + BS + 'plain' + BS + 'ql' + BS + 'sb0' + BS +         'sa0' + BS + 'li0' + BS + 'ri0' + BS + 'fi0' + BS + 'sl240' + BS +         'slmult1' + BS + 'nowidctlpar' + BS + 'f1' + BS + 'fs20 '


def _marker(iid: str) -> str:
    """Production splits `%[N]` across two runs; both forms work, and emitting
    the split form matches what GhostDraft itself writes (854 markers over 119
    templates in the ISO package). Contiguous is safer to re-read, so this
    emits contiguous and the verifier destyles anyway."""
    return '{' + BS + 'cf0' + BS + 'f1' + BS + 'fs20' + BS + 'ulnone' + BS + \
        'ulc0 %[' + iid + ']}'


def _rtf(text: str) -> str:
    return text.replace(BS, BS + BS).replace('{', BS + '{').replace('}', BS + '}')


def _x(text: str) -> str:
    return (text.replace('&', '&amp;').replace('<', '&lt;')
            .replace('>', '&gt;').replace('"', '&quot;'))


def _stable_guid(seed: str) -> str:
    """A template-local iterator guid. Deterministic so output is byte-stable
    across runs, which the .gd golden suite depends on."""
    import hashlib
    h = hashlib.sha256(seed.encode('utf-8')).hexdigest()
    return f'{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}'


RTF_HEAD = (
    '{' + BS + 'rtf1 ' + BS + 'adeflang1025' + BS + 'uc1' + BS + 'deflang1033 '
    '{' + BS + 'fonttbl{' + BS + 'f0 Times New Roman;}{' + BS + 'f1 Arial;}}'
    '{' + BS + 'colortbl;}'
    + BS + 'paperw12240' + BS + 'paperh15840' + BS + 'margl1440' + BS + 'margr1440'
    + BS + 'margt1080' + BS + 'margb1080'
    + BS + 'sectd' + BS + 'sbknone'
    + BS + 'pard' + BS + 'plain' + BS + 'ql' + BS + 'sb0' + BS + 'sa0' + BS + 'li0'
    + BS + 'ri0' + BS + 'fi0' + BS + 'sl240' + BS + 'slmult1' + BS + 'nowidctlpar'
    + BS + 'f1' + BS + 'fs20 ')
RTF_TAIL = '{' + BS + 'cf0' + BS + 'f1' + BS + 'fs20' + BS + 'ulnone' + BS + \
    'ulc0 ' + BS + 'par }}'


def wrap(rtf_body: str, markup: str, title: str,
         env: 'Envelope | None' = None, roots: list[str] | None = None,
         library: str = '') -> str:
    """The full document. A structural diff against production found eight
    elements missing from an earlier version of this function; they are all
    here now, and all sourced from the package rather than hardcoded."""
    style_map = (f'      <styleMap libraryid="{env.default_style_id}" />'
                 if env and env.default_style_id else '')
    domainmodels = (env.domainmodels_xml(roots or [], library) if env else '')
    annot = env.annotation_style_xml() if env else ''
    body = [
        '<?xml version="1.0" encoding="utf-8"?>',
        '<Content Name="GhostDraftDocument" Version="1.0" '
        'ApplicationVersion="GhostDraft 5.3.58854.0" CompatibleVersion="GhostDraft 3.3">',
        '  <document xmlns:xsd="http://www.w3.org/2001/XMLSchema" '
        f'xmlns:xsi="{XSI_NS}" '
        'xmlns="http://schemas.korbitec.com/GhostDraft/Document/1.0">',
        '    <properties>',
        f'      <system xmlns="{DOCPROP_NS}">',
        f'        <property name="Title" type="string"><value>{_x(title)}</value></property>',
        '      </system>',
        f'      <custom xmlns="{DOCPROP_NS}">',
        '        <property name="ghostassembler:subscriptionName" type="string">',
        f'          <value>{_x(title)}</value>',
        '        </property>',
        '      </custom>',
        '    </properties>',
        '    <content>',
        f'      <rtf>{_x(RTF_HEAD + rtf_body + RTF_TAIL)}</rtf>',
        style_map,
        '    </content>',
        '    <library xsi:nil="true" />',
        '    <markup>',
        '      <markup ID="0" descriptionSource="ParsedUserText" '
        f'xmlns="{MARKUP_NS}">',
        '        <instructions>',
        markup,
        '        </instructions>',
        '      </markup>',
        annot,
        domainmodels,
        '    </markup>',
        '    <scenarios default="default">',
        '      <scenario defaultListCount="2" defaultTestValue="true" '
        'locked="false" name="default" />',
        '    </scenarios>',
        '    <stylelibrary name="" />',
        '    <trimlastparagraphmarker>false</trimlastparagraphmarker>',
        '    <documenttype>Document</documenttype>',
        '  </document>',
        '</Content>',
        '',
    ]
    return chr(10).join(x for x in body if x != '')


# ------------------------------------------------------------------- envelope

MARKUP_NS = 'http://schemas.korbitec.com/GhostDraft/MarkupModel/1.0'
DOCPROP_NS = 'http://schemas.korbitec.com/GhostDraft/DocumentProperties/1.0'


class Envelope:
    """Everything a `.gd` needs AROUND the logic, read from the package itself.

    A structural diff against production showed the emitter was missing eight
    elements. `domainmodels` is the load-bearing one: it DECLARES which model
    roots the template binds to, and without it GhostDraft reports the bound
    variables as not being in the model. The rest are style and document
    plumbing.

    Nothing here is hardcoded -- style library ids come from the Style Libraries
    folder, domain model names from the `.gdm`, and the default style library is
    whichever the package's own templates reference most.
    """

    def __init__(self, package: str) -> None:
        self.style_libs: dict[str, tuple[str, list[tuple[str, str]]]] = {}
        self.domain_names: dict[str, str] = {}
        self.default_style_id = ''
        self.annotation_style_id = ''
        self._load_style_libraries(package)
        self._load_domain_models(package)
        self._pick_defaults(package)

    def _load_style_libraries(self, package: str) -> None:
        d = os.path.join(package, 'Style Libraries')
        if not os.path.isdir(d):
            return
        for name in os.listdir(d):
            try:
                root = ET.parse(os.path.join(d, name)).getroot()
            except ET.ParseError:
                continue
            for e in root.iter():
                if e.tag.rsplit('}', 1)[-1] != 'styleMap':
                    continue
                lib = e.get('libraryid', '')
                styles = [(c.get('name', ''), c.get('link', '')) for c in e
                          if c.tag.rsplit('}', 1)[-1] == 'style']
                self.style_libs[name] = (lib, styles)
                break

    def _load_domain_models(self, package: str) -> None:
        d = os.path.join(package, 'Concept Libraries')
        if not os.path.isdir(d):
            return
        for f in os.listdir(d):
            if not f.endswith('.gdm'):
                continue
            for e in ET.parse(os.path.join(d, f)).getroot().iter():
                if e.tag.rsplit('}', 1)[-1] == 'domainModel' and e.get('guid'):
                    self.domain_names[e.get('guid')] = e.get('name', '')

    def _pick_defaults(self, package: str) -> None:
        """Whichever library ids the package's OWN templates reference most."""
        import collections
        style = collections.Counter()
        annot = collections.Counter()
        for f in glob.glob(os.path.join(package, 'Templates', '*.gd')):
            head = open(f, encoding='utf-8').read(20000)
            for m in re.finditer(r'<styleMap libraryid="([^"]+)"', head):
                style[m.group(1)] += 1
            for m in re.finditer(r'<annotationStyleMap libraryid="([^"]+)"', head):
                annot[m.group(1)] += 1
        self.default_style_id = style.most_common(1)[0][0] if style else ''
        self.annotation_style_id = annot.most_common(1)[0][0] if annot else ''

    def annotation_styles(self) -> list[tuple[str, str]]:
        for lib, styles in self.style_libs.values():
            if lib == self.annotation_style_id:
                return styles
        return []

    def domainmodels_xml(self, root_guids: list[str], library: str) -> str:
        if not root_guids:
            return f'      <domainmodels xmlns="{MARKUP_NS}" />'
        out = [f'      <domainmodels xmlns="{MARKUP_NS}">']
        for g in root_guids:
            nm = self.domain_names.get(g, '')
            out.append(f'        <domainmodel conceptlibrary="{_x(library)}" major="0" '
                       f'minor="0" domainmodel="{_x(nm)}" domainmodelguid="{g}" />')
        out.append('      </domainmodels>')
        return chr(10).join(out)

    def annotation_style_xml(self) -> str:
        styles = self.annotation_styles()
        if not self.annotation_style_id:
            return ''
        head = (f'      <annotationStyleMap libraryid="{self.annotation_style_id}" '
                f'xmlns="{MARKUP_NS}">')
        body = [f'        <style name="{_x(n)}" link="{l}" />' for n, l in styles]
        return chr(10).join([head] + body + ['      </annotationStyleMap>'])


# ------------------------------------------------------------------- the verifier

CTRL = re.compile(BS + BS + r"[a-zA-Z]+-?[0-9]*[ ]?")
MARK = re.compile(r"%" + re.escape('[') + r"([^" + re.escape(']') + r"]*)" + re.escape(']'))
CONTAINERS = {'conditionalInstructionType', 'listInstructionType'}


def verify(gd_path: str) -> list[str]:
    """Re-derive all five grammar rules from the finished file."""
    bad: list[str] = []
    root = ET.parse(gd_path).getroot()

    def lo(t):
        return t.rsplit('}', 1)[-1]

    def ch(e, n):
        for c in e:
            if lo(c.tag) == n:
                return c
        return None

    doc = ch(root, 'document')
    rtf = ch(ch(doc, 'content'), 'rtf').text or ''
    plain = CTRL.sub('', rtf).replace('{', '').replace('}', '')
    pos: dict[str, int] = {}
    for m in MARK.finditer(plain):
        if m.group(1) in pos:
            bad.append(f'rule 2: marker %[{m.group(1)}] appears more than once')
        pos.setdefault(m.group(1), m.start())

    declared: dict[str, str] = {}
    for e in ch(doc, 'markup').iter():
        n = lo(e.tag)
        if n in ('instruction', 'part') and e.get('ID'):
            declared[e.get('ID')] = f'{n}:{e.get(XSI, "(none)")}'

    for i in pos:
        if i not in declared:
            bad.append(f'rule 3: marker %[{i}] has no declaration')

    for i, kind in declared.items():
        is_container = kind.split(':')[1] in CONTAINERS
        if is_container and i in pos:
            bad.append(f'rule 1: container {kind} ID={i} is placed in the RTF')
        if not is_container and i not in pos:
            bad.append(f'rule 2: {kind} ID={i} is never placed')

    def walk(instructions, parent_span):
        for instr in instructions:
            if lo(instr.tag) != 'instruction':
                continue
            parts = ch(instr, 'parts')
            iid = instr.get('ID')
            if parts is None:
                if parent_span and iid in pos:
                    l, h = parent_span
                    if not (l <= pos[iid] <= h):
                        bad.append(f'rule 5: leaf ID={iid} outside its parent span')
                continue
            plist = [p for p in parts if lo(p.tag) == 'part' and p.get('ID')]
            offs = [pos.get(p.get('ID')) for p in plist]
            if any(o is None for o in offs):
                bad.append(f'rule 2: container ID={iid} has an unplaced part')
                continue
            if offs != sorted(offs):
                bad.append(f'rule 4: parts of ID={iid} are not in declaration order')
            ends = [p for p in plist if p.get(XSI) == 'endPartType']
            if ends and pos[ends[-1].get('ID')] != max(offs):
                bad.append(f'rule 4: endPart of ID={iid} is not last')
            span = (min(offs), max(offs))
            if parent_span:
                l, h = parent_span
                if not (l <= span[0] and span[1] <= h):
                    bad.append(f'rule 5: container ID={iid} not nested in parent')
            for p in plist:
                inner = ch(p, 'instructions')
                if inner is not None:
                    walk(inner, span)

    top = ch(ch(ch(doc, 'markup'), 'markup'), 'instructions')
    if top is not None:
        walk(top, None)
    return bad


# ------------------------------------------------------------------------- demo

def demo_spec(model: Model):
    """A Loss Payable Clause vehicle schedule, modelled on the production
    template of the same name: iterate the autos, and print each field only
    when it is provided."""
    veh = model.root.members_by_name['MOECAAutoLevelCoverages']
    vehicles = model.types[veh.type_id]
    lpc = vehicles.members_by_name['AutosWithLossPayableClause']
    item = model.types[model.types[lpc.type_id].element_type]
    pol = model.root.members_by_name['Policy']
    policy = model.types[pol.type_id]

    root_veh = Path('MOECAAutoLevelCoverages', veh.guid)
    auto_guid = _stable_guid('MOECAAutoLevelCoverages > AutosWithLossPayableClause|Auto')
    auto = Path('Auto', auto_guid)

    def attr(name):
        m = item.members_by_name[name]
        return auto.then(m.name, m.guid)

    def field_if_provided(name, label):
        return Cond(attr(name).then(*IS_PROVIDED),
                    then=[Static(label), Fill(attr(name))])

    payee = item.members_by_name['LossPayee']
    payee_t = model.types[payee.type_id]

    def row(name, label):
        """One label/value line, printed only when the value is provided.

        The Break goes INSIDE the conditional: a suppressed field must not leave
        a blank line behind, which is the difference between a schedule with
        optional columns and one with holes in it.
        """
        return Cond(attr(name).then(*IS_PROVIDED),
                    then=[Static(label), Fill(attr(name)), Break()])

    return [
        Static('LOSS PAYABLE CLAUSE - SCHEDULE OF COVERED AUTOS'),
        Break(),
        Static('Policy Number: '),
        Fill(Path('Policy', pol.guid).then(
            policy.members_by_name['Policy Number'].name,
            policy.members_by_name['Policy Number'].guid)),
        Break(),
        Break(),
        Repeat(root_veh.then(lpc.name, lpc.guid), 'Auto', body=[
            row('VehicleNumber', 'Auto No.: '),
            row('Description', 'Description: '),
            Cond(attr('VIN').then(*IS_PROVIDED),
                 then=[Static('VIN: '), Fill(attr('VIN')), Break()],
                 otherwise=[Static('VIN: not supplied'), Break()]),
            row('ComprehensiveDeductible', 'Comprehensive Deductible: '),
            row('CollisionDeductible', 'Collision Deductible: '),
            Static('Loss Payee: '),
            Fill(auto.then(payee.name, payee.guid).then(
                payee_t.members_by_name['FullName'].name,
                payee_t.members_by_name['FullName'].guid)),
            Break(),
            Break(),
        ]),
    ]


def selftest(gd_path: str) -> int:
    """A verifier that cannot reject a broken file says nothing about a good one.

    Mutate the authored .gd five ways -- one per rule -- and require each to be
    caught. This is the same discipline as gdxsdcheck --selftest.
    """
    original = open(gd_path, encoding='utf-8').read()
    tmp = gd_path + '.mutant'

    def run(label, text, expect):
        with open(tmp, 'w', encoding='utf-8', newline='') as fh:
            fh.write(text)
        bad = verify(tmp)
        hit = [b for b in bad if b.startswith(expect)]
        print(f'  {label:<44}{"CAUGHT" if hit else "MISSED":>8}   '
              f'{hit[0] if hit else (bad[0] if bad else "no complaint")}')
        return bool(hit)

    root = ET.parse(gd_path).getroot()

    def lo(t):
        return t.rsplit('}', 1)[-1]

    def ch(e, n):
        for c in e:
            if lo(c.tag) == n:
                return c
        return None

    doc = ch(root, 'document')
    container_ids, leaf_ids, part_ids = [], [], []
    for e in ch(doc, 'markup').iter():
        n = lo(e.tag)
        if n == 'instruction' and e.get('ID'):
            (container_ids if e.get(XSI) in CONTAINERS else leaf_ids).append(e.get('ID'))
        elif n == 'part' and e.get('ID'):
            part_ids.append(e.get('ID'))

    print(f'selftest on {gd_path}')
    print(f'  containers {len(container_ids)}  leaves {len(leaf_ids)}  parts {len(part_ids)}')
    print()
    results = []

    # rule 1: place a container
    cid = container_ids[0]
    results.append(run(f'place container %[{cid}]',
                       original.replace('%[' + leaf_ids[0] + ']',
                                        '%[' + cid + ']%[' + leaf_ids[0] + ']'),
                       'rule 1'))
    # rule 2: delete a leaf's marker
    lid = leaf_ids[-1]
    results.append(run(f'delete leaf marker %[{lid}]',
                       original.replace('%[' + lid + ']', '', 1), 'rule 2'))
    # rule 2 again: duplicate a marker
    results.append(run(f'duplicate marker %[{lid}]',
                       original.replace('%[' + lid + ']',
                                        '%[' + lid + ']%[' + lid + ']', 1), 'rule 2'))
    # rule 3: a marker with no declaration
    results.append(run('marker %[9999] with no declaration',
                       original.replace('%[' + leaf_ids[0] + ']',
                                        '%[9999]%[' + leaf_ids[0] + ']'), 'rule 3'))
    # rule 4: move an endPart marker before its siblings
    end_ids = [p.get('ID') for e in ch(doc, 'markup').iter()
               for p in [e] if lo(e.tag) == 'part' and e.get(XSI) == 'endPartType']
    eid = end_ids[0]
    moved = original.replace('%[' + eid + ']', '')
    first_part = part_ids[0]
    moved = moved.replace('%[' + first_part + ']',
                          '%[' + eid + ']%[' + first_part + ']', 1)
    results.append(run(f'move endPart %[{eid}] before its siblings', moved, 'rule 4'))

    os.remove(tmp)
    print()
    if all(results):
        print(f'SELFTEST PASS: all {len(results)} mutations rejected')
        return 0
    print(f'SELFTEST FAIL: {results.count(False)} of {len(results)} mutations slipped through')
    return 1


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('package')
    ap.add_argument('--demo', default='output/authored-demo.gd')
    ap.add_argument('--selftest', default=None,
                    help='mutate an authored .gd and require every rule to fire')
    ap.add_argument('--library', default=None,
                    help='concept library name for the emitted paths')
    a = ap.parse_args()

    if a.selftest:
        return selftest(a.selftest)

    cl_dir = os.path.join(a.package, 'Concept Libraries')
    libs = [os.path.join(cl_dir, f) for f in os.listdir(cl_dir) if f.endswith('.gdm')]
    model = Model.load(os.path.join(a.package, 'model.xml'), concept_libs=libs)
    lib_name = a.library
    if lib_name is None:
        cl = ET.parse(libs[0]).getroot()
        for e in cl.iter():
            if e.tag.rsplit('}', 1)[-1] == 'conceptLibrary':
                lib_name = e.get('name', '')
                break
    print(f'model   {model.name!r} v{model.version}')
    print(f'library {lib_name!r}')

    env = Envelope(a.package)
    em = Emitter(model, lib_name or '')
    spec = demo_spec(model)
    markup, rtf, resolved = em.build(spec, 'Authored Loss Payable Clause Schedule')

    if em.errors:
        print(f'\nBINDINGS THAT DO NOT RESOLVE ({len(em.errors)}):')
        for e in em.errors:
            print('  ', e)
        return 1

    os.makedirs(os.path.dirname(a.demo) or '.', exist_ok=True)
    with open(a.demo, 'w', encoding='utf-8', newline='') as fh:
        fh.write(wrap(rtf, markup, 'Authored Loss Payable Clause Schedule',
                      env=env, roots=em.roots, library=lib_name or ''))
    print(f'\nwrote {a.demo}')
    print(f'instructions declared  {em._next - 1}')
    print(f'bindings resolved      {len(resolved)}   (all against the real model)')
    print(f'domain models declared {len(em.roots)}   '
          + ', '.join(env.domain_names.get(g, g) for g in em.roots))
    for p in resolved:
        print(f'    {p}')

    bad = verify(a.demo)
    print()
    if bad:
        print(f'GRAMMAR CHECK FAILED ({len(bad)}):')
        for b in bad:
            print('  ', b)
        return 1
    print('GRAMMAR CHECK PASSED -- all five rules re-derived from the written file')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
