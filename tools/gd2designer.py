"""gd2designer -- convert a GhostDraft `.gd` template into a Document Designer import.

    python gd2designer.py <package-dir> "<template name>" [--out DIR]
    python gd2designer.py <package-dir> --forms-csv ghostdraft-forms.csv [--out DIR]

Writes <out>/<name>.json = { name, title, source, components, css, model, report }:

  components  GrapesJS component definitions using the designer's own types
              (data-field, conditional, choice/choice-branch, repeat, page-break)
  css         classes for the paragraph / character / cell formatting read from the RTF
  model       sample message payload covering every bound path (Server XML -> JSON, see below)
  report      what was converted, approximated or dropped

A `.gd` is XML with two halves (see fact-pdf-tools/tools/gdbindings.py):
  content/rtf                  the layout, as RTF, carrying %[ID] markers
  markup/markup/instructions   the logic: fill points, conditionals, lists, subscriptions
A fill point's marker is replaced by its value. A conditional/list is delimited by the markers of its
PARTS, in document order: [cond, cond..., else?, end] or [list, end].

Paths are GUID tuples against a concept library; fact-pdf-tools' gdmodel.Model resolves them to Server XML
paths (the package's own projection). Server XML -> JSON is mechanical:
  A/B/Leaf        -> A.B.Leaf
  A/Items/Auto    -> A.Autos[]   (loop alias = the designer's singular() of the key = Auto)
"""

from __future__ import annotations

import argparse
import base64
import copy
import csv
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter

PDF_TOOLS = os.environ.get('FACT_PDF_TOOLS', r'C:\src\fact-pdf-tools\tools')
sys.path.insert(0, PDF_TOOLS)
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from gdmodel import Model  # noqa: E402
import emfplus  # noqa: E402

try:
    from PIL import ImageFont
except ImportError:  # measuring is only used to detect justified lines
    ImageFont = None

XSI = '{http://www.w3.org/2001/XMLSchema-instance}type'
MARKER = re.compile(r'%\[([^\]]*)\]')


def local(tag):
    return tag.rsplit('}', 1)[-1]


def child(e, name):
    if e is None:
        return None
    for c in e:
        if local(c.tag) == name:
            return c
    return None


def text_of(e, name, default=''):
    c = child(e, name)
    return (c.text or default).strip() if c is not None else default


# =====================================================================================================
# RTF -> block tree
#   doc/cell/shape/region: {'k', 'kids': [blocks]}   para: {'k':'para', 'p': props, 'kids': [inlines]}
#   table: {'k':'table', 'kids': [rows]}   row: {'k':'row', 'def': rowdef, 'kids': [cells]}
#   inlines: run {'k':'run','t','c'} | tab | br | marker {'id'} | field {'name'} | img
# =====================================================================================================

TOKEN = re.compile(r"\\([a-zA-Z]+)(-?\d+)? ?|\\'([0-9a-fA-F]{2})|\\([^a-zA-Z])|([{}])|([^\\{}\r\n]+)|[\r\n]+", re.S)

SKIP_DESTS = {
    'stylesheet', 'info', 'listtable', 'listoverridetable', 'sp', 'shprslt', 'nonshppict', 'pnseclvl',
    'generator', 'userprops', 'company', 'title', 'author', 'operator', 'xmlnstbl', 'rsidtbl', 'themedata',
    'colorschememapping', 'latentstyles', 'datastore', 'bkmkstart', 'bkmkend', 'formfield', 'template',
    'picprop', 'pgdsctbl', 'revtbl', 'filetbl', 'doccomm', 'keywords', 'subject', 'comment', 'hlinkbase',
    'category', 'manager', 'creatim', 'revtim', 'printim', 'buptim', 'ftnsep', 'ftnsepc', 'ftncn', 'aftnsep',
    'aftnsepc', 'aftncn', 'objdata', 'blipuid', 'pntxta', 'pntxtb', 'listpicture', 'mmathPr', 'wgrffmtfilter',
    'propname', 'staticval', 'fldtype', 'panose', 'falt',
}
SINKS = {'header': 'header', 'headerr': 'header', 'headerl': 'header-other', 'headerf': 'header-first',
         'footer': 'footer', 'footerr': 'footer', 'footerl': 'footer-other', 'footerf': 'footer-first'}
# built-in document properties a field can show (TITLE, COMPANY, ... or DOCPROPERTY "Company")
DOC_PROPS = ('TITLE', 'SUBJECT', 'AUTHOR', 'KEYWORDS', 'COMMENTS', 'COMPANY', 'MANAGER', 'CATEGORY')
SPECIAL = {'endash': '\u2013', 'emdash': '\u2014', 'bullet': '\u2022', 'lquote': '\u2018', 'rquote': '\u2019',
           'ldblquote': '\u201c', 'rdblquote': '\u201d', 'emspace': '\u2003', 'enspace': '\u2002',
           'qmspace': '\u2005'}
SYMBOL = {0xB7: '\u2022', 0xA7: '\u2663', 0xD8: '\u00ac', 0x2D: '\u2212', 0xB0: '\u00b0', 0xB4: '\u00d7',
          0xA3: '\u2264', 0xB3: '\u2265', 0xB1: '\u00b1', 0xAE: '\u2192'}
WINGDINGS = {0xA7: '\u25aa', 0x6C: '\u25cf', 0x6E: '\u25a0', 0x71: '\u2751', 0x77: '\u2b25', 0xD8: '\u27a2',
             0xFC: '\u2714', 0xFB: '\u2718', 0xA8: '\u25fb', 0x6F: '\u25a1', 0xA1: '\u25cb', 0x76: '\u2756'}


def default_cp():
    return {'b': False, 'i': False, 'ul': False, 'strike': False, 'caps': False, 'fs': 24, 'f': 0, 'cf': 0,
            'va': '', 'v': False, 'scale': 100, 'sp': 0}


def default_pp():
    return {'align': 'l', 'li': 0, 'ri': 0, 'fi': 0, 'sb': 0, 'sa': 0, 'sl': 0, 'slmult': 0, 'intbl': False,
            'keepn': False}


def new_celldef():
    return {'x': 0, 'b': {}, 'vm': '', 'hm': '', 'valign': 't', 'bg': 0, 'pad': {}}


def new_rowdef():
    return {'left': 0, 'cells': [], 'pad': {}, 'hdr': False}


def cp_key(cp):
    return (cp['b'], cp['i'], cp['ul'], cp['strike'], cp['caps'], cp['fs'], cp['f'], cp['cf'], cp['va'],
            cp['scale'], cp['sp'])


class Container:
    def __init__(self, node):
        self.node = node
        self.table = None
        self.cells = []
        self.cell_blocks = []


