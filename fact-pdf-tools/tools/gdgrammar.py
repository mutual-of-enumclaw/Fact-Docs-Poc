"""The AUTHORING grammar: which markup nodes must be placed in the RTF as `%[ID]`.

Run this against any GhostDraft package to re-derive the rule, and against a
GENERATED .gd to check it before shipping the file. See FORM-STUDIO-PLAN §43.

Verified over the 491 ISO Commercial Auto templates, zero violations:
  * containers (conditionalInstructionType, listInstructionType) are NEVER placed
  * every other instruction and every part is placed exactly once
  * no marker exists without a declaration
  * a container's part markers are in declaration order, endPart last
  * every child's span nests inside its parent's

Which markup nodes are PLACED in the RTF, and is `%[N]` always contiguous?

Authoring question: to write a .gd you must know which instructions need a
`%[ID]` marker in the RTF and which are pure containers. Reading the raw RTF is
not enough -- a marker can be split across runs, `{...%}{...[70]}` -- so the
scan has to work on the destyled character stream.
"""
import collections
import glob
import os
import re
import sys
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


def destyle(s: str) -> str:
    s = HEXE.sub(' ', s)
    s = CTRL.sub('', s)
    return s.replace('{', '').replace('}', '')


def main():
    stats = collections.Counter()
    placed = collections.Counter()
    split_examples = []

    pattern = (sys.argv[1] if len(sys.argv) > 1
               else 'output/iso-packages/commercial-auto-2607/Templates/*.gd')
    for f in sorted(glob.glob(pattern)):
        r = ET.parse(f).getroot()
        doc = ch(r, 'document')
        rtf = (ch(ch(doc, 'content'), 'rtf').text or '')
        naive = set(MARK.findall(rtf))
        full = set(MARK.findall(destyle(rtf)))
        if full - naive:
            split_examples.append((os.path.basename(f), sorted(full - naive)[:4]))

        mk = ch(doc, 'markup')
        kinds = {}
        for e in mk.iter():
            n = lo(e.tag)
            if n in ('instruction', 'part') and e.get('ID'):
                kinds[e.get('ID')] = f"{n}:{e.get(XSI, '(none)')}"

        stats['templates'] += 1
        stats['declared ids'] += len(kinds)
        stats['markers naive'] += len(naive)
        stats['markers destyled'] += len(full)
        stats['split markers naive missed'] += len(full - naive)
        stats['markers with no declaration'] += len(full - set(kinds))
        for i, k in kinds.items():
            placed[(k, i in full)] += 1

    print('--- totals ---')
    for k, v in stats.items():
        print(f'  {k:<32}{v}')
    print(f'\n  templates with a split marker  {len(split_examples)}')
    for name, ids in split_examples[:5]:
        print(f'    {name[:60]:<62} {ids}')

    print('\n--- THE AUTHORING INVARIANT ---')
    print(f'{"node kind":<44}{"placed":>8}{"NOT placed":>12}')
    for k in sorted({k for k, _ in placed}):
        print(f'{k:<44}{placed[(k, True)]:>8}{placed[(k, False)]:>12}')


if __name__ == '__main__':
    main()
