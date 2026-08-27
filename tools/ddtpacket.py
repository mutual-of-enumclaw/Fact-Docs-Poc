#!/usr/bin/env python3
"""
ddtpacket.py -- recover the Documaker packet-assembly graph from FORM.DAT + the DDTs.

Until now the plan said "there is no assembler yet" (HANDOFF-QUOTE-POC.md, Phase 1
step 7).  There is: DocProd's own assembly instructions are sitting in two places we
had never read.

  1. FORM.DAT gives the *ordered top-level image list* for a form entry:

        ;MOE;CPP;QUOTE CPPSUM.1;;RD;;QTE_HDR|D3SOX<AGT(0)>/QCPPSUM_HDR|D3S<AGT(0)>/...;

     (a trailing backslash continues the entry on the next line)

  2. Each image's DDT carries an `<Image Rules>` section whose `PNTAddImgAfterCurImg`
     lines name the *data-driven children* to splice in after it, together with the
     extract table and the column filter that decides how many times:

        ;SetOrigin;Rel+0,Max+0;
        ;PNTAddImgAfterCurImg;MOE,OVERFLOWS,OVFORMS,QCPPSUM_CA,1,TBLOFF,PMSP0200|TBLOFF,QCOV,INSLINE,(CA,AS);

     `SetOrigin Rel+0,Max+0` is the stacking rule -- each image is placed relative to
     the running maximum Y, i.e. flowed underneath its predecessor.

Together those are a complete, machine-readable packet definition.  This tool reads
them and prints/exports the expanded tree so the HTML and GhostDraft assemblers can
be driven from the same source instead of a hand-written list.

Usage
    # every quote packet entry for a LOB
    python tools/ddtpacket.py --lob CPP --form "QUOTE CPPSUM.1"
    python tools/ddtpacket.py --lob CPP --list
    python tools/ddtpacket.py --lob CPP --form "QUOTE CPPCA.1" --csv output/packet-cppca.csv

    # the whole quote packet, in page order
    python tools/ddtpacket.py --lob CPP --packet quote --csv output/packet-cpp-quote.csv
"""

from __future__ import annotations

import argparse
import csv
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Set

MSTRRES = r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0"
DEFAULT_FORMDAT = os.path.join(MSTRRES, "DEFLIB", "form.DAT")
DEFAULT_DDTDIR = os.path.join(MSTRRES, "DDTLIB")
DEFAULT_FAPDIR = os.path.join(MSTRRES, "FORMS")


# --------------------------------------------------------------------------- FORM.DAT


@dataclass
class FormEntry:
    company: str
    lob: str
    name: str
    placements: List[tuple[str, str]]   # (image, flags) in FORM.DAT order
    line: int

    @property
    def images(self) -> List[str]:
        return [i for i, _ in self.placements]

    @property
    def key(self) -> str:
        return f"{self.company}/{self.lob}/{self.name}"


def read_formdat(path: str) -> List[FormEntry]:
    """Parse FORM.DAT into entries, honouring backslash continuations."""
    with open(path, "r", encoding="latin-1") as fh:
        raw = fh.read().splitlines()

    # Join continuations first; a record is one logical line.
    logical: List[tuple[int, str]] = []
    buf: Optional[str] = None
    start = 0
    for n, line in enumerate(raw, start=1):
        stripped = line.rstrip("\r\n")
        if buf is None:
            if not stripped.startswith(";"):
                continue
            start = n
            buf = ""
        if stripped.endswith("\\"):
            buf += stripped[:-1]
        else:
            buf += stripped
            logical.append((start, buf))
            buf = None
    if buf is not None:
        logical.append((start, buf))

    entries: List[FormEntry] = []
    for lineno, rec in logical:
        # ;COMPANY;LOB;NAME;?;RD;;IMAGES;
        parts = rec.split(";")
        if len(parts) < 8:
            continue
        company, lob, name = parts[1], parts[2], parts[3]
        images_blob = parts[7]
        if not images_blob:
            continue
        # Each segment is `IMAGE|FLAGS`, e.g. `QTE_HDR|D3SOX<AGT(0)>`. The flags
        # carry the pagination role: OX = repeat as the overflow HEADER on every
        # page, OY = repeat as the overflow FOOTER.
        placements: List[tuple[str, str]] = []
        for seg in images_blob.split("/"):
            seg = seg.strip()
            if not seg:
                continue
            image, _, flags = seg.partition("|")
            image = image.strip()
            if image:
                placements.append((image, flags.strip()))
        if not placements:
            continue
        entries.append(FormEntry(company, lob, name, placements, lineno))
    return entries