class RtfParser:
    def __init__(self):
        self.fonts: dict[int, str] = {}
        self.colors: list[str | None] = []
        self.doc = {'k': 'doc', 'kids': []}
        self.sinks: dict[str, dict] = {}
        self.sink_all: dict[str, list] = {}
        self.containers = [{'c': Container(self.doc), 'depth': 0, 'saved': None}]
        self.inl: list = []
        self.pp = default_pp()
        self.colbreak = False   # a \column is pending: the next paragraph placed starts a new column
        self.colsplit = False   # ... and it split a paragraph (an empty remainder is dropped)
        self.rowdef = new_rowdef()
        self.celldef = new_celldef()
        self.border = None
        self.stack: list[dict] = []
        self.st = {'cp': default_cp(), 'dest': 'body', 'uc': 1}
        self.depth = 0
        self.skip = 0
        self.star = False
        self.font_id = 0
        self.font_name = ''
        self.rgb = [None, None, None]
        self.field_inst = ''
        self.pict = None
        self.dropped = Counter()
        self.sp_name = ''
        self.sp_val = ''
        self.page = {'paperw': 12240, 'paperh': 15840, 'margl': 1800, 'margr': 1800, 'margt': 1440, 'margb': 1440}

    # --------------------------------------------------------------------------------- tokens
    def parse(self, rtf: str) -> 'RtfParser':
        for m in TOKEN.finditer(rtf):
            word, num, hexc, sym, brace, txt = m.groups()
            if word is not None:
                self.control(word, int(num) if num is not None else None)
            elif hexc is not None:
                self.hexchar(int(hexc, 16))
            elif sym is not None:
                self.symbol(sym)
            elif brace == '{':
                self.stack.append(self.st)
                self.st = {'cp': dict(self.st['cp']), 'dest': self.st['dest'], 'uc': self.st['uc']}
                self.depth += 1
                self.star = False
            elif brace == '}':
                self.close_group()
            elif txt is not None:
                self.text(txt)
        while len(self.containers) > 1:
            self.close_container()
        self.finish(self.containers[0]['c'])
        return self

    def close_group(self):
        popped = self.st
        self.st = self.stack.pop() if self.stack else {'cp': default_cp(), 'dest': 'body', 'uc': 1}
        self.depth -= 1
        if popped.get('pict_owner'):
            self.finish_pict()
        if popped['dest'] == 'sv' and self.st['dest'] != 'sv':
            shape = self.current_shape()
            if shape is not None:
                shape['sp'][self.sp_name.strip()] = self.sp_val.strip()
        while len(self.containers) > 1 and self.containers[-1]['depth'] > self.depth:
            self.close_container()

    # ----------------------------------------------------------------------------- text sinks
    def text(self, s: str):
        if self.skip:
            n = min(self.skip, len(s))
            s = s[n:]
            self.skip -= n
            if not s:
                return
        dest = self.st['dest']
        if dest == 'skip':
            return
        if dest == 'fonttbl':
            for ch in s:
                if ch == ';':
                    self.fonts[self.font_id] = self.font_name.strip()
                    self.font_name = ''
                else:
                    self.font_name += ch
            return
        if dest == 'colortbl':
            for _ in range(s.count(';')):
                r, g, b = self.rgb
                self.colors.append(None if r is None else f'#{r:02x}{g or 0:02x}{b or 0:02x}')
                self.rgb = [None, None, None]
            return
        if dest == 'fldinst':
            self.field_inst += s
            return
        if dest == 'pict':
            if self.pict is not None:
                self.pict['hex'].append(s)
            return
        if dest == 'sn':
            self.sp_name += s
            return
        if dest == 'sv':
            self.sp_val += s
            return
        if dest == 'sp':
            return
        cp = self.st['cp']
        if cp['v']:
            return
        if self.inl and self.inl[-1]['k'] == 'run' and self.inl[-1]['c'] == cp_key(cp):
            self.inl[-1]['t'] += s
        else:
            self.inl.append({'k': 'run', 't': s, 'c': cp_key(cp)})

    def hexchar(self, code: int):
        if self.skip:
            self.skip -= 1
            return
        if self.st['dest'] == 'pict':
            return
        font = self.fonts.get(self.st['cp']['f'], '').lower()
        if 'symbol' in font:
            ch = SYMBOL.get(code, bytes([code]).decode('cp1252', 'replace'))
        elif 'wingdings' in font:
            ch = WINGDINGS.get(code, '\u25aa')
        else:
            ch = bytes([code]).decode('cp1252', 'replace')
        self.text(ch)

    def symbol(self, s: str):
        if s in '\\{}':
            self.text(s)
        elif s == '~':
            self.text('\u00a0')
        elif s == '_':
            self.text('\u2011')
        elif s == '*':
            self.star = True
        elif s in '\r\n':
            self.control('par', None)

    def inline(self, node):
        if self.st['dest'] == 'body' and not self.st['cp']['v']:
            self.inl.append(node)

    # --------------------------------------------------------------------------- containers
    def top(self) -> Container:
        return self.containers[-1]['c']

    def current_shape(self):
        for entry in reversed(self.containers):
            if entry['c'].node.get('k') == 'shape':
                return entry['c'].node
        return None

    def place(self, block, intbl: bool):
        c = self.top()
        if intbl:
            c.cell_blocks.append(block)
            return
        c.table = None
        c.node['kids'].append(block)

    def finalize_para(self, force_intbl=False):
        intbl = self.pp['intbl'] or force_intbl
        if self.colbreak and self.colsplit and not any(n['k'] != 'run' or n['t'].strip() for n in self.inl):
            # nothing followed the column break in its paragraph: the next paragraph starts the column
            self.inl = []
            self.colsplit = False
            return
        kids, seq = [], []
        for n in self.inl + [None]:
            if n is not None and n['k'] == 'run':
                seq.append(n)
                continue
            if seq:
                kids.extend(split_marker_runs(seq))
                seq = []
            if n is not None:
                kids.append(n)
        self.inl = []
        p = dict(self.pp)
        p['mark_fs'] = self.st['cp']['fs']
        if getattr(self, 'after_page', False):
            p['sb'] = 0
            self.after_page = False
        if self.colbreak:
            p['colbreak'] = True
            # like after \page: the paragraph began in the column before, so the next one opens without its space
            # before (DA 00 93's right-hand symbol column, 5.8pt too low)
            p['sb'] = 0
            self.colbreak = self.colsplit = False
        self.place({'k': 'para', 'p': p, 'kids': kids}, intbl)

    def column_break(self):
        """\\column: what follows starts the next newspaper column. At the start of a paragraph (after its list
        number at most) the paragraph moves; after text the paragraph is split there."""
        if self.st['dest'] != 'body':
            return
        kids = self.inl
        tab = next((i for i, n in enumerate(kids) if n['k'] == 'tab'), None)
        if tab is not None and re.fullmatch(r'\s*\S{1,4}\s*', ''.join(n.get('t', '') for n in kids[:tab] if n['k'] == 'run')):
            kids = kids[tab + 1:]
        if any(n['k'] != 'run' or n['t'].strip() for n in kids if n['k'] != 'tab'):
            self.finalize_para()
            self.colsplit = True
        self.colbreak = True

    def open_container(self, node):
        self.containers.append({'c': Container(node), 'depth': self.depth,
                                'saved': (self.inl, self.pp, self.rowdef, self.celldef)})
        self.inl = []
        self.pp = default_pp()
        self.rowdef = new_rowdef()
        self.celldef = new_celldef()

    def finish(self, c: Container):
        if self.inl:
            self.finalize_para()
        if c.cell_blocks:
            c.node['kids'].extend(c.cell_blocks)
            c.cell_blocks = []
        c.table = None

    def close_container(self):
        entry = self.containers.pop()
        self.finish(entry['c'])
        self.inl, self.pp, self.rowdef, self.celldef = entry['saved']

    def end_cell(self):
        self.finalize_para(force_intbl=True)
        c = self.top()
        c.cells.append({'k': 'cell', 'kids': c.cell_blocks})
        c.cell_blocks = []

    def end_row(self):
        c = self.top()
        if self.inl:
            self.end_cell()
        row = {'k': 'row', 'def': json.loads(json.dumps(self.rowdef)), 'kids': c.cells}
        c.cells = []
        if c.table is None:
            c.table = {'k': 'table', 'kids': []}
            c.node['kids'].append(c.table)
        c.table['kids'].append(row)

    def finish_pict(self):
        p, self.pict = self.pict, None
        if not p:
            return
        data = bytes.fromhex(re.sub(r'\s+', '', ''.join(p['hex'])))
        if p['type'] == 'emf':
            self.inl.append({'k': 'img', 'src': None, 'emf': data,
                             'w': p['wgoal'] * p['sx'] / 100 / 20 if p['wgoal'] else None,
                             'h': p['hgoal'] * p['sy'] / 100 / 20 if p['hgoal'] else None})
            return
        if p['type'] in ('png', 'jpeg'):
            src = f"data:image/{p['type']};base64," + base64.b64encode(data).decode()
            w = p['wgoal'] * p['sx'] / 100 / 20 if p['wgoal'] else None
            self.inl.append({'k': 'img', 'src': src, 'w': w})
        else:
            self.dropped[f"{p['type'] or 'unknown'} picture"] += 1
            self.inl.append({'k': 'img', 'src': None, 'w': p['wgoal'] * p['sx'] / 100 / 20 if p['wgoal'] else None,
                             'h': p['hgoal'] * p['sy'] / 100 / 20 if p['hgoal'] else None})

    # ------------------------------------------------------------------------- control words
    def control(self, w: str, n: int | None):
        star, self.star = self.star, False
        st = self.st
        dest = st['dest']

        if dest == 'skip':
            return
        if dest == 'fonttbl':
            if w == 'f':
                self.font_id = n or 0
                self.font_name = ''
            elif w in SKIP_DESTS or star:
                st['dest'] = 'skip'
            return
        if dest == 'colortbl':
            if w in ('red', 'green', 'blue'):
                self.rgb[('red', 'green', 'blue').index(w)] = n or 0
            return
        if dest == 'pict':
            p = self.pict
            if p is None:
                return
            if w in ('pngblip', 'jpegblip', 'emfblip', 'wmetafile', 'dibitmap', 'wbitmap', 'macpict'):
                p['type'] = {'pngblip': 'png', 'jpegblip': 'jpeg', 'emfblip': 'emf'}.get(w, w)
            elif w in ('picwgoal', 'pichgoal', 'picscalex', 'picscaley'):
                p[{'picwgoal': 'wgoal', 'pichgoal': 'hgoal', 'picscalex': 'sx', 'picscaley': 'sy'}[w]] = n or 0
            elif w in SKIP_DESTS or star:
                st['dest'] = 'skip'
            return
        if dest == 'fldinst':
            return
        if dest in ('sp', 'sn', 'sv'):
            if w == 'sn':
                st['dest'] = 'sn'
                self.sp_name = ''
            elif w == 'sv':
                st['dest'] = 'sv'
                self.sp_val = ''
            return
        if w == 'sp':
            st['dest'] = 'sp'
            return

        # destinations
        if w == 'fonttbl':
            st['dest'] = 'fonttbl'
            return
        if w == 'colortbl':
            st['dest'] = 'colortbl'
            return
        if w in SINKS:
            # each section repeats its headers/footers; the document's is the first section's that has content
            node = {'k': 'doc', 'kids': []}
            self.sinks.setdefault(SINKS[w], node)
            self.sink_all.setdefault(SINKS[w], []).append(node)
            self.open_container(node)
            return
        if w == 'fldinst':
            st['dest'] = 'fldinst'
            self.field_inst = ''
            return
        if w == 'fldrslt':
            inst = self.field_inst.strip().split(' ')[0].upper() if self.field_inst.strip() else ''
            if inst in ('PAGE', 'NUMPAGES', 'SECTIONPAGES'):
                self.inline({'k': 'field', 'name': inst, 'c': cp_key(self.st['cp'])})
                st['dest'] = 'skip'
            elif inst in DOC_PROPS or inst == 'DOCPROPERTY':
                # document-property fields show the ASSEMBLED document's properties (the form's), not the cached
                # result of the subscription they sit in (the ISO footer caches "ISO" for COMPANY)
                prop = inst
                if inst == 'DOCPROPERTY':
                    m = re.match(r'\s*DOCPROPERTY\s+"?([^"\\]+?)"?(\s|\\|$)', self.field_inst, re.I)
                    prop = m.group(1).strip().upper() if m else ''
                if prop in DOC_PROPS:
                    self.inline({'k': 'field', 'name': 'PROP', 'prop': prop, 'c': cp_key(self.st['cp'])})
                    st['dest'] = 'skip'
            return
        if w == 'pict':
            st['dest'] = 'pict'
            st['pict_owner'] = True
            self.pict = {'type': None, 'hex': [], 'wgoal': 0, 'hgoal': 0, 'sx': 100, 'sy': 100}
            return
        if w == 'shp':
            shape = {'k': 'shape', 'kids': [], 'box': [0, 0, 0, 0], 'sp': {}, 'relh': 'column', 'relv': 'para'}
            self.inl.append(shape)
            self.open_container(shape)
            return
        if w in ('shpinst', 'shptxt', 'shppict', 'field', 'listtext', 'pntext', 'upr', 'ud'):
            return
        if w in SKIP_DESTS or (star and w not in ('shpinst',)):
            st['dest'] = 'skip'
            return

        cp = st['cp']
        pp = self.pp
        on = n is None or n != 0
        # paragraphs
        if w == 'par':
            self.finalize_para()
        elif w == 'pard':
            self.pp = default_pp()
        elif w == 'plain':
            st['cp'] = default_cp()
        elif w in ('ql', 'qc', 'qr', 'qj'):
            pp['align'] = w[1]
        elif w in ('li', 'ri', 'fi', 'sb', 'sa', 'sl', 'slmult'):
            pp[w] = n or 0
        elif w == 'intbl':
            pp['intbl'] = True
        elif w == 'keepn':
            pp['keepn'] = True
        # characters
        elif w in ('b', 'i', 'strike', 'caps', 'scaps'):
            cp['caps' if w == 'scaps' else w] = on
        elif w in ('ul', 'ulth', 'uldb', 'ulw', 'uld', 'uldash', 'ulthd'):
            cp['ul'] = on
        elif w in ('ulnone',):
            cp['ul'] = False
        elif w == 'fs':
            cp['fs'] = n or 24
        elif w == 'f':
            cp['f'] = n or 0
        elif w == 'cf':
            cp['cf'] = n or 0
        elif w == 'charscalex':
            cp['scale'] = n or 100
        elif w == 'expndtw':
            cp['sp'] = n or 0
        elif w == 'expnd':
            cp['sp'] = (n or 0) * 5      # quarter points; \expndtw (twips) follows it when Word writes both
        elif w == 'super':
            cp['va'] = 'super'
        elif w == 'sub':
            cp['va'] = 'sub'
        elif w == 'nosupersub':
            cp['va'] = ''
        elif w == 'v':
            cp['v'] = on
        elif w == 'uc':
            st['uc'] = n or 0
        elif w == 'u':
            code = n if n is not None and n >= 0 else (n or 0) + 65536
            self.text(chr(code))
            self.skip = st['uc']
        elif w in SPECIAL:
            self.text(SPECIAL[w])
        elif w == 'tab':
            self.inline({'k': 'tab'})
        elif w == 'line':
            self.inline({'k': 'br'})
        elif w == 'column':
            self.column_break()
        elif w == 'page' or (w == 'sect' and self.containers[-1]['c'].node is self.doc):
            if self.inl:
                self.finalize_para()
            node = {'k': 'pagebreak'}
            self.place(node, False)
            # the new section's \sectd properties follow \sect: \sbknone makes it continuous, \colsN adds columns
            self.last_sect = node if w == 'sect' else None
            self.cont_sect = False
            # the rest of a paragraph broken by \page opens the next page without its space before: the paragraph
            # began on the page before (DA 00 93 page 2: sb224 on the page-break paragraph, 11pt too low)
            self.after_page = w == 'page'
        elif w == 'sbknone' and getattr(self, 'last_sect', None):
            self.last_sect['k'] = 'colsect'
            # a continuous section continues the page: its header/footer distances and margins only apply to
            # pages that START in it (DA 00 93's two-column symbol list says \headery0 -- page 2 keeps 576)
            self.cont_sect = True
        elif w in ('sbknone', 'sbkpage', 'sbkcol', 'sbkeven', 'sbkodd'):
            self.page.setdefault('sbk', w)
        elif w == 'cols' and getattr(self, 'last_sect', None):
            self.last_sect['cols'] = n or 1
        elif w == 'colsx' and getattr(self, 'last_sect', None):
            self.last_sect['gap'] = n or 0
        elif w in ('colw', 'colsr') and getattr(self, 'last_sect', None):
            self.last_sect.setdefault(w, []).append(n or 0)
        # tables
        elif w == 'cell':
            self.end_cell()
        elif w == 'row':
            self.end_row()
        elif w == 'trowd':
            self.rowdef = new_rowdef()
            self.celldef = new_celldef()
            self.border = None
        elif w == 'trleft':
            self.rowdef['left'] = n or 0
        elif w == 'trftsWidth':
            self.rowdef['fts'] = n or 0
        elif w == 'trwWidth':
            self.rowdef['ww'] = n or 0
        elif w == 'trhdr':
            self.rowdef['hdr'] = True
        elif w == 'trrh':
            self.rowdef['h'] = n or 0
        elif w in ('trpaddl', 'trpaddr', 'trpaddt', 'trpaddb'):
            self.rowdef['pad'][w[-1]] = n or 0
        elif w in ('clpadl', 'clpadr', 'clpadt', 'clpadb'):
            # Word (and GhostDraft) write the TOP cell margin as \clpadl and the LEFT one as \clpadt
            self.celldef['pad'][{'l': 't', 't': 'l'}.get(w[-1], w[-1])] = n or 0
        elif w in ('clbrdrt', 'clbrdrl', 'clbrdrb', 'clbrdrr'):
            self.border = self.celldef['b'].setdefault(w[-1], {'s': 'solid', 'w': 10, 'c': 0})
        elif w.startswith('trbrdr') or w.startswith('brdr') and w in ('brdrt', 'brdrl', 'brdrb', 'brdrr', 'brdrbtw', 'box'):
            self.border = {}   # row/paragraph borders: read, not rendered
        elif w in ('brdrs', 'brdrth', 'brdrsh', 'brdrdot', 'brdrdash', 'brdrdb', 'brdrnone', 'brdrtriple'):
            if self.border is not None:
                self.border['s'] = {'brdrnone': 'none', 'brdrdb': 'double', 'brdrtriple': 'double',
                                    'brdrdot': 'dotted', 'brdrdash': 'dashed'}.get(w, 'solid')
        elif w == 'brdrw':
            if self.border is not None:
                self.border['w'] = n or 0
        elif w == 'brdrcf':
            if self.border is not None:
                self.border['c'] = n or 0
        elif w in ('clvmgf', 'clvmrg'):
            self.celldef['vm'] = 'first' if w == 'clvmgf' else 'cont'
        elif w in ('clmgf', 'clmrg'):
            self.celldef['hm'] = 'first' if w == 'clmgf' else 'cont'
        elif w in ('clvertalt', 'clvertalc', 'clvertalb'):
            self.celldef['valign'] = w[-1]
        elif w == 'clcbpat':
            self.celldef['bg'] = n or 0
        elif w == 'cellx':
            self.celldef['x'] = n or 0
            self.rowdef['cells'].append(self.celldef)
            self.celldef = new_celldef()
            self.border = None
        # shapes
        elif w in ('shpleft', 'shptop', 'shpright', 'shpbottom'):
            node = self.current_shape()
            if node is not None:
                node['box'][('shpleft', 'shptop', 'shpright', 'shpbottom').index(w)] = n or 0
        elif w in ('shpbxpage', 'shpbxmargin', 'shpbxcolumn', 'shpbypage', 'shpbymargin', 'shpbypara'):
            node = self.current_shape()
            if node is not None:
                node['relh' if w[4] == 'x' else 'relv'] = w[5:]
        # page setup (document or section)
        elif w in ('headery', 'footery'):
            if not getattr(self, 'cont_sect', False):
                self.page[w] = n or 0
        elif getattr(self, 'cont_sect', False) and (
                w.rstrip('sxn') in ('margl', 'margr', 'margt', 'margb', 'paperw', 'paperh') or
                w in ('marglsxn', 'margrsxn', 'margtsxn', 'margbsxn', 'pgwsxn', 'pghsxn')):
            # ...but its side margins narrow or widen its own text (DA 00 93's footnote section: 0.25in margins)
            if w in ('margl', 'margr', 'marglsxn', 'margrsxn') and getattr(self, 'last_sect', None):
                self.last_sect[w[:5]] = n or 0
        elif w.rstrip('sxn') in ('margl', 'margr', 'margt', 'margb', 'paperw', 'paperh') or \
                w in ('marglsxn', 'margrsxn', 'margtsxn', 'margbsxn', 'pgwsxn', 'pghsxn'):
            key = {'pgwsxn': 'paperw', 'pghsxn': 'paperh'}.get(w, w[:5] if w.startswith('marg') else w)
            self.page[key] = n or 0


# =====================================================================================================
# Tree helpers
# =====================================================================================================

BLOCKISH = ('doc', 'cell', 'shape', 'region')


def split_marker_runs(runs):
    """Split %[ID] markers out of consecutive runs. A marker can straddle runs (Word re-styles '%' apart from
    '[5]'), so markers are found in the joined text and the runs are cut around them."""
    text = ''.join(r['t'] for r in runs)
    owner = [i for i, r in enumerate(runs) for _ in r['t']]
    spans = [(m.start(), m.end(), m.group(1)) for m in MARKER.finditer(text)]
    out, pos = [], 0

    def emit(a, b):
        i = a
        while i < b:
            ri = owner[i]
            j = i
            while j < b and owner[j] == ri:
                j += 1
            out.append({'k': 'run', 't': text[i:j], 'c': runs[ri]['c']})
            i = j

    for a, b, mid in spans:
        emit(pos, a)
        # GhostDraft prints a fill point's value in the formatting of its marker's '%' (Word may style '[1]' apart:
        # DA 00 93 Named Insured '%' 9.5pt 110% wide, '[1]' 10pt -> the value prints 9.5pt 110% wide; the premium
        # '%' 8pt regular, '[17]' 10pt -> 8pt)
        out.append({'k': 'marker', 'id': mid, 'c': runs[owner[a]]['c']})
        pos = b
    emit(pos, len(text))
    return out


def find_marker(node, mid, path=None):
    """[(container, index), ...] from `node` down to the marker, or None."""
    path = path or []
    for i, k in enumerate(node.get('kids', [])):
        if k.get('k') == 'marker' and k.get('id') == mid:
            return path + [(node, i)]
        if 'kids' in k:
            r = find_marker(k, mid, path + [(node, i)])
            if r:
                return r
    return None


def is_empty_inline(kids):
    return all(k['k'] == 'run' and not k['t'].strip() for k in kids)


def run_sizes(kids):
    """Font sizes (half points) of the text a paragraph prints: its runs and fill points (None = a fill point with
    no formatting of its own), inside Show Ifs too."""
    out = []
    for k in kids:
        if k['k'] == 'run' and k['t'].strip():
            out.append(k['c'][5] if k['c'] else None)
        elif k['k'] == 'fill':
            out.append(k['c'][5] if k.get('c') else None)
        elif k['k'] in ('region', 'para') or ('kids' in k and k['k'] not in ('shape',)):
            out.extend(run_sizes(k.get('kids', [])))
    return out


def has_content(n):
    if n['k'] in ('run',):
        return bool(n['t'].strip())
    if n['k'] in ('marker',):
        return True
    if n['k'] in ('tab', 'br', 'secbreak', 'colsect'):
        return False
    if 'kids' in n:
        return any(has_content(k) for k in n['kids'])
    return True


def lift(root, mid, target, notes):
    """Move marker `mid` up until it is a direct kid of `target`, splitting paragraphs on the way."""
    for _ in range(64):
        path = find_marker(root, mid)
        if path is None:
            return False
        parent, mi = path[-1]
        if parent is target:
            return True
        if len(path) < 2:
            return False
        gp, pi = path[-2]
        before, after = parent['kids'][:mi], parent['kids'][mi + 1:]
        marker = parent['kids'][mi]
        if parent['k'] in ('para',) or (parent['k'] == 'region' and parent.get('inline')):
            pieces = []
            if before and not is_empty_inline(before):
                pieces.append(dict(parent, kids=before))
            pieces.append(marker)
            if after and not is_empty_inline(after):
                pieces.append(dict(parent, kids=after))
            gp['kids'][pi:pi + 1] = pieces
        else:
            # cells/rows/shapes/tables cannot be split: move the marker to whichever side has no content
            pb = any(has_content(k) for k in before)
            pa = any(has_content(k) for k in after)
            del parent['kids'][mi]
            if pb and pa:
                notes.append(f'marker {mid} moved out of a {parent["k"]} that has content on both sides')
            at = pi + 1 if (pb and not pa) else pi
            gp['kids'].insert(at, marker)
    return False


