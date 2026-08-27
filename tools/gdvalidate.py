"""Validate a GhostDraft Studio project with GhostDraft's OWN compiler.

`CreateSnapshot.exe` (shipped in GhostDraft Studio, and one of only two console
apps in the whole install) takes a `.gdproj` and a destination folder, compiles
the project, and **checks every template's markup against the concept library**.
It is the only GhostDraft-authored correctness gate that can be run without a
GUI, which makes it the right check to run on an authored template before asking
a human to look at anything.

    CreateSnapshot "<Project File Path>" "<Destination Folder>"

Two things it gives us beyond pass/fail:

  * per-template markup errors, e.g.
        The markup of document "EA9940 0194" is invalid.
        'FarmFamilyCorporationsorPartnerships of MOECAPolicyLevelCoverages
         Has 8 or more' is not in the model.
  * a CANONICALISED copy of every template. GhostDraft rewrites what it does not
    like into what it wants, so diffing our input against the snapshot output is
    GhostDraft telling us its own house style. That is how sections 43.7's open
    questions were closed: the `<explanation>` blob is GENERATED (we need not
    author one), `<description>` is derived and rewritten
    (`Policy > Policy Number` -> `Policy Number of Policy`), the ten system
    properties are filled in, and instruction IDs are RENUMBERED -- so
    contiguity and ordering of our IDs do not matter.

What this is NOT: a render. Every GhostDraft UI is a GUI app (Studio,
Server.TestClient, Data Workbench), so a PDF cannot be produced locally from a
shell. Rendering goes through `GhostDraftServer/RestAPI/AssembleDocument` on the
server, which needs credentials and a PUBLISHED template.

Usage:
    python tools/gdvalidate.py "<project.gdproj>" [--out output/snapshot]
    python tools/gdvalidate.py "<project.gdproj>" --diff "My Template"
"""

from __future__ import annotations

import argparse
import difflib
import glob
import os
import re
import shutil
import subprocess
import sys

CREATE_SNAPSHOT = (r'C:\Program Files (x86)\GhostDraft\GhostDraft Studio'
                   r'\CreateSnapshot.exe')


def run_snapshot(gdproj: str, out_dir: str, timeout: int = 600
                 ) -> tuple[int, str, str | None]:
    """(exit code, combined output, snapshot folder or None)."""
    if not os.path.exists(CREATE_SNAPSHOT):
        print(f'not found: {CREATE_SNAPSHOT}')
        return 127, '', None
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir, exist_ok=True)
    proc = subprocess.run([CREATE_SNAPSHOT, os.path.abspath(gdproj),
                           os.path.abspath(out_dir)],
                          capture_output=True, text=True, timeout=timeout)
    text = (proc.stdout or '') + (proc.stderr or '')
    inner = [d for d in glob.glob(os.path.join(out_dir, '*')) if os.path.isdir(d)]
    return proc.returncode, text, (inner[0] if inner else None)


def parse_errors(text: str) -> dict[str, list[str]]:
    """document name -> its complaints, in the order reported."""
    out: dict[str, list[str]] = {}
    current = None
    for line in text.splitlines():
        line = line.strip()
        if not line:
            continue
        m = re.match(r'The markup of document "(.+?)" is invalid\.', line)
        if m:
            current = m.group(1)
            out.setdefault(current, [])
            continue
        if current is not None:
            if line not in out[current]:
                out[current].append(line)
        else:
            out.setdefault('<project>', []).append(line)
    return out


def canonical_diff(source_gd: str, snapshot_dir: str, keep_rtf: bool = False) -> None:
    """What GhostDraft changed about our template -- its house style, stated."""
    name = os.path.basename(source_gd)
    target = os.path.join(snapshot_dir, name)
    if not os.path.exists(target):
        print(f'{name} is not in the snapshot')
        return
    a = open(source_gd, encoding='utf-8').read()
    b = open(target, encoding='utf-8').read()

    def strip(s):
        return s if keep_rtf else re.sub(r'<(rtf|explanation)>.*?</\1>',
                                         r'<\1>[BLOB]</\1>', s, flags=re.S)

    print(f'{name}: source {len(a):,} chars -> snapshot {len(b):,} '
          f'({len(b) - len(a):+,})')
    d = list(difflib.unified_diff(strip(a).splitlines(), strip(b).splitlines(),
                                  'ours', 'ghostdraft', lineterm='', n=1))
    print('\n'.join(d) if d else 'identical outside the RTF blobs')


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('gdproj')
    ap.add_argument('--out', default='output/snapshot')
    ap.add_argument('--diff', default=None,
                    help='after validating, diff this template (by .gd path or '
                         'name) against GhostDraft\'s canonicalised copy')
    ap.add_argument('--keep-rtf', action='store_true')
    a = ap.parse_args()

    code, text, snap = run_snapshot(a.gdproj, a.out)
    errors = parse_errors(text)

    print(f'CreateSnapshot exit {code}')
    print(f'snapshot folder      {snap or "(none produced)"}')
    if snap:
        gds = glob.glob(os.path.join(snap, '*.gd'))
        print(f'templates compiled   {len(gds)}')
    print()
    if not errors:
        print('no markup errors reported')
    else:
        print(f'documents with complaints: {len(errors)}')
        for doc, msgs in errors.items():
            print(f'\n  {doc}')
            for m in msgs:
                print(f'      {m}')
    print()
    if text.strip() and not errors:
        print('--- raw output ---')
        print(text[:2000])

    if a.diff and snap:
        print()
        src = a.diff if a.diff.endswith('.gd') else None
        if src is None:
            cand = glob.glob(os.path.join('output', a.diff + '.gd'))
            src = cand[0] if cand else None
        if src and os.path.exists(src):
            canonical_diff(src, snap, a.keep_rtf)
        else:
            print(f'cannot find a local source .gd for {a.diff!r}')

    # A validation that cannot fail is worthless: a non-zero exit or any parsed
    # complaint is a real signal, so propagate it.
    return 1 if errors else 0


if __name__ == '__main__':
    sys.exit(main())