# ------------------------------------------------------------------------------- DDT


@dataclass
class TableSpec:
    """`TBLOFF,QCOV,INSLINE,(CA,AS)` -- the extract table and the filter on it."""
    table: str
    filters: List[tuple[str, str]] = field(default_factory=list)

    def __str__(self) -> str:
        if not self.filters:
            return self.table
        conds = " and ".join(f"{c}{_op(v)}" for c, v in self.filters)
        return f"{self.table}[{conds}]"


def _op(value: str) -> str:
    if value.startswith("~"):
        return f" != {value[1:]}"
    if value.startswith("(") and value.endswith(")"):
        return f" in ({value[1:-1]})"
    return f" = {value}"


@dataclass
class ChildImage:
    image: str
    count: str
    tables: List[TableSpec]

    @property
    def driver(self) -> Optional[TableSpec]:
        """The table that decides the repetition -- the last spec, which is the
        one carrying the filter when there are two."""
        with_filters = [t for t in self.tables if t.filters]
        if with_filters:
            return with_filters[-1]
        return self.tables[-1] if self.tables else None


@dataclass
class ImageRules:
    name: str
    dimensions: Optional[str] = None
    origin: Optional[str] = None
    extract_files: Optional[str] = None
    children: List[ChildImage] = field(default_factory=list)
    found: bool = True

    # Parsed geometry, all in FAP units (1/2400").
    # `;SetImageDimensions;98,0,<height>,<width>,<yOffset>,<xOffset>,...;`
    # matches the FAP's own `H,` tuple `(xOffset,yOffset,height,width)`.
    height: int = 0
    width: int = 0
    y_offset: int = 0
    x_offset: int = 0
    # `;SetOrigin;<xMode>+<xVal>,<yMode>+<yVal>;` -- Rel / Max / Abs.
    origin_x_mode: str = "Rel"
    origin_x_val: int = 0
    origin_y_mode: str = "Max"
    origin_y_val: int = 0

    def _set_dimensions(self, args: str) -> None:
        toks = [t.strip() for t in args.split(",")]
        def num(i: int) -> int:
            try:
                return int(toks[i])
            except (IndexError, ValueError):
                return 0
        self.height, self.width = num(2), num(3)
        self.y_offset, self.x_offset = num(4), num(5)

    def _set_origin(self, args: str) -> None:
        parts = [p.strip() for p in args.split(",")]
        for i, part in enumerate(parts[:2]):
            m = re.match(r"([A-Za-z]+)\s*([+-]\s*\d+)?", part)
            if not m:
                continue
            mode = m.group(1)
            val = int((m.group(2) or "0").replace(" ", ""))
            if i == 0:
                self.origin_x_mode, self.origin_x_val = mode, val
            else:
                self.origin_y_mode, self.origin_y_val = mode, val


def _parse_tablespecs(blob: str) -> List[TableSpec]:
    specs: List[TableSpec] = []
    for chunk in blob.split("|"):
        toks = [t.strip() for t in chunk.split(",") if t.strip() != ""]
        # Re-join a parenthesised value list that the comma split tore apart:
        # "(CA" , "AS)"  ->  "(CA,AS)"
        merged: List[str] = []
        for tok in toks:
            if merged and merged[-1].count("(") > merged[-1].count(")"):
                merged[-1] = merged[-1] + "," + tok
            else:
                merged.append(tok)
        toks = merged
        if not toks:
            continue
        if toks[0].upper() == "TBLOFF":
            toks = toks[1:]
        if not toks:
            continue
        table = toks[0]
        rest = toks[1:]
        filters = [(rest[i], rest[i + 1]) for i in range(0, len(rest) - 1, 2)]
        specs.append(TableSpec(table, filters))
    return specs