def lca(p1, p2):
    c = None
    for (a, _), (b, _) in zip(p1, p2):
        if a is b:
            c = a
        else:
            break
    return c


def remove_marker(root, mid):
    path = find_marker(root, mid)
    if path:
        parent, i = path[-1]
        del parent['kids'][i]


# =====================================================================================================
# Server XML path -> Liquid path; conditions
# =====================================================================================================

def ident(s):
    s = re.sub(r'[^A-Za-z0-9_]', '_', s or '')
    return s if re.match(r'^[A-Za-z_]', s) else '_' + s


def plural(e):
    e = ident(e)
    return e + 'es' if e.endswith('s') else e + 's'


def designer_singular(key):
    return key[:-1] if key.endswith('s') else key + 'Item'


def convert_segs(segs):
    out, i = [], 0
    while i < len(segs):
        if segs[i] == 'Items' and i + 1 < len(segs):
            out.append(plural(segs[i + 1]))
            i += 2
            if i < len(segs):
                out.append('first')
        else:
            out.append(ident(segs[i]))
            i += 1
    return out


def liquid(xml, scopes):
    xml = (xml or '').strip('/')
    for sc in reversed(scopes):
        if xml == sc['xml']:
            return sc['alias']
        if xml.startswith(sc['xml'] + '/'):
            return '.'.join([sc['alias']] + convert_segs(xml[len(sc['xml']) + 1:].split('/')))
    return '.'.join(convert_segs(xml.split('/'))) if xml else ''


NEGATE = {'present': 'blank', 'blank': 'present', 'eq': 'ne', 'ne': 'eq'}


def negate(cond):
    op = cond['operator']
    if op in NEGATE:
        return dict(cond, operator=NEGATE[op])
    if op == 'gt' and re.fullmatch(r'-?\d+', cond['value']):
        return dict(cond, operator='lt', value=str(int(cond['value']) + 1))
    if op == 'lt' and re.fullmatch(r'-?\d+', cond['value']):
        return dict(cond, operator='gt', value=str(int(cond['value']) - 1))
    return None


# =====================================================================================================
# Converter
# =====================================================================================================

class Converter:
    def __init__(self, package: str):
        self.package = package
        libs = sorted(glob.glob(os.path.join(package, 'Concept Libraries', '*.gdm')))
        self.model = Model.load(os.path.join(package, 'model.xml'), concept_libs=libs)
        self.tdir = os.path.join(package, 'Templates')
        self.index = {os.path.splitext(f)[0].lower(): os.path.join(self.tdir, f)
                      for f in os.listdir(self.tdir) if f.lower().endswith('.gd')}

    # ------------------------------------------------------------------------ one template
    def convert(self, name: str):
        self.notes: list[str] = []
        self.counts = Counter()
        self.bind: dict[str, dict] = {}       # model registrations: xml path -> {kind, value?}
        self.bool_tests: set[str] = set()     # liquid paths of the yes/no tests conditions read
        self.fonts: dict[int, str] = {}
        self.colors: list = []
        tree = self.template_tree(name, stack=())
        if tree is None:
            raise FileNotFoundError(name)
        return tree

    def template_tree(self, name, stack):
        # the package export escapes ':' in file names as '_-1' ("Header: Policy Number ..." ->
        # "Header_-1 Policy Number ....gd")
        path = self.index.get(name.lower()) or self.index.get(name.lower().replace(':', '_-1'))
        if path is None:
            return None
        if name.lower() in stack or len(stack) > 8:
            self.notes.append(f'subscription cycle/depth stop at "{name}"')
            return {'doc': {'k': 'doc', 'kids': []}, 'sinks': {}, 'title': name}
        root = ET.parse(path).getroot()
        doc = child(root, 'document')
        title = ''
        props = child(doc, 'properties')
        values = {}
        for group in (props if props is not None else []):
            for p in group:
                if p.get('name') == 'Title':
                    title = text_of(p, 'value')
                if (p.get('name') or '').upper() in DOC_PROPS:
                    values[p.get('name').upper()] = (text_of(p, 'value') or '').strip()
        if not stack:
            self.props = values
        rtf = child(child(doc, 'content'), 'rtf')
        parser = RtfParser().parse(rtf.text or '' if rtf is not None else '')
        for key, defs in parser.sink_all.items():
            parser.sinks[key] = next((d for d in defs if has_content(d)), defs[0])
        if not stack:
            self.page = parser.page
        # fonts/colors differ per template; index them by name so classes are shared
        fontmap = {k: v for k, v in parser.fonts.items()}
        self.remap_styles(parser, fontmap)
        for k, v in parser.dropped.items():
            self.counts['dropped ' + k] += v

        roots = [parser.doc] + list(parser.sinks.values())
        fills: dict[str, dict | None] = {}
        ops: list = []
        markup = child(doc, 'markup')
        for m in (markup if markup is not None else []):
            if local(m.tag) == 'markup':
                self.walk(child(m, 'instructions'), [], fills, ops)

        # 1. fill points / subscriptions / annotations replace their marker
        for r in roots:
            self.replace_fills(r, fills)
        # 2. subscriptions first, so block content splits paragraphs before regions are cut around it
        stack = stack + (name.lower(),)
        sinks = dict(parser.sinks)
        for key in list(sinks):
            sinks[key] = self.expand(sinks[key], stack, sinks)
        body = self.expand(parser.doc, stack, sinks)
        roots = [body] + list(sinks.values())
        # 3. regions, innermost first
        for op in ops:
            self.apply(roots, op)
        # 4. anything left
        for r in roots:
            for mid in self.leftover(r):
                self.notes.append(f'marker %[{mid}] has no instruction; dropped')
                remove_marker(r, mid)
        return {'doc': body, 'sinks': sinks, 'title': title, 'page': parser.page}

    def remap_styles(self, parser, fontmap):
        """Rewrite run style keys to carry the font NAME and color VALUE instead of per-template indexes."""
        def fix(node):
            for k in node.get('kids', []):
                if k['k'] in ('run', 'marker') or (k['k'] == 'field' and k.get('c')):
                    b, i, ul, s, caps, fs, f, cf, va, *rest = k['c']
                    color = parser.colors[cf] if 0 < cf < len(parser.colors) else None
                    k['c'] = (b, i, ul, s, caps, fs, fontmap.get(f, ''), color, va, *rest)
                elif k['k'] == 'row':
                    for cd in k['def']['cells']:
                        bg = cd.get('bg') or 0
                        cd['bg'] = parser.colors[bg] if 0 < bg < len(parser.colors) else None
                        for side in cd['b'].values():
                            c = side.get('c') or 0
                            side['c'] = parser.colors[c] if 0 < c < len(parser.colors) else None
                    fix(k)
                elif 'kids' in k:
                    fix(k)
        fix(parser.doc)
        for s in parser.sinks.values():
            fix(s)

    # ------------------------------------------------------------------------ instructions
    def walk(self, instrs, scopes, fills, ops):
        for ins in (instrs if instrs is not None else []):
            if local(ins.tag) != 'instruction':
                continue
            t, iid = ins.get(XSI, ''), ins.get('ID', '')
            desc = text_of(ins, 'description')
            if t == 'fillPointType':
                fills[iid] = self.fill_node(ins, desc, scopes)
            elif t == 'subscriptionType':
                sub = child(ins, 'subscriptionDocumentName')
                fills[iid] = {'k': 'sub', 'name': sub.get('name', '') if sub is not None else ''}
                self.counts['subscriptions'] += 1
            elif t == 'annotationType':
                fills[iid] = None
            elif t == 'listInstructionType':
                self.list_op(ins, desc, scopes, fills, ops)
            elif t == 'conditionalInstructionType':
                self.cond_op(ins, scopes, fills, ops)
            else:
                self.notes.append(f'instruction {iid} type {t} not supported')

    def parts(self, ins):
        ps = child(ins, 'parts')
        return [p for p in (ps if ps is not None else []) if local(p.tag) == 'part']

    def resolve(self, pe, scopes):
        root_name = text_of(pe, 'rootNode')
        root_guid = text_of(pe, 'rootguid')
        nodes = []
        pn = child(pe, 'pathNodes')
        for n in (pn if pn is not None else []):
            if local(n.tag) == 'node':
                nodes.append((n.get('name', ''), n.get('guid', '')))
        scope = {s['guid']: (s['xml'], s['type'], '') for s in scopes if s.get('guid')}
        return self.model.resolve_path(root_guid, root_name, nodes, scope), root_name, nodes

    def fill_node(self, ins, desc, scopes):
        self.counts['fill points'] += 1
        r, rn, nodes = self.resolve(child(ins, 'path'), scopes)
        if not r.ok or not r.xml_path:
            self.notes.append(f'fill point "{desc}" unresolved: {r.reason}')
            self.counts['unresolved'] += 1
            path = 'unresolved.' + ident(nodes[-1][0] if nodes else rn)
            self.register('unresolved/' + ident(nodes[-1][0] if nodes else rn), 'text')
            return {'k': 'fill', 'field': path, 'format': ''}
        rtype = (r.type_id or '')
        fmt = ''
        lowered = desc.lower()
        if 'mm/dd/yyyy' in lowered or (rtype.startswith('Date') and not r.xml_path.endswith('Wording')):
            fmt = 'shortdate'
        elif 'as dollars' in lowered:
            # "without cents as Dollars with comma grouping" prints $1,267; "as Dollars with comma grouping" $1,267.00
            fmt = 'dollars' if 'without cents' in lowered else 'currency'
        elif 'without cents' in lowered:
            fmt = 'number'
        elif 'comma grouping' in lowered:
            # GhostDraft keeps a Currency amount's cents: Premium 24 "with comma grouping" prints 24.00
            fmt = 'decimal' if rtype.startswith('Currency') else 'number'
        elif 'dollars' in lowered or rtype.startswith('Currency'):
            fmt = 'dollars'
        # an unformatted Decimal prints as given: GhostDraft keeps a .NET decimal's scale (Server XML 1.37600 prints
        # 1.37600, DA 02 93), so the message carries it as text rather than a number the composer would normalise
        plain_decimal = not fmt and rtype.startswith('Decimal')
        kind = 'date' if fmt == 'shortdate' else 'money' if fmt in ('dollars', 'decimal', 'currency') else \
            'text' if plain_decimal else \
            'number' if fmt == 'number' or rtype.startswith(('Integer', 'Number', 'Decimal')) else 'text'
        self.register(r.xml_path, kind)
        return {'k': 'fill', 'field': liquid(r.xml_path, scopes), 'format': fmt}

    def register(self, xml, kind, value=None):
        cur = self.bind.get(xml)
        if cur is None or cur['kind'] in ('present',):
            self.bind[xml] = {'kind': kind, 'value': value}

    def condition(self, part, scopes):
        ptype = part.get(XSI, '')
        if ptype == 'elsePartType':
            return None
        neg = text_of(part, 'isNegative') == 'true'
        pe = child(part, 'path')
        if ptype == 'compositeConditionalPartType':
            paths = child(part, 'paths')
            pe = next(iter(paths), None) if paths is not None else None
            self.notes.append(f'composite condition "{text_of(part, "description")}" uses only its first test')
        if pe is None:
            return {'field': 'unresolved.condition', 'operator': 'present', 'value': ''}
        r, rn, nodes = self.resolve(pe, scopes)
        desc = text_of(part, 'description')
        if not r.ok:
            self.counts['unresolved'] += 1
            self.notes.append(f'condition "{desc}" unresolved: {r.reason}')
            flag = 'unresolved/' + ident(desc)[:60]
            self.register(flag, 'bool')
            c = {'field': liquid(flag, []), 'operator': 'eq', 'value': 'true'}
        else:
            c = self.cond_from(r, scopes, desc)
        if neg:
            n = negate(c)
            if n is None:
                self.notes.append(f'cannot negate condition "{desc}"; kept positive')
            else:
                c = n
        return c

    def cond_from(self, r, scopes, desc):
        leaf = r.leaf_kind
        base = liquid(r.xml_path, scopes)
        if leaf == 'builtin-test':
            name = (r.steps[-1].name if r.steps else '').strip().lower()
            m = re.match(r'has (\d+) or (more|fewer) elements', name)
            if name in ('is provided', 'is not provided'):
                self.register(r.xml_path, 'present')
                return {'field': base, 'operator': 'present' if name == 'is provided' else 'blank', 'value': ''}
            if m:
                self.register(r.xml_path, 'list')
                n = int(m.group(1))
                return {'field': base + '.size', 'operator': 'gt' if m.group(2) == 'more' else 'lt',
                        'value': str(n - 1 if m.group(2) == 'more' else n + 1)}
            m = re.match(r'has exactly (\d+) elements?', name)
            if m:
                self.register(r.xml_path, 'list')
                return {'field': base + '.size', 'operator': 'eq', 'value': m.group(1)}
            if name == 'has no elements':
                self.register(r.xml_path, 'list')
                return {'field': base, 'operator': 'blank', 'value': ''}
            pos = {'is first': ('forloop.first', 'eq'), 'is last': ('forloop.last', 'eq'),
                   'is not first': ('forloop.first', 'ne'), 'is not last': ('forloop.last', 'ne')}
            if name in pos:
                return {'field': pos[name][0], 'operator': pos[name][1], 'value': 'true'}
            if name == 'is only':
                return {'field': 'forloop.length', 'operator': 'eq', 'value': '1'}
            self.notes.append(f'list-position test "{name}" approximated as "is first"')
            return {'field': 'forloop.first', 'operator': 'eq', 'value': 'true'}
        if leaf == 'mutex-value':
            value = r.filters[-1].split('==', 1)[1].strip('"') if r.filters and '==' in r.filters[-1] else ''
            self.register(r.xml_path, 'enum', value)
            return {'field': base, 'operator': 'eq', 'value': value}
        if leaf == 'selector':
            sel = r.filters[-1] if r.filters else 'Selected'
            if any(r.xml_path == s['xml'] for s in scopes):
                self.register(r.xml_path + '/' + sel, 'bool')
                return {'field': liquid(r.xml_path + '/' + sel, scopes), 'operator': 'eq', 'value': 'true'}
            segs = r.xml_path.split('/')
            if len(segs) >= 2 and segs[-2] == 'Items':
                flag = '/'.join(segs[:-2] + ['Any' + ident(segs[-1]) + ident(sel)])
            else:
                flag = r.xml_path + '/Any' + ident(sel)
            self.notes.append(f'"{desc}" needs a derived flag {flag.replace("/", ".")} (any item matching selector {sel})')
            self.register(flag, 'bool')
            return {'field': liquid(flag, scopes), 'operator': 'eq', 'value': 'true'}
        if leaf in ('test', 'derived-test', 'mutexTestGroup'):
            if leaf == 'derived-test':
                self.notes.append(f'derived test "{desc}" -> {base}: GhostDraft computes it; the message must supply it')
            elif leaf == 'test':
                self.bool_tests.add(base)
            self.register(r.xml_path, 'bool')
            return {'field': base, 'operator': 'eq', 'value': 'true'}
        self.register(r.xml_path, 'list' if leaf in ('list', 'list-item') else 'present')
        return {'field': base, 'operator': 'present', 'value': ''}

    def cond_op(self, ins, scopes, fills, ops):
        parts = self.parts(ins)
        conds = []
        for p in parts:
            self.walk(child(p, 'instructions'), scopes, fills, ops)
            if p.get(XSI) != 'endPartType':
                conds.append(self.condition(p, scopes))
        ids = [p.get('ID', '') for p in parts]
        self.counts['conditionals'] += 1
        # GhostDraft leaves out BOTH branches of "if <test> ... otherwise ..." when the message has no value for the
        # test (DA 40 93: no ORPolicy -> neither "Full Cov/ No" nor "N/A"), so the otherwise needs the test false
        if len(conds) == 2 and conds[1] is None and conds[0] and conds[0]['field'] in self.bool_tests and \
                conds[0]['operator'] in ('eq', 'ne') and conds[0]['value'] == 'true':
            first = conds[0]
            conds = [dict(first, operator='eq', value='true' if first['operator'] == 'eq' else 'false'),
                     dict(first, operator='eq', value='false' if first['operator'] == 'eq' else 'true')]
        ops.append(('cond', ids, conds))

    def list_op(self, ins, desc, scopes, fills, ops):
        pe = child(ins, 'pathToList')
        root_name = text_of(pe, 'rootNode')
        root_guid = text_of(pe, 'rootguid')
        pn = child(pe, 'pathNodes')
        nodes = [(n.get('name', ''), n.get('guid', '')) for n in (pn if pn is not None else []) if local(n.tag) == 'node']
        scope = {s['guid']: (s['xml'], s['type'], '') for s in scopes if s.get('guid')}
        r = self.model.resolve_list(root_guid, root_name, nodes, scope)
        self.counts['lists'] += 1
        if not r.ok:
            self.counts['unresolved'] += 1
            self.notes.append(f'list "{desc}" unresolved: {r.reason}')
            xml = 'unresolved/Items/' + ident(text_of(ins, 'iterator') or 'Item')
        else:
            xml = r.xml_path
        segs = xml.split('/')
        collection = liquid(xml, scopes)
        alias = designer_singular(plural(segs[-1]))
        self.register(xml, 'list')
        inner = scopes + [{'xml': xml, 'alias': alias, 'guid': text_of(ins, 'iteratorGuid'), 'type': r.type_id or ''}]
        filt = None
        if r.ok and r.filters:
            sel = r.filters[-1]
            if '==' not in sel:
                self.register(xml + '/' + sel, 'bool')
                filt = {'field': alias + '.' + ident(sel), 'operator': 'eq', 'value': 'true'}
        parts = self.parts(ins)
        for p in parts:
            self.walk(child(p, 'instructions'), inner, fills, ops)
        ids = [p.get('ID', '') for p in parts]
        ops.append(('list', ids, {'collection': collection, 'alias': alias, 'filter': filt}))

    # ------------------------------------------------------------------------ tree surgery
    def replace_fills(self, node, fills):
        for i, k in enumerate(node.get('kids', [])):
            if k['k'] == 'marker' and k['id'] in fills:
                rep = fills[k['id']]
                if rep is not None and rep.get('k') == 'fill' and k.get('c'):
                    rep = dict(rep, c=k['c'])
                node['kids'][i] = rep if rep is not None else {'k': 'run', 't': '', 'c': None}
            elif 'kids' in k:
                self.replace_fills(k, fills)

    def leftover(self, node, acc=None):
        acc = [] if acc is None else acc
        for k in node.get('kids', []):
            if k['k'] == 'marker':
                acc.append(k['id'])
            elif 'kids' in k:
                self.leftover(k, acc)
        return acc

    def apply(self, roots, op):
        kind, ids, data = op
        root = next((r for r in roots if find_marker(r, ids[0])), None)
        if root is None or not find_marker(root, ids[-1]):
            self.notes.append(f'{kind} {ids}: markers not found together; content kept unconditionally')
            for r in roots:
                for mid in ids:
                    remove_marker(r, mid)
            return
        ps, pe = find_marker(root, ids[0]), find_marker(root, ids[-1])
        self.pull_into_table(root, ps, ids)
        ps, pe = find_marker(root, ids[0]), find_marker(root, ids[-1])
        c = lca(ps, pe)
        inline = c['k'] == 'para' or (c['k'] == 'region' and c.get('inline'))
        # A marker that ends its paragraph leaves that paragraph's mark inside the following segment. When the
        # segment holds nothing else (e.g. "%[101]\par %[102]" = a blank line between list items), the mark is
        # the content: an empty line.
        tail_para = {}
        if not inline:
            for mid in ids[:-1]:
                parent, mi = find_marker(root, mid)[-1]
                if parent['k'] == 'para' and is_empty_inline(parent['kids'][mi + 1:]):
                    tail_para[mid] = dict(parent, kids=[])
        for mid in ids:
            lift(root, mid, c, self.notes)
        pos = [next((i for i, k in enumerate(c['kids']) if k['k'] == 'marker' and k['id'] == mid), None) for mid in ids]
        if None in pos or pos != sorted(pos):
            self.notes.append(f'{kind} {ids}: markers out of order after lifting; content kept unconditionally')
            for mid in ids:
                remove_marker(root, mid)
            return
        segments = [c['kids'][pos[i] + 1:pos[i + 1]] or ([tail_para[ids[i]]] if ids[i] in tail_para else [])
                    for i in range(len(pos) - 1)]
        level = 'inline' if inline else 'rows' if c['k'] == 'table' else 'cells' if c['k'] == 'row' else 'block'
        if level == 'cells':
            self.notes.append(f'{kind} {ids} spans table cells; condition dropped, content kept')
            c['kids'][pos[0]:pos[-1] + 1] = [k for seg in segments for k in seg]
            return
        if kind == 'list':
            only = [k for k in segments[0] if has_content(k) or k['k'] == 'shape']
            if len(only) == 1 and only[0]['k'] == 'shape':
                # A list around one positioned text box repeats the box's CONTENT; repeating the box stacks copies
                # at the same position.
                shape = only[0]
                shape['kids'] = [{'k': 'region', 'kind': 'list', 'level': 'block', 'inline': False,
                                  'kids': shape['kids'], **data}]
                c['kids'][pos[0]:pos[-1] + 1] = segments[0]
                return
            region = {'k': 'region', 'kind': 'list', 'level': level, 'inline': inline, 'kids': segments[0], **data}
        else:
            conds = data
            if len(segments) == 1:
                region = {'k': 'region', 'kind': 'if', 'level': level, 'inline': inline, 'kids': segments[0],
                          'cond': conds[0]}
            else:
                branches = [{'k': 'region', 'kind': 'branch', 'level': level, 'inline': inline, 'kids': seg,
                             'cond': cond} for seg, cond in zip(segments, conds)]
                region = {'k': 'region', 'kind': 'choice', 'level': level, 'inline': inline, 'kids': branches}
        c['kids'][pos[0]:pos[-1] + 1] = [region]

    def pull_into_table(self, root, ps, ids):
        """A region that starts on a table row and ends in the paragraph right after the table (EA 99 05: the
        choice/list end markers follow the table: "...\\row }%[19]%[15]\\par") covers rows to the table's end.
        Move the leading end markers of that paragraph into the table, after its last row."""
        tables = [n for n, _ in ps if n['k'] == 'table']
        if not tables:
            return
        for mid in ids[1:]:
            pe = find_marker(root, mid)
            if not pe or any(n is tables[-1] for n, _ in pe):
                continue
            para, i = pe[-1]
            if para['k'] != 'para' or len(pe) < 2 or any(k['k'] != 'marker' for k in para['kids'][:i]):
                continue
            gp, pi = pe[-2]
            if pi == 0 or gp['kids'][pi - 1] is not tables[-1]:
                continue
            tables[-1]['kids'].extend(para['kids'][:i + 1])
            del para['kids'][:i + 1]

    def expand(self, node, stack, sinks):
        """Replace subscription nodes with the subscribed template's content."""
        out = []
        for k in node.get('kids', []):
            if k['k'] == 'para' and any(x['k'] == 'sub' for x in k['kids']):
                out.extend(self.expand_para(k, stack, sinks))
                continue
            if k['k'] == 'sub':   # inside an inline region
                out.extend(self.sub_inlines(k, stack, sinks))
                continue
            if 'kids' in k:
                k = dict(k, kids=self.expand(k, stack, sinks)['kids'])
            out.append(k)
        return dict(node, kids=out)

    def sub_tree(self, name, stack, sinks):
        t = self.template_tree(name, stack)
        if t is None:
            self.notes.append(f'subscribed template "{name}" not in package; left as a note')
            self.counts['subscriptions missing'] += 1
            return None
        self.counts['subscriptions expanded'] += 1
        self._sub_has_hf = any(s and s['kids'] for s in t['sinks'].values())
        self._sub_sbk = (t.get('page') or {}).get('sbk')
        self._sub_sinks = t['sinks']
        for key, s in t['sinks'].items():
            if key not in sinks or not sinks[key]['kids']:
                sinks[key] = s
        return t['doc']

    def sub_inlines(self, sub, stack, sinks):
        doc = self.sub_tree(sub['name'], stack, sinks)
        if doc is None:
            return [{'k': 'run', 't': f'[{sub["name"]}]', 'c': None}]
        paras = [b for b in doc['kids'] if b['k'] == 'para']
        if len(paras) == len(doc['kids']):
            out = []
            for i, p in enumerate(paras):
                if i:
                    out.append({'k': 'br'})
                out.extend(p['kids'])
            return out
        self.notes.append(f'subscription "{sub["name"]}" is block content inside a line; flattened to text')
        return [{'k': 'run', 't': ' '.join(flatten_text(doc)), 'c': None}]

    def expand_para(self, para, stack, sinks):
        out, cur, block = [], [], False
        for x in para['kids']:
            if x['k'] != 'sub':
                cur.append(x)
                continue
            doc = self.sub_tree(x['name'], stack, sinks)
            if doc is None:
                cur.append({'k': 'run', 't': f'[{x["name"]}]', 'c': None})
                continue
            if getattr(self, '_sub_has_hf', False) and not has_content(doc):
                if self._sub_sbk == 'sbknone':
                    # a continuous section (EA 99 41's header): the headers change, the page does not
                    continue
                # a header/footer subscription starts a new section: GhostDraft breaks the page there (unless the
                # page is still empty) and the rest of the paragraph prints as the first line of the next page
                if cur:
                    out.append(dict(para, kids=cur))
                # the new section prints the subscribed template's headers/footers
                out.append({'k': 'secbreak', 'sinks': self._sub_sinks})
                cur = []
                continue
            if len(doc['kids']) == 1 and doc['kids'][0]['k'] == 'para':
                cur.extend(doc['kids'][0]['kids'])
                continue
            if cur and not is_empty_inline(cur):
                out.append(dict(para, kids=cur))
            cur, block = [], True
            out.extend(doc['kids'])
        # a paragraph that only held line-level subscriptions (e.g. a header/footer subscription) still prints as a
        # line in GhostDraft even when empty; only the empty remainder around block content is dropped
        if not block or (cur and not is_empty_inline(cur)):
            out.append(dict(para, kids=cur))
        return [self.expand(b, stack, sinks) if 'kids' in b else b for b in out]


