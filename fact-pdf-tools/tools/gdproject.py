"""Register an authored `.gd` in a GhostDraft Studio project (`.gdproj`).

Which file does Studio open?
---------------------------
Not a `.gdsp`. Scanning `GhostDraftStudio.exe` for the extensions it knows gives
`.gd .gdm .gds .gdproj .gdbund .gdp .gdpk .gdmpk .gdspk .gdrp .gduser` -- and
`.gdsp` is NOT among them. A `.gdsp` is a Packager output
(`GhostDraft.Server.Packager.exe`, in Server Client Tools) destined for the
composition server. Studio opens a `.gdproj`, laid out as:

    <Project>.gdproj
    Documents/<folder>/*.gd                    the templates
    Resources/Model Libraries/*.gdm            the concept library
    Resources/Style Libraries/*.gds
    *.catalog

`<documentPaths><documentPath><autoDiscover>` is **false**, so Studio does not
pick up a `.gd` dropped into `Documents/`. It must be listed in the manifest.

Why this edits TEXT and not an ElementTree
------------------------------------------
The first version parsed the `.gdproj` with ElementTree and re-serialised it.
That produced a file Studio silently ignored, in two ways:

  * the manifest declares its default `xmlns` **per element**
    (`<temporaryStorageIdentifier xmlns="...GhostBridge/1.0">`), not on the root.
    ElementTree hoisted it into a prefixed `ns0:` form and rewrote every element
    in the file -- and because the ROOT `<Content>` carries no namespace, the
    element this tool added landed in NO namespace at all while its 83 siblings
    were now `ns0:`. Studio was right to ignore it.
  * a real entry carries `<artifactStatusGuid>` (the status column in Studio's
    document list) and `<lockable>false</lockable>`. The added entry had neither.

So: copy an existing sibling's exact shape and splice the new block in as text.
Everything else in the file stays byte-for-byte identical, which is verified
after writing rather than assumed.

Usage:
    python tools/gdproject.py <project.gdproj> <file.gd> [--folder Documents/Proprietary]
    python tools/gdproject.py <project.gdproj> <file.gd> --status New --dry-run
"""

from __future__ import annotations

import argparse
import hashlib
import os
import re
import shutil
import xml.etree.ElementTree as ET


def _stable_guid(seed: str) -> str:
    """Deterministic, so re-running does not churn the manifest."""
    h = hashlib.sha256(seed.encode('utf-8')).hexdigest()
    return f'{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}'


def _documents_span(text: str) -> tuple[int, int]:
    """(start, end) of the <documents> element's inner content."""
    m = re.search(r'<documents\b[^>]*>', text)
    if not m:
        raise ValueError('no <documents> element -- is this a .gdproj?')
    close = text.index('</documents>', m.end())
    return m.end(), close


def _sibling_blocks(text: str) -> list[str]:
    lo, hi = _documents_span(text)
    return re.findall(r'<document>.*?</document>', text[lo:hi], re.S)


def status_guid(text: str, name: str) -> str | None:
    for m in re.finditer(r'<artifactStatus>(.*?)</artifactStatus>', text, re.S):
        blk = m.group(1)
        n = re.search(r'<name>(.*?)</name>', blk, re.S)
        g = re.search(r'<guid>(.*?)</guid>', blk, re.S)
        if n and g and n.group(1).strip() == name:
            return g.group(1).strip()
    return None


def build_block(sibling: str, rel_path: str, name: str, guid: str,
                folder: str, art_guid: str | None) -> str:
    """A new <document> block with the sibling's exact field order and indent."""
    out = sibling
    subs = {
        'path': rel_path,
        'name': name,
        'guid': guid,
        'included': 'true',
        'projectFolder': folder,
    }
    for tag, val in subs.items():
        out = re.sub(r'<%s>.*?</%s>' % (tag, tag),
                     lambda _m, t=tag, v=val: f'<{t}>{v}</{t}>', out, count=1, flags=re.S)
    if art_guid and '<artifactStatusGuid>' in out:
        out = re.sub(r'<artifactStatusGuid>.*?</artifactStatusGuid>',
                     f'<artifactStatusGuid>{art_guid}</artifactStatusGuid>',
                     out, count=1, flags=re.S)
    return out