def read_ddt(ddtdir: str, image: str) -> ImageRules:
    path = os.path.join(ddtdir, image + ".DDT")
    if not os.path.exists(path):
        return ImageRules(image, found=False)

    rules = ImageRules(image)
    in_rules = False
    with open(path, "r", encoding="latin-1") as fh:
        for line in fh:
            s = line.strip()
            if s.startswith("<"):
                in_rules = s.lower().startswith("<image rules>")
                continue
            if not in_rules or not s.startswith(";"):
                continue
            body = s.strip(";")
            if not body:
                continue
            verb, _, args = body.partition(";")
            verb = verb.strip()
            args = args.strip().rstrip(";")
            low = verb.lower()
            if low == "setimagedimensions":
                rules.dimensions = args
                rules._set_dimensions(args)
            elif low == "setorigin":
                rules.origin = args
                rules._set_origin(args)
            elif low == "csccsetextrfile":
                rules.extract_files = args
            elif low == "pntaddimgaftercurimg":
                # NOTE: these are collected in file order here and REVERSED at the
                # end of this function -- see the comment on the return.
                # MOE,OVERFLOWS,OVFORMS,<IMAGE>,<count>,<tablespecs...>
                toks = args.split(",")
                if len(toks) < 5:
                    continue
                child, count = toks[3].strip(), toks[4].strip()
                tail = ",".join(toks[5:])
                rules.children.append(
                    ChildImage(child, count, _parse_tablespecs(tail)))

    # THE RULES RUN BACKWARDS. The verb is "add image AFTER CURRENT image", and every
    # rule in the section uses the same anchor -- the image being defined -- so each
    # one lands immediately after it and pushes the previous insertions down. The last
    # rule written is therefore the first image rendered.
    #
    # Two independent pieces of evidence, both from a render rather than from reading:
    #   * QCPP_CAV lists QCPP_CAV2 (the "Total Vehicle Premium" row) before QCPP_CAV1
    #     (the coverage rows). Rendered in file order the total sits ABOVE the
    #     coverages it totals; reversed, it sits below them.
    #   * QCPPSUM_HDR lists ...GL, CP, CA. The Products reference document
    #     ("Example QUote Dec.pdf", page 3) prints Commercial Property ABOVE Commercial
    #     General Liability, and Terrorism -- listed first -- last.
    rules.children.reverse()
    return rules


# ---------------------------------------------------------------------------- walking


@dataclass
class Node:
    image: str
    depth: int
    driver: Optional[TableSpec]
    origin: Optional[str]
    fap: bool
    ddt: bool
    cycle: bool = False


def walk(ddtdir: str, fapdir: str, root: str, depth: int = 0,
         seen: Optional[Set[str]] = None,
         driver: Optional[TableSpec] = None,
         out: Optional[List[Node]] = None) -> List[Node]:
    seen = set() if seen is None else seen
    out = [] if out is None else out

    rules = read_ddt(ddtdir, root)
    fap = os.path.exists(os.path.join(fapdir, root + ".FAP"))
    node = Node(root, depth, driver, rules.origin, fap, rules.found,
                cycle=root in seen)
    out.append(node)
    if node.cycle:
        return out

    seen = seen | {root}
    for child in rules.children:
        walk(ddtdir, fapdir, child.image, depth + 1, seen, child.driver, out)
    return out


# ------------------------------------------------------------------------------- CLI