def outline(node, depth=0, out=None):
    """Debug: compact text outline of a block tree."""
    out = [] if out is None else out
    for k in node.get('kids', []):
        kind = k['k']
        pad = '  ' * depth
        if kind == 'para':
            txt = ''.join(x['t'] if x['k'] == 'run' else f'<{x["k"]}>' for x in k['kids'] if x['k'] != 'shape')[:70]
            out.append(f'{pad}para: {txt!r}')
            outline(k, depth + 1, out)
            continue
        if kind == 'region':
            out.append(f'{pad}region {k["kind"]} {k.get("level")} {k.get("cond") or k.get("collection") or ""}')
        elif kind == 'shape':
            out.append(f'{pad}shape rel={k["relh"]}/{k["relv"]} box={k["box"]}')
        elif kind in ('run', 'fill', 'sub', 'marker', 'tab', 'br', 'img', 'field'):
            continue
        else:
            out.append(f'{pad}{kind}')
        outline(k, depth + 1, out)
    return out


def flatten_text(node):
    out = []
    for k in node.get('kids', []):
        if k['k'] == 'run':
            out.append(k['t'])
        elif 'kids' in k:
            out.extend(flatten_text(k))
    return out


# =====================================================================================================
# Block tree -> GrapesJS components + CSS
# =====================================================================================================

def pt(twips):
    return f'{twips / 20:g}pt'


