"""Marker ORDER: the second authoring rule for .gd logic.

For a container (list / conditional), the parts' `%[ID]` markers must appear in
the RTF in an order that brackets the content. This checks, over all 491
production templates:

  1. do a container's part markers appear in DECLARATION order?
  2. is the endPart marker always LAST among its siblings?
  3. is the whole part span nested inside the parent's span?

If all three hold universally, an authoring tool has a mechanical rule: emit the
parts' markers in declaration order and close with endPart.
"""
import collections
import glob
import re
import xml.etree.ElementTree as ET

XSI = '{http://www.w3.org/2001/XMLSchema-instance}type'
BS = chr(92)
CTRL = re.compile(BS + BS + r"[a-zA-Z]+-?[0-9]*[ ]?")
HEXE = re.compile(BS + BS + r"'[0-9a-fA-F]{2}")
MARK = re.compile(r"%" + re.escape('[') + r"([^" + re.escape(']') + r"]*)" + re.escape(']'))


def lo(t):
    return t.rsplit('}', 1)[-1]


def ch(e, n):
    for c in e:
        if lo(c.tag) == n:
            return c
    return None


def destyle(s):
    s = HEXE.sub(' ', s)
    s = CTRL.sub('', s)
    return s.replace('{', '').replace('}', '')


def main():
    res = collections.Counter()
    bad_order = []
    bad_end = []
    bad_nest = []

    for f in sorted(glob.glob('output/iso-packages/commercial-auto-2607/Templates/*.gd')):
        r = ET.parse(f).getroot()
        doc = ch(r, 'document')
        rtf = destyle(ch(ch(doc, 'content'), 'rtf').text or '')
        pos = {}
        for m in MARK.finditer(rtf):
            pos.setdefault(m.group(1), m.start())
        mk = ch(doc, 'markup')

        def walk(instructions, parent_span):
            for instr in instructions:
                if lo(instr.tag) != 'instruction':
                    continue
                itype = instr.get(XSI, '')
                iid = instr.get('ID')
                parts = ch(instr, 'parts')
                if parts is None:
                    if iid in pos and parent_span:
                        lo_, hi_ = parent_span
                        res['leaf inside parent span' if lo_ <= pos[iid] <= hi_
                            else 'LEAF OUTSIDE PARENT SPAN'] += 1
                    continue

                plist = [p for p in parts if lo(p.tag) == 'part' and p.get('ID')]
                marks = [(p, pos.get(p.get('ID'))) for p in plist]
                if any(v is None for _p, v in marks):
                    res['container with an unplaced part'] += 1
                    continue

                offs = [v for _p, v in marks]
                if offs != sorted(offs):
                    res['PART MARKERS OUT OF DECLARATION ORDER'] += 1
                    bad_order.append((f, iid, itype))
                else:
                    res['part markers in declaration order'] += 1

                ends = [p for p, _v in marks if p.get(XSI) == 'endPartType']
                if ends:
                    end_off = pos[ends[-1].get('ID')]
                    if end_off == max(offs):
                        res['endPart marker is last'] += 1
                    else:
                        res['ENDPART NOT LAST'] += 1
                        bad_end.append((f, iid, itype))

                span = (min(offs), max(offs))
                if parent_span:
                    lo_, hi_ = parent_span
                    if lo_ <= span[0] and span[1] <= hi_:
                        res['container nested inside parent span'] += 1
                    else:
                        res['CONTAINER NOT NESTED IN PARENT'] += 1
                        bad_nest.append((f, iid, itype))

                for p in plist:
                    inner = ch(p, 'instructions')
                    if inner is not None:
                        walk(inner, span)

        top = ch(ch(mk, 'markup'), 'instructions') if ch(mk, 'markup') is not None else None
        if top is not None:
            walk(top, None)

    for k, v in sorted(res.items()):
        flag = '  <-- VIOLATION' if k.isupper() or k[:4].isupper() else ''
        print(f'{v:9d}  {k}{flag}')
    for label, rows in (('order', bad_order), ('endPart', bad_end), ('nesting', bad_nest)):
        if rows:
            print(f'\nfirst {label} violations:')
            for f, iid, itype in rows[:5]:
                print(f'   {f.rsplit(chr(92), 1)[-1][:60]}  ID={iid} {itype}')


if __name__ == '__main__':
    main()
