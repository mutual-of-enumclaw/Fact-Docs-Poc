"""Repackage a `.gdsp` with authored templates added, so GhostDraft can resolve them.

Why this is necessary
---------------------
A `.gd` never carries its own model. Across all 570 production templates in both
packages, `<library>` is `xsi:nil="true"` without exception -- zero
counter-examples -- so there is no such thing as embedding a concept library in
a template, and "mock a model inside the file" is not a shape GhostDraft has.

A template's `<domainmodels>` DECLARES which model roots it uses (section 45),
but the library those declarations name has to exist in the workspace. Open a
`.gd` standalone and Designer shows `Model Library (missing)` and red-underlines
every path -- not because the paths are wrong, but because it has nothing to
resolve them against.

So the way to hand someone an authored template that resolves is to give them a
PACKAGE: the original `.gdsp` with the new template dropped into `Templates/`.
The concept library, style libraries, `model.xml` and test cases all come along,
and nothing is missing.

Usage:
    python tools/gdpackage.py <extracted-package-dir> out.gdsp file1.gd [file2.gd ...]
    python tools/gdpackage.py <dir> out.gdsp mine.gd --catalog "WIP Form Templates"
"""

from __future__ import annotations

import argparse
import os
import shutil
import xml.etree.ElementTree as ET
import zipfile


def add_to_catalog(catalog_path: str, doc_names: list[str], catalog_name: str | None) -> bool:
    """Register the documents in a template catalog, if one is named.

    Optional: a template resolves and renders without a catalog entry -- the
    catalog is what lists it for assembly. Left off by default so the emitted
    package differs from the original in exactly one way.
    """
    try:
        tree = ET.parse(catalog_path)
    except (ET.ParseError, FileNotFoundError):
        return False
    root = tree.getroot()
    ns = 'http://schemas.korbitec.com/GhostDraft/TemplateCatalogs/1.0'
    ET.register_namespace('', ns)
    changed = False
    for cat in root.iter():
        if cat.tag.rsplit('}', 1)[-1] != 'templateCatalog':
            continue
        if catalog_name and cat.get('name') != catalog_name:
            continue
        existing = {c.get('document') for c in cat}
        for name in doc_names:
            if name in existing:
                continue
            el = ET.SubElement(cat, '{%s}item' % ns)
            el.set('document', name)
            el.set('type', 'document')
            el.set('outputFormat', '')
            el.set('passwordDocument', '')
            changed = True
        break
    if changed:
        tree.write(catalog_path, encoding='utf-8', xml_declaration=True)
    return changed


def build(package_dir: str, out_gdsp: str, templates: list[str],
          catalog_name: str | None = None) -> int:
    if not os.path.isfile(os.path.join(package_dir, 'model.xml')):
        print(f'{package_dir} has no model.xml -- not an extracted package')
        return 1

    staging = out_gdsp + '.staging'
    if os.path.isdir(staging):
        shutil.rmtree(staging)
    shutil.copytree(package_dir, staging)

    tdir = os.path.join(staging, 'Templates')
    os.makedirs(tdir, exist_ok=True)
    added = []
    for t in templates:
        name = os.path.basename(t)
        shutil.copy2(t, os.path.join(tdir, name))
        added.append(os.path.splitext(name)[0])
    print(f'added {len(added)} template(s): {", ".join(added)}')

    if catalog_name:
        cdir = os.path.join(staging, 'Template Catalogs')
        for f in os.listdir(cdir) if os.path.isdir(cdir) else []:
            if add_to_catalog(os.path.join(cdir, f), added, catalog_name):
                print(f'registered in catalog {catalog_name!r} ({f})')
                break
        else:
            print(f'catalog {catalog_name!r} not found -- template added but unregistered')

    # A .gdsp is a plain zip with the package tree at its root.
    if os.path.exists(out_gdsp):
        os.remove(out_gdsp)
    count = 0
    with zipfile.ZipFile(out_gdsp, 'w', zipfile.ZIP_DEFLATED) as z:
        for root, _dirs, files in os.walk(staging):
            for f in files:
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, staging).replace(os.sep, '/'))
                count += 1
    shutil.rmtree(staging)
    size = os.path.getsize(out_gdsp)
    print(f'wrote {out_gdsp}  ({count} entries, {size / 1e6:.1f} MB)')
    return 0


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument('package')
    ap.add_argument('out')
    ap.add_argument('templates', nargs='+')
    ap.add_argument('--catalog', default=None,
                    help='also register the templates in this template catalog')
    a = ap.parse_args()
    return build(a.package, a.out, a.templates, a.catalog)


if __name__ == '__main__':
    raise SystemExit(main())