class Emitter:
    def __init__(self, notes, counts, title='', props=None):
        self.notes = notes
        self.counts = counts
        self.title = title
        self.props = props or {}
        self.classes: dict[str, str] = {}   # css body -> class name
        self.base = None
        self.page = None

    def cls(self, prefix, decls):
        body = ';'.join(d for d in decls if d)
        if not body:
            return None
        name = self.classes.get(body)
        if name is None:
            name = f'{prefix}{len(self.classes) + 1}'
            self.classes[body] = name
        return name

    def css(self):
        base_font, base_size = self.base
        pg = self.page or {'paperw': 12240, 'paperh': 15840}
        margt, margr, margb, margl = (pt(pg.get(k, 1440)) for k in ('margt', 'margr', 'margb', 'margl'))
        hy, fy = pt(pg.get('headery', 720)), pt(pg.get('footery', 720))
        band_t = pt(max(pg.get('margt', 1440) - pg.get('headery', 720), 0))
        band_b = pt(max(pg.get('margb', 1440) - pg.get('footery', 720), 0))
        rules = [
            f'.form-page{{position:relative;width:{pt(pg["paperw"])};height:{pt(pg["paperh"])};overflow:hidden;'
            'background:#fff;}',
            '.form-page + .form-page{page-break-before:always;}',
            # Word lets tables run into the side margins; if that overflowed the @page content box Chrome would
            # shrink-to-fit the WHOLE PDF (CA 20 16: 540pt table in a 504pt column => every page scaled 0.94).
            # So the side margins are padding on the flow, leaving room to overflow within the sheet.
            f'@page gdflow{{margin:{margt} 0 {margb} 0;}}',
            f'.gd-flow{{page:gdflow;padding:0 {margr} 0 {margl};}}',
            # running header/footer: Chrome repeats thead/tfoot on every printed page. The page margin is Word's
            # header/footer distance and the band keeps the body at the body margin (or below a taller header).
            f'@page gdrun{{margin:{hy} 0 {fy} 0;}}',
            '.gd-flow.gd-run{page:gdrun;}',
            '.gd-doc table.gd-runt{width:100%;border-collapse:collapse;table-layout:fixed;}',
            '.gd-doc table.gd-runt>*>tr>td{padding:0;}',
            f'.gd-doc table.gd-runt>thead>tr>td{{height:{band_t};vertical-align:top;}}',
            f'.gd-doc table.gd-runt>tfoot>tr>td{{height:{band_b};vertical-align:bottom;position:relative;}}',
            '.gd-pdffoot-even{position:absolute;visibility:hidden;left:0;right:0;bottom:0;}',
            '.gd-doc table.gd-runt>thead>tr>td{position:relative;}',
            '.gd-pgmark{position:absolute;left:0;top:0;font-size:2pt;line-height:1;color:#fff;white-space:nowrap;}',
            # a sheet's section footer covers the footer printed in the sheet's own picture (GhostDraft shows only it)
            '.gd-sheetfoot{position:absolute;background:#fff;}',
            '.gd-sheetfoot.gd-side-even{display:none;}',
            # Chrome prints the last page's tfoot right after the body, not at the page bottom; a fixed element
            # repeats on every page at the bottom of the page area (= Word's footer distance). The tfoot copy only
            # reserves the footer's height in print, and is the one seen/edited on screen.
            '.gd-foot-fixed{display:none;}',
            f'@media print{{.gd-foot-space{{visibility:hidden;}}.gd-foot-fixed{{display:block;position:fixed;left:0;'
            f'right:0;bottom:0;padding:0 {margr} 0 {margl};}}}}',
            # a flow page whose only content is a Show If that printed nothing must not start a page
            '.gd-if:empty{display:none;}',
            '.gd-flow:not(:has(:not(.gd-if))){display:none;}',
            # the page break between two flows sits on the default (unnamed) page: leaving the flow's named page
            # breaks before it, then it breaks after itself -- a blank sheet (DA 00 93 page 2). The next flow breaks.
            '.gd-flow + .page-break:has(+ .gd-flow){display:none;}',
            '.gd-flow + .page-break + .gd-flow{break-before:page;}',
            # likewise a form sheet whose shapes all sit in Show Ifs that printed nothing (the other branch's sheet)
            '.form-page:not(:has(:not(.gd-if))){display:none;}',
            '.form-page .abs{position:absolute;white-space:pre;margin:0;padding:0;line-height:normal;}',
            '.form-page .rule{position:absolute;background:#000;}',
            '.form-page .gd-tb{white-space:normal;box-sizing:border-box;}',
            '.gd-doc .gd-pagebg{position:absolute;width:0;height:0;}',
            '.gd-doc .gd-anchor{position:relative;}',
            '.gd-doc .gd-anchored{position:absolute;box-sizing:border-box;}',
            '.gd-doc .gd-header{position:relative;}',
            '.gd-pagebg .abs{position:absolute;white-space:pre;margin:0;padding:0;line-height:normal;}',
            '.gd-pagebg .rule{position:absolute;background:#000;}',
            '.gd-pagebg .gd-tb{white-space:normal;box-sizing:border-box;}',
            f'.gd-doc{{font-family:{font_css(base_font)};font-size:{base_size / 2:g}pt;color:#000;line-height:1.15;}}',
            # element defaults use :where() (no specificity): the designer stores the per-cell/paragraph rules as
            # bare .gd-tdN/.gd-pN selectors, which '.gd-doc td' would otherwise beat (valign, padding, spacing lost)
            ':where(.gd-doc) p{margin:0;}',
            '.gd-doc p:empty::before{content:"\\00a0";}',
            '.gd-doc p.gd-keepline{min-height:1lh;}',
            '.gd-doc .gd-table{border-collapse:collapse;table-layout:fixed;}',
            ':where(.gd-doc) td{vertical-align:top;padding:0 5.4pt;}',
            '.gd-doc tr.gd-grid td{height:0;padding:0;border:0;}',
            '.gd-doc .gd-box{margin:0 0 6pt 0;}',
            '.gd-doc .gd-tab{display:inline-block;width:36pt;}',
            '.gd-doc .gd-hang{display:inline-block;}',
            '.gd-doc .gd-img-missing{display:inline-block;border:1px dashed #9ca3af;color:#6b7280;font-size:7pt;}',
        ]
        for body, name in self.classes.items():
            rules.append(f'.gd-doc .{name}{{{body};}}')
        return '\n'.join(rules)

    # ------------------------------------------------------------------ styles
    def pick_base(self, trees):
        c = Counter()

        def walk(n):
            for k in n.get('kids', []):
                if k['k'] == 'run' and k['c'] and k['t'].strip():
                    c[(k['c'][6], k['c'][5])] += len(k['t'])
                elif 'kids' in k:
                    walk(k)
        for t in trees:
            walk(t)
        self.base = c.most_common(1)[0][0] if c else ('Arial', 20)

    def run_class(self, key, text=None):
        if not key:
            return None
        b, i, ul, strike, caps, fs, font, color, va, *rest = key
        scale, sp = rest if len(rest) == 2 else (100, 0)
        bf, bs = self.base
        deco = ' '.join(x for x in ('underline' if ul else '', 'line-through' if strike else '') if x)
        # Word's character spacing (\expndtw, twips) is letter-spacing; its horizontal scale (\charscalex) stretches
        # the glyphs, which CSS can't do to wrapping text -- the extra advance is spread as letter-spacing instead
        # (DA 00 93's company line at 105%, Named Insured at 110%), so the words land where GhostDraft puts them
        spacing = sp / 20
        if scale != 100:
            sample = text if text else 'Abcdefghijklmnopqrstuvwxyz 0123456789'
            spacing += (scale / 100 - 1) * measure(sample, fs / 2, b, i, font or bf) / len(sample)
        return self.cls('gd-c', [
            'font-weight:bold' if b else '',
            'font-style:italic' if i else '',
            f'text-decoration:{deco}' if deco else '',
            'text-transform:uppercase' if caps else '',
            f'font-size:{fs / 2:g}pt' if fs != bs else '',
            f'font-family:{font_css(font)}' if font and font != bf else '',
            f'color:{color}' if color and color.lower() not in ('#000000',) else '',
            f'vertical-align:{va};font-size:smaller' if va else '',
            f'letter-spacing:{spacing:.2f}pt' if abs(spacing) >= 0.005 else '',
        ])

    def para_class(self, p):
        line = ''
        if p['sl'] and p['slmult']:
            line = f'line-height:{1.15 * p["sl"] / 240:.3g}'
        elif p['sl'] > 0:
            line = f'line-height:{pt(p["sl"])}'
        elif p['sl'] < 0:
            line = f'line-height:{pt(-p["sl"])}'
        return self.cls('gd-p', [
            {'c': 'text-align:center', 'r': 'text-align:right', 'j': 'text-align:justify'}.get(p['align'], ''),
            f'margin-left:{pt(p["li"])}' if p['li'] else '',
            f'margin-right:{pt(p["ri"])}' if p['ri'] else '',
            f'text-indent:{pt(p["fi"])}' if p['fi'] else '',
            f'margin-top:{pt(p["sb"])}' if p['sb'] else '',
            f'margin-bottom:{pt(p["sa"])}' if p['sa'] else '',
            line,
            'break-after:avoid' if p['keepn'] else '',
            'break-before:column' if p.get('colbreak') else '',
        ])

    # ------------------------------------------------------------------ blocks
    def blocks(self, kids):
        out = []
        cols = None     # open multi-column section: [section node, blocks, blocks per column break]
        for b in kids:
            if b['k'] in ('pagebreak', 'colsect', 'secbreak'):
                if cols:
                    out.append(self.columns(*cols))
                    cols = None
                out.extend(self.block(b))
                if b['k'] != 'secbreak' and (b.get('cols', 1) > 1 or self.sect_shift(b)):
                    cols = [b, [], [[]]]
                continue
            if cols and b['k'] == 'para' and b['p'].get('colbreak') and cols[2][-1]:
                cols[2].append([])
            comps = self.block(b)
            (cols[1] if cols else out).extend(comps)
            if cols:
                cols[2][-1].extend(comps)
        if cols:
            out.append(self.columns(*cols))
        return out

    def sect_shift(self, sect):
        """A continuous section's own side margins relative to the page's, in twips (left, right), or None."""
        pg = self.page or {}
        dl = sect.get('margl', pg.get('margl', 1440)) - pg.get('margl', 1440)
        dr = sect.get('margr', pg.get('margr', 1440)) - pg.get('margr', 1440)
        return (dl, dr) if dl or dr else None

    def columns(self, sect, comps, parts=None):
        shift = self.sect_shift(sect)
        out = self.section_columns(sect, comps, parts) if sect.get('cols', 1) > 1 else \
            {'tagName': 'div', 'components': comps}
        if shift:
            out['classes'] = out.get('classes', []) + [
                self.cls('gd-sm', [f'margin-left:{pt(shift[0])}', f'margin-right:{pt(shift[1])}'])]
        return out

    def section_columns(self, sect, comps, parts=None):
        # Word section columns (\cols2\colsx720): newspaper columns, balanced at the end of a continuous section
        widths = sect.get('colw') or []
        if parts and len(parts) == sect['cols'] and len(widths) == sect['cols'] and len(set(widths)) > 1:
            # columns of their own widths (\colw), filled up to a column break: a grid lays them out exactly
            # (DA 00 93's symbol list: 252.45pt + 263.8pt; CSS columns are equal, the right one 5.7pt off)
            gap = (sect.get('colsr') or [sect.get('gap', 720)])[0]
            cls = self.cls('gd-cols', ['display:grid', 'grid-template-columns:' + ' '.join(pt(w) for w in widths),
                                       f'column-gap:{pt(gap)}', 'align-items:start'])
            return {'tagName': 'div', 'classes': [cls],
                    'components': [{'tagName': 'div', 'components': p} for p in parts]}
        cls = self.cls('gd-cols', [f'column-count:{sect["cols"]}', f'column-gap:{pt(sect.get("gap", 720))}',
                                   'column-fill:balance'])
        return {'tagName': 'div', 'classes': [cls], 'components': comps}

    def block(self, b):
        k = b['k']
        if k == 'para':
            return [self.para(b)]
        if k == 'table':
            return [self.table(b)]
        if k == 'shape':
            return [{'tagName': 'div', 'classes': ['gd-box'], 'components': self.blocks(b['kids'])}]
        if k == 'region':
            return [self.region(b, 'div', self.blocks)]
        if k == 'pagebreak':
            return [{'type': 'page-break'}]
        if k == 'secbreak':
            return []
        if k == 'raw':
            return b['comps']
        if k in ('run', 'fill', 'tab', 'br', 'img', 'field'):
            return [{'type': 'text', 'tagName': 'p', 'components': self.inlines([b])}]
        if k == 'marker':
            return []
        if 'kids' in b:
            return self.blocks(b['kids'])
        return []

    def para(self, p):
        self.counts['paragraphs'] += 1
        kids = [x for x in p['kids'] if x['k'] != 'shape']
        boxes = [x for x in p['kids'] if x['k'] == 'shape']
        comps = []
        if p['p']['fi'] < 0 and any(x['k'] == 'tab' for x in kids):
            first = next(i for i, x in enumerate(kids) if x['k'] == 'tab')
            hang = self.cls('gd-h', [f'min-width:{pt(-p["p"]["fi"])}'])
            comps.append({'tagName': 'span', 'classes': ['gd-hang', hang], 'components': self.inlines(kids[:first])})
            kids = kids[first + 1:]
        comps.extend(self.inlines(kids))
        classes = ['gd-p'] + [c for c in [self.para_class(p['p'])] if c]
        sizes = run_sizes(kids)
        if comps and sizes and None not in sizes and self.base[1] not in sizes and min(sizes) < self.base[1]:
            # Word sizes a line by the text on it; the CSS line box also holds a strut of the paragraph's own font,
            # so a 7.5pt paragraph in an 8pt document gets 8pt lines (DA 40 93's intro: 12pt pitch, GhostDraft 11.5)
            classes.append(self.cls('gd-m', [f'font-size:{min(sizes) / 2:g}pt']))
        if not comps and p['p'].get('mark_fs') and p['p']['mark_fs'] != self.base[1]:
            # an empty paragraph is one line of its paragraph mark's size (DA 00 93: the 10pt blank lines between
            # ITEM ONE's rows in an 8pt document)
            classes.append(self.cls('gd-m', [f'font-size:{p["p"]["mark_fs"] / 2:g}pt']))
        if any(c.get('type') in ('conditional', 'choice') for c in comps):
            # a Show If inside the paragraph leaves its paragraph mark behind: the line stays when it is hidden
            classes.append('gd-keepline')
        para = {'type': 'text', 'tagName': 'p', 'classes': classes, 'components': comps}
        if not boxes:
            return para
        return {'tagName': 'div', 'classes': ['gd-anchor'], 'components': ([para] if kids else []) + [
            self.anchored_box(b) for b in boxes]}

    def anchored_box(self, b):
        """A paragraph-anchored text box: Word places it at its offset from the anchor paragraph (DA 01 93's
        liability and physical damage rows, 210pt below the list they follow), not in the text flow."""
        if b['relv'] != 'para':
            return {'tagName': 'div', 'classes': ['gd-box'], 'components': self.blocks(b['kids'])}
        left, top, right, _ = b['box']
        if b['relh'] == 'page':
            margl = (self.page or {}).get('margl', 1440)
            left, right = left - margl, right - margl
        sp = b['sp']

        def emu(name, default):
            try:
                return f'{int(sp.get(name, default)) / 12700:g}pt'
            except ValueError:
                return '0pt'
        style = {'left': pt(left), 'top': pt(top), 'width': pt(max(right - left, 0)),
                 'padding': ' '.join(emu(n, d) for n, d in (('dyTextTop', 45720), ('dxTextRight', 91440),
                                                          ('dyTextBottom', 45720), ('dxTextLeft', 91440)))}
        if sp.get('fLine') == '1':
            style['border'] = '0.75pt solid #000'
        self.counts['anchored text boxes'] += 1
        return {'tagName': 'div', 'classes': ['gd-anchored'], 'style': style, 'components': self.blocks(b['kids'])}

    def inlines(self, kids):
        out = []
        for x in kids:
            k = x['k']
            if k == 'run':
                if not x['t']:
                    continue
                t = x['t'].replace('{{', '{\u200b{').replace('{%', '{\u200b%')
                c = self.run_class(x['c'], x['t'])
                node = {'type': 'textnode', 'content': t}
                out.append({'tagName': 'span', 'classes': [c], 'components': [node]} if c else node)
            elif k == 'fill':
                field = {'type': 'data-field', 'field': x['field'], 'format': x['format']}
                c = self.run_class(x['c']) if x.get('c') else None
                out.append({'tagName': 'span', 'classes': [c], 'components': [field]} if c else field)
            elif k == 'tab':
                out.append({'tagName': 'span', 'classes': ['gd-tab']})
            elif k == 'br':
                out.append({'tagName': 'br'})
            elif k == 'field':
                if x['name'] == 'PROP':
                    val = self.title if x['prop'] == 'TITLE' else self.props.get(x['prop'], '')
                    if val:
                        c = self.run_class(x['c']) if x.get('c') else None
                        text = {'type': 'textnode', 'content': val}
                        out.append({'tagName': 'span', 'classes': [c], 'components': [text]} if c else text)
                    continue
                self.counts['page-number fields'] += 1
                c = self.run_class(x['c']) if x.get('c') else None
                out.append({'tagName': 'span', 'classes': [c for c in (c,) if c] + [
                    'gd-pageno' if x['name'] == 'PAGE' else 'gd-pagecount'],
                    'components': [{'type': 'textnode', 'content': '#'}]})
            elif k == 'img':
                if x.get('src'):
                    out.append({'type': 'image', 'src': x['src'],
                                'attributes': {'style': f'width:{x["w"]:g}pt'} if x.get('w') else {}})
                else:
                    w, h = x.get('w') or 36, x.get('h') or 18
                    out.append({'tagName': 'span', 'classes': ['gd-img-missing'],
                                'attributes': {'style': f'width:{w:g}pt;height:{h:g}pt'},
                                'components': [{'type': 'textnode', 'content': 'image'}]})
            elif k == 'region':
                out.append(self.region(x, 'span', self.inlines))
            elif k == 'sub':
                out.append({'type': 'textnode', 'content': f'[{x["name"]}]'})
            elif k in ('para',):
                out.extend(self.inlines(x['kids']))
        return out

    # ------------------------------------------------------------------ regions
    def region(self, r, tag, emit):
        kind = r['kind']
        if kind == 'if':
            self.counts['show if'] += 1
            return dict({'type': 'conditional', 'tagName': tag, 'components': emit(r['kids'])}, **r['cond'])
        if kind == 'choice':
            branches = []
            for b in r['kids']:
                cond = b['cond'] or {'field': '', 'operator': 'else', 'value': ''}
                branches.append(dict({'type': 'choice-branch', 'tagName': tag, 'components': emit(b['kids'])}, **cond))
            fallback = self.wording_fallback(branches, tag) if getattr(self, 'collapse_wording', False) else None
            if fallback:
                self.counts['wording fallbacks'] += 1
                return fallback
            self.counts['choose'] += 1
            return {'type': 'choice', 'tagName': tag, 'components': branches}
        if kind == 'list':
            self.counts['repeat'] += 1
            body = emit(r['kids'])
            if r.get('filter'):
                body = [dict({'type': 'conditional', 'tagName': tag, 'components': body}, **r['filter'])]
            return {'type': 'repeat', 'tagName': tag, 'listPath': r['collection'], 'components': body}
        return {'tagName': tag, 'components': emit(r['kids'])}

    @staticmethod
    def wording_fallback(branches, tag):
        """The ISO concept library pairs values with a "(Wording)" text attribute, and the templates print it when
        the value is missing: Choose [X is provided: {X}] [otherwise: {X (Wording)}]. The message never carries
        the wording, so the pair becomes one field whose "If empty, show" text (blank) the author can fill in.
        When the test is a separate IsProvided flag, or the value branch has more than the field (a "$" before it),
        the value branch becomes a Show If; a value branch with nothing in it prints nothing either way."""
        if len(branches) != 2 or branches[1].get('operator') != 'else':
            return None

        def content(b):
            return [c for c in b['components'] if not (c.get('type') == 'textnode' and not c.get('content', '').strip())]
        rest = content(branches[1])
        if len(rest) != 1 or rest[0].get('type') != 'data-field' or not rest[0].get('field', '').endswith('Wording'):
            return None
        base = rest[0]['field'][:-len('Wording')]
        test = branches[0]
        tfield = test.get('field', '')
        if not (test.get('operator') == 'present' and tfield == base) and not (
                test.get('operator') == 'eq' and test.get('value') == 'true' and tfield.endswith('.IsProvided')
                and tfield[:-len('.IsProvided')] in (base, base.rsplit('.', 1)[0])):
            return None
        comps = content(test)
        if not comps:
            return {'type': 'textnode', 'content': ''}
        if len(comps) == 1 and comps[0].get('type') == 'data-field' and test.get('operator') == 'present' \
                and comps[0].get('field') == tfield:
            return dict(comps[0], ifEmpty='')
        return {'type': 'conditional', 'tagName': tag, 'components': test['components'],
                'field': tfield, 'operator': test['operator'], 'value': test.get('value', '')}

    # ------------------------------------------------------------------ tables
    def table(self, t):
        self.counts['tables'] += 1
        saved_cols = getattr(self, '_cols', None)
        saved_pct = getattr(self, '_pct', None)
        self._pct = self.pct_scale(self.flat_rows_quiet(t['kids']))
        self._cols = self.column_grid(self.flat_rows_quiet(t['kids']))
        groups, plain = [], []
        width = 0
        for k in t['kids']:
            if k['k'] == 'row':
                plain.append(k)
                width = max(width, self.row_width(k))
                continue
            if plain:
                groups.append({'type': 'tbody', 'components': self.rows(plain)})
                plain = []
            groups.extend(self.row_region(k))
        if plain:
            groups.append({'type': 'tbody', 'components': self.rows(plain)})
        if self._cols:
            # GrapesJS tables keep only tbody children, so the column grid is a zero-height first row that fixes
            # the widths under table-layout:fixed
            cells = [{'type': 'cell', 'classes': [self.cls('gd-col', [f'width:{self.tw(b - a)}'])], 'components': []}
                     for a, b in zip(self._cols, self._cols[1:])]
            groups.insert(0, {'type': 'tbody', 'components': [{'type': 'row', 'classes': ['gd-grid'], 'components': cells}]})
        self._cols = saved_cols
        first = next((k for k in t['kids'] if k['k'] == 'row'), None)
        left = first['def']['left'] if first else 0
        # Word "keep with next" on every row but the last keeps the table on one page (CA 20 16: one schedule
        # table per auto, each moved whole to the next page); Chrome only honors that as break-inside on the table
        rows = self.flat_rows_quiet(t['kids'])
        keep = len(rows) > 1 and all(
            any(b['k'] == 'para' and b['p'].get('keepn') for c in r['kids'] for b in c.get('kids', []))
            for r in rows[:-1])
        tcls = self.cls('gd-t', [self.table_width(first, width) if width else '',
                                 f'margin-left:{self.tw(left)}' if left else '',
                                 'break-inside:avoid' if keep else ''])
        self._pct = saved_pct
        return {'type': 'table', 'classes': ['gd-table'] + ([tcls] if tcls else []), 'components': groups}

    def table_width(self, first, width):
        """A percent-width table (\\trftsWidth2) is that share of the text width PLUS its rows' left and right cell
        padding, as in Word: DA 00 93's 100% tables are 550.8pt wide in a 540pt column, their cell text lining up
        with the margin on the left and the table running 10.8pt into the right margin."""
        w = self.tw(width)
        if first and first['def'].get('fts') == 2 and getattr(self, '_pct', None):
            pad = first['def']['pad'].get('l', 0) + first['def']['pad'].get('r', 0)
            if pad:
                return f'width:calc({w} + {pt(pad)})'
        return f'width:{w}'

    PCT_BASE = 10000

    def pct_scale(self, rows):
        """GhostDraft percent-width tables (\\trftsWidth2, \\trwWidth in 50ths of a percent) write \\cellx as
        relative weights, not twips; rescale them onto a nominal PCT_BASE width so widths can be emitted as %."""
        if not rows or rows[0]['def'].get('fts') != 2:
            return None
        for row in rows:
            d = row['def']
            if d.get('scaled') or not d['cells']:
                continue
            span = d['cells'][-1]['x'] - d['left']
            if span <= 0:
                continue
            f = self.PCT_BASE * (d.get('ww') or 5000) / 5000 / span
            d['left'] = round(d['left'] * f)
            for cd in d['cells']:
                cd['x'] = round(cd['x'] * f)
            d['scaled'] = True
        return self.PCT_BASE

    def tw(self, w):
        return f'{w / self._pct * 100:.3f}%' if getattr(self, '_pct', None) else pt(w)

    def row_width(self, row):
        cells = row['def']['cells']
        return (cells[-1]['x'] - row['def']['left']) if cells else 0

    def flat_rows_quiet(self, kids):
        rows = []
        for k in kids:
            if k['k'] == 'row':
                rows.append(k)
            elif k['k'] == 'region':
                rows.extend(self.flat_rows_quiet(k['kids']))
        return rows

    @staticmethod
    def column_grid(rows):
        """Word rows each carry their own cell boundaries; HTML columns are shared. When the rows disagree, return
        the union of boundaries (within 30 twips) so cells can span columns; None when every row agrees."""
        edges = []
        for row in rows:
            xs = [row['def']['left']] + [cd['x'] for cd in row['def']['cells']]
            edges.append(xs)
        if len({tuple(e) for e in edges}) <= 1:
            return None
        grid = []
        for x in sorted(x for e in edges for x in e):
            if not grid or x - grid[-1] > 30:
                grid.append(x)
        return grid

    def colspan(self, a, b):
        return max(1, sum(1 for x in self._cols[1:] if a + 30 < x <= b + 30))

    def flat_rows(self, kids):
        rows = []
        for k in kids:
            if k['k'] == 'row':
                rows.append(k)
            elif k['k'] == 'region':
                self.notes.append(f'nested {k["kind"]} over table rows flattened (condition dropped)')
                rows.extend(self.flat_rows(k['kids']))
        return rows

    def row_comps(self, kids):
        """Rows and nested row regions (CA 99 28: a coverage list inside the auto list). A nested region becomes a
        nested tbody component, which the designer exports as its Liquid tags alone (no tag of its own)."""
        out, plain = [], []
        for k in kids:
            if k['k'] == 'row':
                plain.append(k)
            elif k['k'] == 'region':
                if plain:
                    out.extend(self.rows(plain))
                    plain = []
                out.extend(self.row_region(k))
        if plain:
            out.extend(self.rows(plain))
        return out

    def cond_tbodies(self, kids, cond):
        """Rows under a row-level condition. Plain rows become a Show If tbody; a row list inside becomes a repeat
        tbody carrying the same condition (tbodies cannot nest, so the condition moves onto the loop)."""
        out, plain = [], []
        for k in kids + [None]:
            if k is not None and (k['k'] == 'row' or k['kind'] != 'list'):
                plain.append(k)
                continue
            if plain:
                out.append(dict({'type': 'conditional', 'tagName': 'tbody', 'components': self.row_comps(plain)},
                                **cond))
                plain = []
            if k is not None:
                rep = self.row_region(k)[0]
                if cond.get('field'):
                    rep.update(cond)
                out.append(rep)
        return out

    def row_region(self, r):
        kind = r['kind']
        if kind == 'if':
            self.counts['show if'] += 1
            return self.cond_tbodies(r['kids'], r['cond'])
        if kind == 'list':
            self.counts['repeat'] += 1
            rep = {'type': 'repeat', 'tagName': 'tbody', 'listPath': r['collection'],
                   'components': self.row_comps(r['kids'])}
            if r.get('filter'):
                f = r['filter']
                rep.update(itemField=f['field'], itemOperator=f.get('operator', 'present'), itemValue=f.get('value', ''))
            return [rep]
        if kind == 'choice':
            self.counts['choose'] += 1
            conds = [b['cond'] for b in r['kids']]
            out = []
            for i, b in enumerate(r['kids']):
                cond = b['cond']
                if cond is None:
                    prev = [c for c in conds[:i] if c]
                    cond = negate(prev[0]) if len(prev) == 1 else None
                    if cond is None:
                        self.notes.append('row-level else after several conditions approximated as always shown')
                        cond = {'field': '', 'operator': 'present', 'value': ''}
                elif i > 0:
                    self.notes.append('row-level else-if approximated as an independent Show If')
                out.extend(self.cond_tbodies(b['kids'], cond))
            return out
        return [{'type': 'tbody', 'components': self.row_comps(r['kids'])}]

    def rows(self, rows):
        # vertical merges within this group: 'cont' cells are removed, the 'first' cell above gets a rowspan
        grid = []
        for row in rows:
            cells = []
            prev_x = row['def']['left']
            for i, cell in enumerate(row['kids']):
                cd = row['def']['cells'][i] if i < len(row['def']['cells']) else None
                x = cd['x'] if cd else prev_x + 1440
                cells.append({'cell': cell, 'def': cd, 'x': x, 'w': x - prev_x, 'span': 1, 'drop': False,
                              'pad': row['def']['pad'], 'pct': row['def'].get('fts') == 2})
                prev_x = x
            grid.append(cells)
        for ri, cells in enumerate(grid):
            for c in cells:
                if c['def'] and c['def']['vm'] == 'cont':
                    for above in reversed(grid[:ri]):
                        target = next((a for a in above if a['x'] == c['x'] and not a['drop']), None)
                        if target is not None:
                            target['span'] += 1
                            c['drop'] = True
                            break
                        if not any(a['x'] == c['x'] for a in above):
                            break
        out = []
        for row, cells in zip(rows, grid):
            comps = []
            for c in cells:
                if c['drop']:
                    continue
                comp = {'type': 'cell', 'components': self.blocks(c['cell']['kids'])}
                exact = row['def'].get('h') or 0
                if exact < 0:
                    # an exact row height (\trrh negative) doesn't grow with its content; a CSS row does (DA 40 93:
                    # "ADDITIONAL COVERAGE ENDORSEMENTS INCLUDED:" and a blank line in an 18.7pt row, 15pt too tall).
                    # GhostDraft doesn't clip what doesn't fit, it prints over the next row (CA 04 54's VIN), so the
                    # content box is capped and the rest overflows visibly
                    pad = self.cell_pad(c)
                    inner = max(-exact - pad.get('t', 0) - pad.get('b', 0), 0)
                    clip = self.cls('gd-x', [f'max-height:{pt(inner)}', 'overflow:visible'])
                    comp['components'] = [{'tagName': 'div', 'classes': [clip], 'components': comp['components']}]
                cc = self.cell_class(c)
                if cc:
                    comp['classes'] = [cc]
                attrs = {}
                if c['span'] > 1:
                    attrs['rowspan'] = str(c['span'])
                if getattr(self, '_cols', None):
                    n = self.colspan(c['x'] - c['w'], c['x'])
                    if n > 1:
                        attrs['colspan'] = str(n)
                if attrs:
                    comp['attributes'] = attrs
                comps.append(comp)
            tr = {'type': 'row', 'components': comps}
            h = row['def'].get('h') or 0
            if h > 0:
                # GhostDraft lays an at-least row height out as the content height, the cell top/bottom padding
                # comes on top (CA 20 16 schedule: \trrh518 with 3.6pt top padding prints a 29.9pt row pitch);
                # exact heights (negative) include it (CA 20 16 page 1 text boxes)
                pads = [self.cell_pad(c) for c in cells if not c['drop']]
                h += max((p.get('t', 0) + p.get('b', 0) for p in pads), default=0)
                # ...and so do the row's borders (0.5pt borders: 29.4pt of height + padding prints a 29.9pt
                # pitch); a collapsed CSS row holds half of each border inside its height
                def bw(c, side):
                    b = (c['def'] or new_celldef())['b'].get(side)
                    return b['w'] if b and b.get('s') != 'none' and b.get('w') else 0
                h += max(((bw(c, 't') + bw(c, 'b')) / 2 for c in cells if not c['drop']), default=0)
            h = abs(h)
            if h:
                # Word row height (\trrh): negative = exact, positive = at least; CSS row height is a minimum
                tr['classes'] = [self.cls('gd-tr', [f'height:{pt(h)}'])]
            out.append(tr)
        return out

    @staticmethod
    def cell_pad(c):
        """The row's cell margins with the cell's own on top. In a percent-width table a cell margin of 0 keeps the
        row's: GhostDraft prints DA 40 93's "Named Insured:" (\\clpadl0 under \\trpaddl108) 5.4pt in from the table
        edge, while the ISO schedules' fixed-width tables print their 0-margin amount cells at 0 (CA 20 21)."""
        own = (c['def'] or new_celldef())['pad']
        return dict(c['pad'], **({k: v for k, v in own.items() if v} if c.get('pct') else own))

    def cell_class(self, c):
        cd = c['def'] or new_celldef()
        decls = [f'width:{self.tw(c["w"])}' if c['w'] > 0 and not getattr(self, '_cols', None) else '']
        for side, name in (('t', 'top'), ('l', 'left'), ('b', 'bottom'), ('r', 'right')):
            b = cd['b'].get(side)
            if not b or b.get('s') == 'none' or not b.get('w'):
                continue
            w = max(b['w'] / 20, 0.5)
            style = b.get('s', 'solid')
            if style == 'double':
                w = max(w * 3, 2.25)
            decls.append(f'border-{name}:{w:g}pt {style} {b.get("c") or "#000"}')
        if cd['valign'] in ('c', 'b'):
            decls.append('vertical-align:' + ('middle' if cd['valign'] == 'c' else 'bottom'))
        if cd.get('bg'):
            decls.append(f'background:{cd["bg"]}')
        pad = self.cell_pad(c)
        # Word draws cell borders inside the padding, so a bordered cell keeps its full text width; a collapsed CSS
        # border takes half its width out of the cell (CA 20 16 "Specified Causes Of Loss" wrapped at 0.5pt short)
        for side in 'lr':
            b = cd['b'].get(side)
            if b and b.get('s') != 'none' and b.get('w') and pad.get(side, 0) > 0:
                pad[side] = max(pad[side] - max(b['w'], 10) / 2, 0)
        if pad:
            decls.append('padding:' + ' '.join(pt(pad.get(s, 0)) for s in 'trbl'))
        return self.cls('gd-td', decls)

    # ------------------------------------------------------------------ pages
    def document(self, tree, page):
        """Top-level components. A page carrying page-anchored shapes (GhostDraft's ISO layout: an EMF+ wording
        sheet behind the page plus positioned text boxes for the data) becomes a fixed designer form page; any
        other page flows."""
        self.page = page
        sinks = tree['sinks']
        header = sinks.get('header') or sinks.get('header-first')
        footer = sinks.get('footer') or sinks.get('footer-first')
        pages = split_pages(tree['doc']['kids'])
        sects = page_sections(pages)
        kept = [i for i, p in enumerate(pages) if any(has_content(b) for b in p) or self.page_shapes(p)]
        pages, sects = [pages[i] for i in kept], [sects[i] for i in kept]
        # \titlepg first-page header = the running header plus lines of its own (EA 99 05: "THIS ENDORSEMENT
        # CHANGES THE POLICY..."): the running header repeats, the extra lines open the first page's body
        first_hdr = sinks.get('header-first')
        split_first = None
        if header and first_hdr and first_hdr is not header and pages and not self.page_shapes(pages[0]):
            def sig(kids):
                return [(k['k'], ''.join(flatten_text(k)).strip()) for k in kids if has_content(k)]
            hs, fs = sig(header['kids']), sig(first_hdr['kids'])
            if fs[:len(hs)] == hs and len(fs) > len(hs):
                # the extra lines follow the last kid that matches the running header's content
                seen, n = 0, 0
                for i, k in enumerate(first_hdr['kids']):
                    if seen == len(hs):
                        break
                    if has_content(k):
                        seen += 1
                        n = i + 1
                last = max((i + 1 for i, k in enumerate(header['kids']) if has_content(k)), default=0)
                n += len(header['kids']) - last
                pages[0] = first_hdr['kids'][n:] + pages[0]
            elif fs != hs and len(pages) > 1 and not self.page_shapes(pages[1]):
                # a first-page header of its own (DA 00 93: the company name page 1, "(Continued)" after): page 1
                # flows under it and the running header starts with page 2 (page 1 overflowing gets no header)
                split_first = first_hdr
        hf = [s['kids'] for s in (header, footer) if s]
        any_sheet = any(self.page_shapes(p) for p in pages)
        # footers with a page number: a flow's footer moves into the PDF's own footer (the renderer prints one
        # footer template per section and odd/even side, then picks each page from the matching print), a form
        # sheet's footer is laid out on the sheet and numbered after a first print finds each sheet's page
        ff = sinks.get('footer-first')
        feet = [footer, sinks.get('footer-other')] + [f for s in sects if s for f in (sect_hf(s)[1], s.get('footer-other'))]
        # any running footer goes there, not only a numbered one: Chrome prints a flow's last tfoot right under the
        # body, where Word keeps the footer at the foot of the page (DA 00 93 page 1). A first page split into a flow
        # of its own (split_first) is a section of its own, so its first-page footer may differ.
        self._pdf_feet = bool(any(f and has_content({'k': 'region', 'kids': strip_shapes(f['kids'])}) for f in feet)
                              and (not ff or foot_sig(ff) == foot_sig(footer) or split_first))
        self._doc_sinks, self._sec_ids = sinks, {}
        out, flow = [], []
        queue = list(zip(pages, sects))
        if split_first:
            out.append(self.flow(queue.pop(0)[0], split_first, sinks.get('footer-first') or footer, sect={}))
            out.append({'type': 'page-break'})
            any_sheet = True   # two flows: a fixed footer would print twice on page 1
        flow_sect, flow_hf, need_break = None, (header, footer), False
        while queue:
            blocks, sect = queue.pop(0)
            page_hf = (header, footer) if sect is None else sect_hf(sect)
            if flow and sect is not flow_sect:
                # a new section with headers/footers of its own: a flow of its own, starting a new page
                out.append(self.flow(flow, *flow_hf, sect=flow_sect))
                flow, need_break = [], True
            if self.page_shapes(blocks):
                if flow:
                    out.append(self.flow(flow, *flow_hf, sect=flow_sect))
                    flow = []
                need_break = False
                sheet, loose = self.sheet(blocks, [s['kids'] for s in page_hf if s], page_hf[1], sect)
                out.append(sheet)
                if loose:
                    # body text on a wording sheet (e.g. an overflow schedule) can't share the page with the ISO
                    # wording, so it continues on the following page(s)
                    self.notes.append('body text outside the text boxes moved to a page after the form sheet')
                    queue.insert(0, (loose, sect))
            else:
                if flow or need_break:
                    # a page that is one Show If takes its page break inside, so a hidden page leaves no blank sheet
                    only = sole_if(blocks)
                    if only:
                        blocks = [dict(only, kids=[{'k': 'pagebreak'}] + only['kids'])]
                    else:
                        blocks = [{'k': 'pagebreak'}] + blocks
                    need_break = False
                flow.extend(blocks)
                flow_sect, flow_hf = sect, page_hf
        if flow:
            out.append(self.flow(flow, *flow_hf, fixed_foot=not any_sheet and not out, sect=flow_sect))
        return out

    def sec_class(self, sect):
        """The class naming a section's footers (None = the document's own; {} = the split first page's)."""
        obj = self._doc_sinks if sect is None else sect
        return f'gd-sec-{self._sec_ids.setdefault(id(obj), len(self._sec_ids))}'

    def even_foot(self, sect, odd):
        """Word's facing-page footer for the section's even pages, when it differs from the odd pages' one."""
        fo = (self._doc_sinks if sect is None else sect).get('footer-other')
        return fo if fo and has_content(fo) and foot_sig(fo) != foot_sig(odd) else None

    def flow(self, blocks, header, footer, fixed_foot=False, sect=None):
        head, foot, pinned = [], [], []
        # page-anchored shapes in the header/footer (DA 01 93: the whole form grid is an EMF+ picture behind the page
        # plus text boxes for the policy number and page count) print on every page: they ride in the running
        # header, positioned from the page's top-left corner
        hf_shapes = []
        for s in (header, footer):
            if s and s['kids']:
                hf_shapes.extend(self.page_shapes(s['kids']))
        if hf_shapes:
            header = dict(header or {'kids': []}, kids=strip_shapes((header or {'kids': []})['kids']))
            if footer:
                footer = dict(footer, kids=strip_shapes(footer['kids']))
            pg = self.page
            comps = []
            for shape, stack in hf_shapes:
                for c in self.shape_abs(shape):
                    comps.append(self.wrap_stack(c, stack))
            bg = self.cls('gd-bg', [f'left:-{pt(pg.get("margl", 1440))}', f'top:-{pt(pg.get("headery", 720))}'])
            bgc = [{'tagName': 'div', 'classes': ['gd-pagebg', bg], 'components': comps}]
            self.counts['page backgrounds'] += 1
        else:
            bgc = []
        # a header of blank lines still pushes the body down in Word, so it is kept even without text
        if header and header['kids']:
            head = [{'tagName': 'div', 'classes': ['gd-header'], 'components': bgc + self.blocks(header['kids'])}]
            self.notes.append('running header repeats on every flowing page')
        elif bgc:
            head = [{'tagName': 'div', 'classes': ['gd-header'], 'components': bgc}]
        if footer and footer['kids']:
            foot = [{'tagName': 'div', 'classes': ['gd-footer'], 'components': self.blocks(footer['kids'])}]
            self.notes.append('running footer repeats on every flowing page')
            if fixed_foot:
                # fixed elements print on EVERY page of the document, so only a document that is one flow does this
                pinned = [dict(copy.deepcopy(foot[0]), classes=['gd-footer', 'gd-foot-fixed'])]
                foot[0]['classes'] = ['gd-footer', 'gd-foot-space']
            if getattr(self, '_pdf_feet', False):
                pg = self.page
                sec = [self.sec_class(sect)]
                # the page geometry rides in class names (the renderer's sanitizer drops data-* attributes)
                geo = [f'gd-{k}-{pg.get(n, d) / 20:g}' for k, n, d in (('fy', 'footery', 720), ('mb', 'margb', 1440),
                                                                       ('ml', 'margl', 1440), ('mr', 'margr', 1440))]
                foot[0]['classes'] = foot[0]['classes'] + ['gd-pdffoot'] + sec + geo
                even = self.even_foot(sect, footer)
                if even:
                    # Word's facing-page footer for even pages (page number on the left): laid out invisibly
                    # beside the odd one so the renderer can print it on the even pages
                    foot.append({'tagName': 'div', 'classes': ['gd-footer', 'gd-pdffoot-even'] + sec,
                                 'components': self.blocks(strip_shapes(even['kids']))})
                # repeats with the header on every page of this flow: tells the renderer the page's section
                head = head + [{'tagName': 'span', 'classes': ['gd-pgmark'] + sec}]
        first = blocks[0] if len(blocks) == 1 else sole_if(blocks)

        def band(tag, comps):
            return {'tagName': tag, 'components': [{'tagName': 'tr', 'components': [
                {'tagName': 'td', 'components': comps}]}]}

        def run_table(body):
            return {'tagName': 'table', 'classes': ['gd-runt'], 'components': [
                band('thead', head), band('tfoot', foot), band('tbody', body)]}
        if first and first['k'] == 'region' and first['kind'] == 'if':
            # a conditional page: its header/footer show only when the page does
            def inner(region):
                lead = region['kids'][:1] if region['kids'][:1] == [{'k': 'pagebreak'}] else []
                rest = region['kids'][len(lead):]
                body = [k for k in rest if has_content(k)]
                if len(body) == 1 and body[0]['k'] == 'region' and body[0]['kind'] == 'if':
                    # blank paragraphs beside the inner condition move inside it, so a hidden page prints nothing
                    at = next(i for i, k in enumerate(rest) if k is body[0])
                    moved = dict(body[0], kids=rest[:at] + body[0]['kids'] + rest[at + 1:])
                    return dict(region, kids=lead + [inner(moved)])
                if not head and not foot:
                    return region
                return dict(region, kids=lead + [{'k': 'raw', 'comps': [run_table(self.blocks(rest))]}])
            return {'tagName': 'div', 'classes': ['gd-doc', 'gd-flow'] + (['gd-run'] if head or foot else []),
                    'components': self.blocks([inner(first)]) + pinned}
        if not head and not foot:
            return {'tagName': 'div', 'classes': ['gd-doc', 'gd-flow'], 'components': self.blocks(blocks)}
        return {'tagName': 'div', 'classes': ['gd-doc', 'gd-flow', 'gd-run'],
                'components': [run_table(self.blocks(blocks))] + pinned}

    def has_field(self, nodes):
        return any((n.get('k') == 'field' and n.get('name') in ('PAGE', 'NUMPAGES', 'SECTIONPAGES'))
                   or self.has_field(n.get('kids', [])) for n in nodes)

    def page_shapes(self, blocks):
        found = []
        self.collect(blocks, [], found)
        return found

    def collect(self, nodes, stack, out):
        for n in nodes:
            k = n.get('k')
            if k == 'shape' and 'page' in (n['relh'], n['relv']):
                out.append((n, list(stack)))
                continue
            if k == 'region':
                if n['kind'] == 'choice':
                    for b in n['kids']:
                        self.collect(b['kids'], stack + [('branch', b, n)], out)
                else:
                    self.collect(n['kids'], stack + [(n['kind'], n, None)], out)
            elif 'kids' in n:
                self.collect(n['kids'], stack, out)

    def sheet(self, blocks, hf, footer=None, sect=None):
        placed = self.page_shapes(blocks)
        for kids in hf:
            # a page that is one Show If (a branch's page) shows the header's sheet only when it shows
            self.collect(kids, page_ifs(blocks), placed)
        comps = []
        for shape, stack in placed:
            for c in self.shape_abs(shape):
                comps.append(self.wrap_stack(c, stack))
        if getattr(self, '_pdf_feet', False) and footer and has_content({'k': 'region', 'kids': strip_shapes(footer['kids'])}):
            # the section's footer text prints on the sheet too (CA 20 01: "Page 1 of 4" beside the wording sheet's
            # own "Page 1 of 2"); the renderer fills its page numbers and shows the even side's on even pages
            pg = self.page
            box = {'left': pt(pg.get('margl', 1440)), 'right': pt(pg.get('margr', 1440)),
                   'bottom': pt(pg.get('footery', 720))}
            even = self.even_foot(sect, footer)
            for side, f in [('odd', footer)] + ([('even', even)] if even else []):
                kids = self.blocks(strip_shapes(f['kids']))
                if side == 'odd':
                    kids.append({'tagName': 'span', 'classes': ['gd-pgmark']})
                comp = {'tagName': 'div', 'classes': ['gd-doc', 'gd-sheetfoot', f'gd-side-{side}'],
                        'style': dict(box), 'components': kids}
                comps.append(self.wrap_stack(comp, page_ifs(blocks)))
        loose = [b for b in strip_shapes(blocks) if has_content(b)]
        self.counts['form pages'] += 1
        return {'type': 'legacy-page', 'tagName': 'section', 'classes': ['form-page'], 'components': comps}, loose

    def shape_abs(self, shape):
        pg = self.page
        left, top, right, bottom = shape['box']
        if shape['relh'] != 'page':
            left, right = left + pg['margl'], right + pg['margl']
        if shape['relv'] != 'page':
            top, bottom = top + pg['margt'], bottom + pg['margt']
            if shape['relv'] == 'para':
                self.notes.append('paragraph-anchored text box placed relative to the top margin')
        out = []
        rest = []
        for b in shape['kids']:
            imgs = [x for x in b.get('kids', []) if x['k'] == 'img' and x.get('emf')] if b['k'] == 'para' else []
            for img in imgs:
                out.extend(self.emf_spans(img, left / 20, top / 20))
            if imgs:
                b = dict(b, kids=[x for x in b['kids'] if not (x['k'] == 'img' and x.get('emf'))])
            rest.append(b)
        if any(has_content(b) for b in rest):
            sp = shape['sp']

            def emu(name, default):
                try:
                    return f'{int(sp.get(name, default)) / 12700:g}pt'
                except ValueError:
                    return '0pt'
            style = {'left': pt(left), 'top': pt(top), 'width': pt(max(right - left, 0)),
                     'padding': ' '.join(emu(n, d) for n, d in (('dyTextTop', 45720), ('dxTextRight', 91440),
                                                              ('dyTextBottom', 45720), ('dxTextLeft', 91440)))}
            if sp.get('fLine') == '1':
                style['border'] = '0.75pt solid #000'
            self.counts['text boxes'] += 1
            out.append({'tagName': 'div', 'classes': ['abs', 'gd-doc', 'gd-tb'], 'style': style,
                        'components': self.blocks(rest)})
        return out

    def emf_spans(self, img, ox, oy):
        """The wording sheet as absolutely positioned pieces. GhostDraft draws the sheet with one DrawString per
        run and splits runs wherever Word placed glyphs itself (kerning, justification, tab stops, columns), so a
        run is only continued while the next piece starts where the previous one ends; anything else starts a new
        piece at its own x. Nothing is stretched or re-flowed, so justified lines and columns land where they did.
        The rules and boxes (filled paths) come first so text paints over them."""
        sheet = emfplus.extract(img['emf'], img.get('w') or 612, img.get('h') or 792)
        out = []
        for r in sheet['rules']:
            if r['color'].lower() == '#ffffff' or r['w'] <= 0 or r['h'] <= 0:
                continue
            style = {'left': f'{ox + r["x"]:.2f}pt', 'top': f'{oy + r["y"]:.2f}pt',
                     'width': f'{max(r["w"], 0.5):.2f}pt', 'height': f'{max(r["h"], 0.5):.2f}pt'}
            if r['color'].lower() != '#000000':
                style['background'] = r['color']
            out.append({'type': 'legacy-shape', 'tagName': 'div', 'classes': ['rule'], 'style': style})
            self.counts['rules (EMF+)'] += 1
        lines = []
        for it in sheet['items']:
            style = (round(it['size'], 1), it['bold'], it['italic'], it['underline'], it['strike'], it['font'],
                     it['color'])
            prev = lines[-1] if lines else None
            # 'end' is where the browser will put the next glyph (the run's start + its natural width), so small
            # justification gaps can't add up along a line: past 0.35pt the piece starts a new span
            # a lone space always joins the run before it: as its own piece it would be dropped and the word gap
            # lost from the text
            if prev and abs(prev['y'] - it['y']) < 0.6 and prev['style'] == style and (
                    abs(it['x'] - prev['end']) <= 0.35 or (not it['text'].strip() and abs(it['x'] - prev['end']) <= 2)):
                prev['parts'].append(it)
            else:
                prev = {'y': it['y'], 'x': it['x'], 'style': style, 'parts': [it], 'end': it['x']}
                lines.append(prev)
            prev['end'] += measure(it['text'], it['size'], it['bold'], it['italic'], it['font'])
        for ln in lines:
            text = ''.join(p['text'] for p in ln['parts'])
            if not text.strip():
                continue
            size, bold, italic, ul, strike, font, color = ln['style']
            deco = ' '.join(x for x in ('underline' if ul else '', 'line-through' if strike else '') if x)
            style = {'left': f'{ox + ln["x"]:.2f}pt', 'top': f'{oy + ln["y"]:.2f}pt', 'font-size': f'{size:g}pt',
                     'font-family': font_css(font.title() if font.isupper() else font)}
            if bold:
                style['font-weight'] = 'bold'
            if italic:
                style['font-style'] = 'italic'
            if deco:
                style['text-decoration'] = deco
            if color and color.lower() != '#000000':
                style['color'] = color
            out.append({'type': 'legacy-text', 'tagName': 'span', 'classes': ['abs'], 'style': style,
                        'components': [{'type': 'textnode', 'content': text}]})
            self.counts['wording lines (EMF+)'] += 1
        return out

    def wrap_stack(self, comp, stack):
        for kind, r, choice in reversed(stack):
            if kind == 'if':
                comp = dict({'type': 'conditional', 'tagName': 'div', 'components': [comp]}, **r['cond'])
            elif kind == 'list':
                self.notes.append(f'repeat over {r["collection"]} wraps a positioned text box (items overlap)')
                comp = {'type': 'repeat', 'tagName': 'div', 'listPath': r['collection'], 'components': [comp]}
            elif kind == 'branch':
                cond = r['cond']
                others = [b['cond'] for b in choice['kids'] if b is not r and b['cond']]
                if cond is None:
                    cond = negate(others[0]) if len(others) == 1 else None
                    if cond is None:
                        self.notes.append('else branch around a positioned text box shown unconditionally')
                        continue
                elif choice['kids'][0] is not r:
                    self.notes.append('else-if around a positioned text box approximated as an independent Show If')
                comp = dict({'type': 'conditional', 'tagName': 'div', 'components': [comp]}, **cond)
        return comp