# The CPP quote packet, in the page order of the Products reference document
# ("Example QUote Dec.pdf").  FORM.DAT names only; the images come from it.
QUOTE_PACKETS: Dict[str, List[str]] = {
    "CPP": [
        "QUOTE COVER.1",
        "QUOTE CPPSUM.1",
        "QUOTE CPPGL.1",
        "QUOTE CPPGL.2",
        "QUOTE CPPCF.1",
        "QUOTE CPPCF.2",
        "QUOTE CPPCA.1",
        "QUOTE CPPCA.2",
        "QUOTE CPPCA.3",
        "QUOTE CPP FORMS.1",
    ],
}


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--formdat", default=DEFAULT_FORMDAT)
    ap.add_argument("--ddtdir", default=DEFAULT_DDTDIR)
    ap.add_argument("--fapdir", default=DEFAULT_FAPDIR)
    ap.add_argument("--lob", default="CPP")
    ap.add_argument("--form", action="append", default=[],
                    help="a FORM.DAT entry name, e.g. 'QUOTE CPPCA.1' (repeatable)")
    ap.add_argument("--packet", help="a named packet, e.g. 'quote'")
    ap.add_argument("--image", action="append", default=[],
                    help="walk from a bare image name instead of a FORM.DAT entry")
    ap.add_argument("--list", action="store_true",
                    help="list the FORM.DAT entries for the LOB and exit")
    ap.add_argument("--csv")
    args = ap.parse_args(argv)

    entries = read_formdat(args.formdat)
    by_lob = [e for e in entries if e.lob.upper() == args.lob.upper()]

    if args.list:
        for e in sorted(by_lob, key=lambda x: x.name):
            print(f"{e.name:<28} {len(e.images):>2} image(s)  {'/'.join(e.images)}")
        return 0

    wanted: List[str] = list(args.form)
    if args.packet:
        key = args.lob.upper()
        if args.packet.lower() == "quote" and key in QUOTE_PACKETS:
            wanted = QUOTE_PACKETS[key] + wanted
        else:
            print(f"unknown packet '{args.packet}' for LOB {key}", file=sys.stderr)
            return 2

    if not wanted and not args.image:
        ap.error("give --form, --packet, --image or --list")

    index: Dict[str, FormEntry] = {e.name.upper(): e for e in by_lob}
    rows: List[dict] = []
    missing_fap: List[str] = []
    missing_ddt: List[str] = []

    def emit(section: str, roots: Iterable[str]) -> None:
        print(f"\n=== {section} ===")
        for root in roots:
            for node in walk(args.ddtdir, args.fapdir, root):
                bullet = "  " * node.depth + ("+ " if node.depth else "")
                flags = []
                if not node.fap:
                    flags.append("NO FAP")
                    missing_fap.append(node.image)
                if not node.ddt:
                    flags.append("no ddt")
                    missing_ddt.append(node.image)
                if node.cycle:
                    flags.append("cycle")
                drv = f"   per {node.driver}" if node.driver else ""
                tag = ("   [" + ", ".join(flags) + "]") if flags else ""
                print(f"{bullet}{node.image}{drv}{tag}")
                rows.append({
                    "section": section,
                    "depth": node.depth,
                    "image": node.image,
                    "driver": str(node.driver) if node.driver else "",
                    "origin": node.origin or "",
                    "hasFap": node.fap,
                    "hasDdt": node.ddt,
                })

    for name in wanted:
        entry = index.get(name.upper())
        if entry is None:
            print(f"\n=== {name} ===\n  (not in FORM.DAT for LOB {args.lob})")
            continue
        emit(name, entry.images)

    for image in args.image:
        emit(image, [image])

    uniq = {r["image"] for r in rows}
    print(f"\n{len(rows)} placement(s), {len(uniq)} distinct image(s)")
    if missing_fap:
        print(f"  {len(set(missing_fap))} image(s) with no FAP: "
              f"{', '.join(sorted(set(missing_fap))[:12])}")
    if missing_ddt:
        print(f"  {len(set(missing_ddt))} image(s) with no DDT: "
              f"{', '.join(sorted(set(missing_ddt))[:12])}")

    if args.csv:
        os.makedirs(os.path.dirname(os.path.abspath(args.csv)), exist_ok=True)
        with open(args.csv, "w", newline="", encoding="utf-8") as fh:
            w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()) if rows else
                               ["section", "depth", "image", "driver", "origin",
                                "hasFap", "hasDdt"])
            w.writeheader()
            w.writerows(rows)
        print(f"wrote {args.csv}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
