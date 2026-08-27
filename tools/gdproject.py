"""Register an authored `.gd` in a GhostDraft Studio project (`.gdproj`).

Which file does Studio open?
---------------------------
Not a `.gdsp`. Scanning `GhostDraftStudio.exe` for the extensions it knows gives
`.gd .gdm .gds .gdproj .gdbund .gdp .gdpk .gdmpk .gdspk .gdrp .gduser` -- and
`.gdsp` is NOT among them. A `.gdsp` is a Packager output
(`GhostDraft.Server.Packager.exe`, in Server Client Tools) destined for the
composition server; its `Info.txt` records a `CompositionServerVersion` and a
`DocumentService`, not a project.

What Studio opens is a `.gdproj`, whose folder layout is:

    <Project>.gdproj
    Documents/<folder>/*.gd                    the templates
    Resources/Model Libraries/*.gdm            the concept library
    Resources/Style Libraries/*.gds
    Resources/{Scenarios,Stationery,Schedule Overflows}/
    *.catalog

`<documentPaths><documentPath><autoDiscover>` is **false**, so Studio does NOT
pick up a `.gd` merely dropped into `Documents/`. It has to be registered in the
project manifest, which is what this does:

    <document>
      <path>Documents\\Proprietary\\Name.gd</path>
      <name>Name</name>
      <guid>…</guid>
      <included>true</included>
      <projectFolder>Documents\\Proprietary</projectFolder>
      <documentPath />
      <documentType>Document</documentType>
      <lockable>true</lockable>
    </document>

Usage:
    python tools/gdproject.py <project.gdproj> <file.gd> [--folder Documents/Proprietary]
    python tools/gdproject.py <project.gdproj> <file.gd> --copy-to <dir>
"""

from __future__ import annotations

import argparse
import hashlib
import os
import shutil
import xml.etree.ElementTree as ET

DOC_FIELDS = ('path', 'name', 'guid', 'included', 'projectFolder',
              'documentPath', 'documentType', 'lockable')


def _lo(tag: str) -> str:
    return tag.rsplit('}', 1)[-1]


def _find(root, name):
    for e in root.iter():
        if _lo(e.tag) == name:
            return e
    return None


def _stable_guid(seed: str) -> str:
    """Deterministic, so re-running does not churn the manifest."""
    h = hashlib.sha256(seed.encode('utf-8')).hexdigest()
    return f'{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}'


def register(gdproj: str, gd_file: str, folder: str, dry_run: bool = False) -> int:
    project_dir = os.path.dirname(os.path.abspath(gdproj))
    name = os.path.splitext(os.path.basename(gd_file))[0]
    rel_folder = folder.replace('/', os.sep).strip(os.sep)
    rel_path = os.path.join(rel_folder, os.path.basename(gd_file))

    tree = ET.parse(gdproj)
    root = tree.getroot()
    ns = ''
    if root.tag.startswith('{'):
        ns = root.tag[1:root.tag.index('}')]
        ET.register_namespace('', ns)

    docs = _find(root, 'documents')
    if docs is None:
        print('no <documents> element -- is this a .gdproj?')
        return 1

    existing = {}
    for d in docs:
        p = _find(d, 'path')
        n = _find(d, 'name')
        if p is not None and p.text:
            existing[p.text.strip().lower()] = (n.text if n is not None else '')

    if rel_path.lower() in existing:
        print(f'already registered: {rel_path}')
        return 0

    def q(t):
        return f'{{{ns}}}{t}' if ns else t

    el = ET.SubElement(docs, q('document'))
    values = {
        'path': rel_path,
        'name': name,
        'guid': _stable_guid(f'{os.path.basename(gdproj)}|{rel_path}'),
        'included': 'true',
        'projectFolder': rel_folder,
        'documentPath': '',
        'documentType': 'Document',
        'lockable': 'true',
    }
    for f in DOC_FIELDS:
        ET.SubElement(el, q(f)).text = values[f] or None

    target = os.path.join(project_dir, rel_path)
    print(f'project   {gdproj}')
    print(f'documents {len(docs) - 1} -> {len(docs)}')
    print(f'adding    {rel_path}   guid={values["guid"]}')
    print(f'copying   {gd_file}')
    print(f'       -> {target}')
    if dry_run:
        print('\n(dry run -- nothing written)')
        return 0

    os.makedirs(os.path.dirname(target), exist_ok=True)
    shutil.copy2(gd_file, target)
    tree.write(gdproj, encoding='utf-8', xml_declaration=True)
    print('\nwritten. Open the .gdproj in GhostDraft Studio.')
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('gdproj')
    ap.add_argument('gd_file')
    ap.add_argument('--folder', default='Documents/Proprietary',
                    help='project folder for the template (default Documents/Proprietary)')
    ap.add_argument('--dry-run', action='store_true',
                    help='print exactly what would change and write nothing')
    a = ap.parse_args()
    return register(a.gdproj, a.gd_file, a.folder, a.dry_run)


# Deliberately NOT offered: a --copy-to that clones the whole project first.
# A real project's deepest relative path is ~160 characters
# (Resources/Stationery/Headers & Footers/Header Policy Number (R) LOB and ...gd)
# and Windows caps paths at 260, so cloning under any non-trivial destination
# fails partway through and leaves a project that is silently missing its
# stationery. Use --dry-run against the real project instead; it writes nothing.


if __name__ == '__main__':
    raise SystemExit(main())