def sole_if(blocks):
    """A page that is one block-level Show If plus blank lines: the Show If with the blank lines moved inside, so
    a hidden page prints nothing (CA 99 28's attached schedule after the form sheet); else None."""
    body = [b for b in blocks if has_content(b)]
    if len(body) != 1 or body[0]['k'] != 'region' or body[0]['kind'] != 'if' or body[0].get('inline'):
        return None
    if any(b is not body[0] and b['k'] != 'para' for b in blocks):
        return None
    at = next(i for i, b in enumerate(blocks) if b is body[0])
    return dict(body[0], kids=blocks[:at] + body[0]['kids'] + blocks[at + 1:])


def page_ifs(blocks):
    """The Show Ifs a page's whole content sits in, outermost first, as collect() stack entries."""
    stack = []
    body = [b for b in blocks if has_content(b) or collect_shapes(b)]
    while len(body) == 1 and body[0]['k'] == 'region' and body[0]['kind'] == 'if' and not body[0].get('inline'):
        stack.append(('if', body[0], None))
        body = [b for b in body[0]['kids'] if has_content(b) or collect_shapes(b)]
    return stack


def foot_sig(s):
    """A footer's text with its fields in place, so a facing-page footer that only swaps the page number and form
    number around differs from the odd pages' one."""
    def toks(n):
        if n.get('k') == 'field':
            return ['{' + (n.get('prop') or n.get('name', '')) + '}']
        if n.get('k') == 'run':
            return [n['t']]
        return [t for k in n.get('kids', []) for t in toks(k)]
    return json.dumps([''.join(toks(k)) for k in s['kids'] if has_content(k)]) if s else None