def register(gdproj: str, gd_file: str, folder: str, status: str,
             dry_run: bool = False) -> int:
    project_dir = os.path.dirname(os.path.abspath(gdproj))
    name = os.path.splitext(os.path.basename(gd_file))[0]
    rel_folder = folder.replace('/', '\\').strip('\\')
    rel_path = f'{rel_folder}\\{os.path.basename(gd_file)}'

    # newline='' keeps CRLF verbatim. Reading in text mode and writing back
    # with newline='' converts the whole file to LF -- 1,355 silent line-ending
    # changes here -- which makes 'nothing else changed' false at the byte level.
    with open(gdproj, encoding='utf-8', newline='') as fh:
        original = fh.read()
    blocks = _sibling_blocks(original)
    if not blocks:
        print('no existing <document> entries to copy the shape from')
        return 1

    if re.search(r'<path>%s</path>' % re.escape(rel_path), original):
        print(f'already registered: {rel_path}')
        return 0

    # Prefer a sibling in the SAME project folder, so the copied shape matches
    # whatever conventions that folder uses.
    same = [b for b in blocks
            if re.search(r'<projectFolder>%s</projectFolder>' % re.escape(rel_folder), b)]
    sibling = (same or blocks)[-1]
    sib_name = re.search(r'<name>(.*?)</name>', sibling, re.S)

    art = status_guid(original, status)
    block = build_block(sibling, rel_path, name,
                        _stable_guid(f'{os.path.basename(gdproj)}|{rel_path}'),
                        rel_folder, art)

    CR, LF = chr(13), chr(10)
    eol = CR + LF if CR + LF in original else LF
    lo, hi = _documents_span(original)
    tail = original[lo:hi]
    last = tail.rindex('</document>') + len('</document>')
    # indentation of the last sibling's opening tag, so the insert lines up
    indent = re.match(r'[ \t]*', tail[tail.rindex(LF, 0, tail.rindex('<document>')) + 1:]) \
        if LF in tail else None
    pad = indent.group(0) if indent else '      '
    block = block.replace(CR + LF, LF).replace(LF, eol)
    updated = original[:lo] + tail[:last] + eol + pad + block + tail[last:] + original[hi:]

    target = os.path.join(project_dir, rel_path.replace('\\', os.sep))
    print(f'project    {gdproj}')
    print(f'documents  {len(blocks)} -> {len(blocks) + 1}')
    print(f'shape from {sib_name.group(1) if sib_name else "?"}'
          f'   (same folder: {bool(same)})')
    print(f'status     {status} = {art or "NOT FOUND -- entry will keep the sibling status"}')
    print(f'adding     {rel_path}')
    print(f'copying    -> {target}')

    if dry_run:
        print(f'\n--- the block that would be inserted ---\n{block}\n\n(dry run)')
        return 0

    # ---- verify BEFORE writing: the only change may be the inserted block
    assert updated[:lo + last] == original[:lo + last], 'text before the insert changed'
    assert updated[lo + last + len(eol) + len(pad) + len(block):] == original[lo + last:], \
        'text after the insert changed'
    root = ET.fromstring(updated)                     # must still parse
    ndocs = len([e for e in root.iter() if e.tag.rsplit('}', 1)[-1] == 'document'])
    assert ndocs == len(blocks) + 1, f'expected {len(blocks) + 1} documents, got {ndocs}'

    backup = gdproj + '.bak'
    if not os.path.exists(backup):
        shutil.copy2(gdproj, backup)
        print(f'backup     {os.path.basename(backup)}')

    os.makedirs(os.path.dirname(target), exist_ok=True)
    shutil.copy2(gd_file, target)
    with open(gdproj, 'w', encoding='utf-8', newline='') as fh:
        fh.write(updated)
    print(f'\nwritten -- {len(updated) - len(original)} bytes added, nothing else changed.')
    print('Open the .gdproj in GhostDraft Studio.')
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('gdproj')
    ap.add_argument('gd_file')
    ap.add_argument('--folder', default='Documents/Proprietary')
    ap.add_argument('--status', default='New',
                    help='artifact status name from the project (default New)')
    ap.add_argument('--dry-run', action='store_true')
    a = ap.parse_args()
    return register(a.gdproj, a.gd_file, a.folder, a.status, a.dry_run)


# Deliberately NOT offered: a --copy-to that clones the whole project first.
# A real project's deepest relative path is ~160 characters
# (Resources/Stationery/Headers & Footers/Header Policy Number (R) LOB and ...gd)
# and Windows caps paths at 260, so cloning under any non-trivial destination
# fails partway through and leaves a project silently missing its stationery.


if __name__ == '__main__':
    raise SystemExit(main())