def page_sections(pages):
    """Each page's section headers/footers, or None for the document's. A header/footer subscription's section break
    carries the headers/footers of the pages before it, back to the previous such break (Word keeps a section's
    properties at its end: CA 20 01's overflow branch prints the ISO-wording footer on its two sheets and the
    schedule footer on the schedule pages); pages after the last one print the document's."""
    out, pending = [None] * len(pages), []
    for i, p in enumerate(pages):
        pending.append(i)
        s = sect_sinks(p)
        if s is not None:
            for j in pending:
                out[j] = s
            pending = []
    return out


def sect_sinks(nodes):
    for n in nodes:
        if n.get('k') == 'secbreak' and n.get('sinks') is not None:
            return n['sinks']
        if n.get('k') == 'region' and not n.get('inline'):
            s = sect_sinks(n['kids'])
            if s is not None:
                return s
    return None


def sect_hf(sinks):
    return sinks.get('header') or sinks.get('header-first'), sinks.get('footer') or sinks.get('footer-first')


def split_pages(blocks):
    """Split top-level blocks at page breaks, cutting block-level Show If regions that contain one. A block-level
    choice with page breaks in it (CA 20 01: overflow schedule or not, each branch two ISO sheets) is cut as one
    Show If per branch."""
    pages = [[]]
    for b in expand_choices(blocks):
        if b['k'] == 'pagebreak':
            pages.append([])
            continue
        if b['k'] == 'secbreak':
            if b.get('sinks') is not None:
                # kept on the page it ends: page_sections() gives its headers/footers to the pages before it
                pages[-1].append(b)
            # a section break only starts a page when something is already on this one
            if any(has_content(x) or collect_shapes(x) for x in pages[-1]):
                pages.append([])
            continue
        if b['k'] == 'region' and b['kind'] == 'if' and not b.get('inline') and contains_pagebreak(b):
            for i, part in enumerate(split_pages(b['kids'])):
                if i:
                    pages.append([])
                if part:
                    pages[-1].append(dict(b, kids=part))
            continue
        pages[-1].append(b)
    return pages


def contains_pagebreak(n):
    return any(k['k'] in ('pagebreak', 'secbreak') or (k['k'] == 'region' and k['kind'] in ('if', 'choice', 'branch')
                                                       and not k.get('inline') and contains_pagebreak(k))
               for k in n.get('kids', []))


def expand_choices(blocks):
    for b in blocks:
        ifs = None
        if b['k'] == 'region' and b['kind'] == 'choice' and not b.get('inline') and contains_pagebreak(b):
            ifs = choice_as_ifs(b)
        if ifs:
            yield from ifs
        else:
            yield b


def choice_as_ifs(choice):
    """A block-level choice as one Show If per branch: if / else (the else is the first condition negated). None when
    a branch can't be one Show If (else-if chains)."""
    branches = choice['kids']
    if len(branches) != 2 or not branches[0]['cond']:
        return None
    first, second = branches[0]['cond'], branches[1]['cond']
    # "if <test> / otherwise" whose otherwise was made "<test> is false" (cond_op) is still an if/else pair
    complement = bool(second) and second.get('field') == first.get('field') and \
        {first.get('operator'), second.get('operator')} == {'eq'} and {first.get('value'), second.get('value')} == {'true', 'false'}
    conds = [first, second or negate(first)]
    if (second and not complement) or not conds[1]:
        return None
    return [{'k': 'region', 'kind': 'if', 'level': choice['level'], 'inline': False, 'kids': b['kids'], 'cond': c}
            for b, c in zip(branches, conds)]


def collect_shapes(n):
    if n.get('k') == 'shape' and 'page' in (n.get('relh'), n.get('relv')):
        return True
    return any(collect_shapes(k) for k in n.get('kids', []))


def strip_shapes(nodes):
    out = []
    for n in nodes:
        if n.get('k') == 'shape' and 'page' in (n['relh'], n['relv']):
            continue
        if 'kids' in n:
            n = dict(n, kids=strip_shapes(n['kids']))
        out.append(n)
    return out


_FONTS: dict = {}


def measure(text, size, bold, italic, family):
    """Advance width in pt using the real TrueType metrics (Windows fonts)."""
    if ImageFont is None or not text:
        return 0.0
    fam = (family or 'Arial').lower()
    base = 'times' if 'times' in fam else 'arial'
    suffix = {(False, False): '', (True, False): 'bd', (False, True): 'i', (True, True): 'bi'}[(bool(bold), bool(italic))]
    name = {'times': {'': 'times', 'bd': 'timesbd', 'i': 'timesi', 'bi': 'timesbi'},
            'arial': {'': 'arial', 'bd': 'arialbd', 'i': 'ariali', 'bi': 'arialbi'}}[base][suffix] + '.ttf'
    key = (name, 1000)
    font = _FONTS.get(key)
    if font is None:
        try:
            font = _FONTS[key] = ImageFont.truetype(name, 1000)
        except OSError:
            return 0.0
    return font.getlength(text) * size / 1000


def font_css(name):
    n = (name or 'Arial').strip()
    generic = 'serif' if 'times' in n.lower() or 'garamond' in n.lower() else 'sans-serif'
    return f"'{n}',{generic}" if re.fullmatch(r"[A-Za-z0-9 \-]+", n) else generic


# =====================================================================================================
# Sample model
# =====================================================================================================

def label(name):
    return re.sub(r'([a-z])([A-Z])', r'\1 \2', name).replace('_', ' ').strip()


def sample(kind, leaf, value, idx):
    if kind == 'bool':
        return True
    if kind == 'enum':
        return value or ''
    if kind == 'date':
        return f'2026-0{7 + idx}-01'
    if kind == 'money':
        return 25000 + 5000 * idx
    if kind == 'number':
        return 2 + idx
    return f'{label(leaf)}{" " + str(idx + 1) if idx else ""}'


def build_model(bindings):
    model: dict = {}
    notes = []
    # containers before leaves so a path that is both keeps its children
    for xml, b in sorted(bindings.items(), key=lambda kv: (kv[0].count('/'), kv[0])):
        segs = [s for s in xml.split('/') if s]
        if segs:
            put(model, segs, b, 0, notes, xml)
    return model, notes


def put(obj, segs, b, idx, notes, xml):
    i = 0
    while i < len(segs):
        seg = segs[i]
        last = i == len(segs) - 1
        if seg == 'Items' and i + 1 < len(segs):
            key = plural(segs[i + 1])
            arr = obj.get(key)
            if not isinstance(arr, list):
                arr = obj[key] = [{}, {}]
            rest = segs[i + 2:]
            if rest:
                for n, item in enumerate(arr):
                    if isinstance(item, dict):
                        put(item, rest, b, n, notes, xml)
            return
        key = ident(seg)
        if last:
            kind = b['kind']
            if kind in ('present', 'list'):
                if key not in obj:
                    obj[key] = {} if kind == 'present' else [{}, {}]
                return
            cur = obj.get(key)
            if isinstance(cur, (dict, list)) and cur:
                return
            obj[key] = sample(kind, seg, b.get('value'), idx)
            return
        nxt = obj.get(key)
        if not isinstance(nxt, dict):
            if nxt is not None and not isinstance(nxt, dict):
                notes.append(f'{xml}: {key} is both a value and a group; kept the group')
            nxt = obj[key] = {}
        obj = nxt
        i += 1


# =====================================================================================================
# Entry point
# =====================================================================================================

def safe_name(name):
    return re.sub(r'[^A-Za-z0-9_-]+', '-', name).strip('-')[:64] or 'ghostdraft-form'


def tag_conditionals(comps):
    """Show If wrappers survive into the rendered HTML even when their content doesn't; the renderer's sanitizer
    drops data-* attributes, so mark them with a class the CSS can use to collapse empty ones."""
    for c in comps:
        if c.get('type') in ('conditional', 'choice', 'choice-branch'):
            c['classes'] = list(c.get('classes', [])) + ['gd-if']
        tag_conditionals(c.get('components') or [])


def drop_unused_wording(bindings, comps):
    """(Wording) attributes the document no longer prints (see Emitter.wording_fallback) leave the sample model."""
    used = set()

    def walk(cs):
        for c in cs or []:
            for key in ('field', 'listPath', 'itemField'):
                if c.get(key):
                    used.add(str(c[key]).split('.')[-1])
            walk(c.get('components'))
    walk(comps)
    return {xml: b for xml, b in bindings.items()
            if not (xml.endswith('Wording') and ident(xml.split('/')[-1]) not in used)}


def convert_one(conv: Converter, name: str, out_dir: str):
    tree = conv.convert(name)
    em = Emitter(conv.notes, conv.counts, tree['title'] or name, getattr(conv, 'props', {}))
    em.collapse_wording = getattr(conv, 'collapse_wording', False)
    sinks = tree['sinks']
    em.pick_base([tree['doc']] + [s for s in sinks.values() if s])
    comps = em.document(tree, conv.page)
    tag_conditionals(comps)
    model, model_notes = build_model(drop_unused_wording(conv.bind, comps))
    result = {
        'name': safe_name(name),
        'title': tree['title'] or name,
        'source': os.path.basename(conv.index[name.lower()]),
        'package': os.path.basename(conv.package.rstrip('\\/')),
        'components': comps,
        'css': em.css(),
        'model': model,
        'report': {
            'counts': dict(sorted(conv.counts.items())),
            'notes': sorted(set(conv.notes + model_notes)),
        },
    }
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, result['name'] + '.json')
    with open(path, 'w', encoding='utf-8') as fh:
        json.dump(result, fh, ensure_ascii=False, indent=1)
    return path, result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('package', help='extracted GhostDraft package (contains model.xml and Templates/)')
    ap.add_argument('form', nargs='?', help='template name without .gd')
    ap.add_argument('--forms-csv', help='convert every GhostDraftForm in this inventory that exists in the package')
    ap.add_argument('--wording-fallbacks', action='store_true',
                    help='turn "X is provided / otherwise X (Wording)" choices into one field with "If empty, show" '
                         'and drop the (Wording) attributes (off: MOE Server XML fills many Wording elements)')
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                                                  'output', 'ghostdraft'))
    args = ap.parse_args()
    conv = Converter(args.package)
    conv.collapse_wording = args.wording_fallbacks
    names = []
    if args.form:
        names = [args.form]
    elif args.forms_csv:
        with open(args.forms_csv, encoding='utf-8-sig') as fh:
            names = [r['GhostDraftForm'] for r in csv.DictReader(fh) if r['GhostDraftForm'].lower() in conv.index]
    for n in names:
        try:
            path, res = convert_one(conv, n, args.out)
            c = res['report']['counts']
            print(f'{n} -> {path}')
            print('   ' + ', '.join(f'{k}={v}' for k, v in c.items()) + f', notes={len(res["report"]["notes"])}')
        except Exception as ex:  # noqa: BLE001 -- batch keeps going; the failure is reported
            print(f'{n} FAILED: {type(ex).__name__}: {ex}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
