#!/usr/bin/env python3
"""
quotetemplate.py -- author the quote packet as ONE GhostDraft template.

Products, 2026-08-27: "for this POC we make one form that has all these elements on
it; in the future we can figure out how to split them out more intelligently." So this
emits a single `.gd` carrying every page of the packet, bound to the single `Quote`
root that tools/quotemodel.py declares.

Everything on paper comes from one of four repeating tables plus a handful of scalars,
and the repeats are the point -- the 428 mechanically converted quote templates carry
zero list or conditional instructions between them, which is why they could never have
rendered a packet. These are real `listInstructionType` / `conditionalInstructionType`
trees, emitted through tools/gdauthor.py and re-verified from the written bytes.

The section order is the packet order that tools/ddtpacket.py recovers from FORM.DAT
and the DDTs, and the wording is the wording tools/htmlpacket.py renders -- so the two
outputs can be diffed value for value against the same quote.

Usage
    python tools/quotemodel.py --gdm --model          # the model, first
    python tools/quotetemplate.py --out output/quote-poc/Quote Proposal.gd
"""

from __future__ import annotations

import argparse
import base64
import os
import re
import sys
from typing import Sequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import quotemodel as qm                                     # noqa: E402
from gdauthor import (Break, Cell, Cond, Emitter, Envelope, Fill,  # noqa: E402
                      Furniture, IS_PROVIDED, Image, PageBreak, PageNumber, Path,
                      Repeat, Row, Static, Table, _stable_guid, verify, wrap)
from gdmodel import Model                                    # noqa: E402

# Usable width between the margins RTF_HEAD declares: 12240 - 1440 - 1440.
PAGE_TWIPS = 9360


def cols(*widths: int) -> list[int]:
    """Column widths -> the cumulative right edges RTF's `cellx` wants, scaled to
    the page so a layout stays put if the widths are retuned."""
    total = sum(widths)
    edge, out = 0, []
    for w in widths:
        edge += w
        out.append(round(edge * PAGE_TWIPS / total))
    return out


# One grid per table. These are proportions of the printable width, chosen to match
# what the HTML render puts on the page -- which is itself the legacy FAP geometry.
SUMMARY_COLS = cols(50, 17, 16, 17)          # line / premium / TRIA / total
VEHICLE_COLS = cols(6, 7, 7, 28, 14, 24, 14)  # veh / state / year / make / class / vin / prem
COVERAGE_COLS = cols(49, 18, 17, 16)         # description / limit / deductible / premium
FORMS_COLS = cols(24, 16, 12, 48)            # line / number / edition / description

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PACKAGE = os.path.join(REPO, "output", "quote-poc", "quote-package")

# The base style library of the Studio project we author into. Both the project's own
# `.gds` and the compiled proprietary package's templates carry this id.
STYLE_LIB = "02546597-58b4-4b0c-976b-abaa6c4a8244"
ANNOTATION_LIB = "64d5b3c3-056a-4f4e-b063-35f84e13f3ec"

QUOTE = Path(qm.ROOT, qm.guid(qm.ROOT))


# ------------------------------------------------------------------ path helpers


def scalar(name: str) -> Path:
    """`Quote > <name>`."""
    return QUOTE.then(name, qm.guid(qm.key(name)))


def list_path(*names: str) -> Path:
    """`Quote > Vehicles`, `Quote > Insurance Lines`, …"""
    p = QUOTE
    parts: list[str] = []
    for n in names:
        parts.append(n)
        p = p.then(n, qm.guid(qm.key(*parts)))
    return p


def item(iterator: Path, path_parts: Sequence[str], name: str) -> Path:
    """An attribute of the current list item. The iterator guid is template-local
    (gdauthor mints it); the ATTRIBUTE guid is the model's."""
    return iterator.then(name, qm.guid(qm.key(*path_parts, name)))


def iterator_for(element: str, list_label: str) -> Path:
    """The iterator guid is TEMPLATE-LOCAL and the emitter mints it from the list
    path's label plus the iterator name -- so a path written against the item has to
    be built with the SAME seed or it resolves against nothing."""
    return Path(element, _stable_guid(f"{list_label}|{element}"))


def labelled(label: str, path: Path, end: bool = True) -> list:
    """A label and its value, printed only when the value is there. The Break goes
    INSIDE the conditional so a suppressed value leaves no blank line -- the
    difference between a schedule with optional columns and one full of holes."""
    body: list = [Static(label), Fill(path)]
    if end:
        body.append(Break())
    return [Cond(path.then(*IS_PROVIDED), then=body)]


def value(path: Path) -> list:
    """A cell's content: the value when it is there, nothing when it is not. In a
    real table an absent value needs no placeholder -- the cell holds the column
    open by itself, which is the whole reason to use a table instead of tabs."""
    return [Cond(path.then(*IS_PROVIDED), then=[Fill(path)])]


def head(text: str, width: int, style: str = "s2") -> Cell:
    return Cell([Static(text)], width, style=style, bold=True)


def money(body: list, width: int) -> Cell:
    """A numeric cell. `s8` is the stylesheet's right-aligned Arial 10 -- the same
    style the ISO templates use for a premium column, and the GhostDraft equivalent
    of the `MODE=R` the DDT puts on these fields."""
    return Cell(body, width, style="s8")


# ------------------------------------------------------------------- the sections


FRAG = os.path.join(REPO, "output", "quote-poc", "frag")


def from_fragment(image: str, fields: dict) -> list:
    """Build a section's STATIC content from the FAP, not by hand.

    An earlier version of `cover()` was typed out from the field list, so the page
    came out as three labels and three values: the banner image, the green footer
    bar and every line of marketing copy -- 76 `M,` records and 11 `T,` records in
    QTE_COVER_A -- were simply absent. Products asked where the text went, and the
    answer was that I never carried it over.

    So the content comes from `emit-html`'s own output for the image, which is the
    parser the HTML render is built on: text runs, images already decoded from the
    Documaker `.LOG` rasters, and field spans keyed by FAP field name. Reading order
    is top then left, and a change of line becomes a Break.

    `fields` maps a FAP field name to a concept Path; a field not in it is skipped
    rather than guessed at.
    """
    path = os.path.join(FRAG, image + ".html")
    if not os.path.exists(path):
        raise SystemExit(f"no emit-html fragment for {image} -- run tools/htmlpacket.py "
                         f"first, it caches them in {FRAG}")
    html = open(path, encoding="utf-8").read()
    # ONE SECTION PER PAGE. QTE_COVER_A is a two-page image -- the cover and then a
    # page of disclaimer -- and reading the file as one stream sorted the two pages'
    # text together by vertical position, which spliced "This Mutual of Enumclaw
    # Quote is personally prepared for" into the middle of the disclaimer.
    pages = re.findall(r'<section class="form-page".*?</section>', html, re.S)
    out: list = []
    for n, body in enumerate(pages):
        if n:
            out.append(PageBreak())
        out.extend(_page_nodes(body, fields))
    return out


def _page_nodes(body: str, fields: dict) -> list:
    items: list[tuple[float, float, str, object]] = []
    for m in re.finditer(r'<span class="abs"[^>]*style="([^"]*)"[^>]*>([^<]*)</span>',
                         body):
        st, text = m.group(1), m.group(2)
        if not text.strip():
            continue
        items.append((_pt(st, "top"), _pt(st, "left"), "text", _unescape(text)))
    for m in re.finditer(r'<span class="abs field"[^>]*data-field="([^"]+)"'
                         r'[^>]*style="([^"]*)"', body):
        name, st = m.group(1), m.group(2)
        if name in fields:
            items.append((_pt(st, "top"), _pt(st, "left"), "fill", fields[name]))
    # `src` comes BEFORE `style` on emit-html's img tags, so match the tag and then
    # pick the attributes out of it rather than assuming an order.
    for m in re.finditer(r'<img\b[^>]*>', body):
        tag = m.group(0)
        src = re.search(r'src="data:image/png;base64,([^"]+)"', tag)
        st = re.search(r'style="([^"]*)"', tag)
        if not (src and st):
            continue
        style = st.group(1)
        items.append((_pt(style, "top"), _pt(style, "left"), "image",
                      (base64.b64decode(src.group(1)),
                       _pt(style, "width"), _pt(style, "height"))))

    items.sort(key=lambda i: (round(i[0], 1), i[1]))
    out: list = []
    line: float | None = None
    for top, _left, kind, payload in items:
        if line is not None and abs(top - line) > 1.5:
            out.append(Break())
        line = top
        if kind == "text":
            out.append(Static(payload + " "))
        elif kind == "fill":
            out.append(Fill(payload))
        else:
            png, w, h = payload
            out.append(Image(png, w, h))
    out.append(Break())
    return out


def _pt(style: str, prop: str) -> float:
    m = re.search(prop + r":(-?[\d.]+)pt", style)
    return float(m.group(1)) if m else 0.0


def _unescape(s: str) -> str:
    return (s.replace("&amp;", "&").replace("&lt;", "<")
             .replace("&gt;", ">").replace("&quot;", '"'))


def cover() -> list:
    """`QTE_COVER_A` in full -- banner, marketing copy, green footer bar and the
    four fields -- taken from the form rather than retyped."""
    return from_fragment("QTE_COVER_A", {
        "INSURED NAME1": scalar("Insured Name"),
        "AGENT NAME": scalar("Agent Name"),
        "AGENT PHONE": scalar("Agent Phone"),
        "PROPOSAL PERIOD": scalar("Proposal Period"),
    })

def premium_summary() -> list:
    lines = list_path("Insurance Lines")
    it = iterator_for("Insurance Line", "Quote > Insurance Lines")
    endorsements = it.then("Endorsements",
                           qm.guid(qm.key("Insurance Lines", "Endorsements")))
    end_it = iterator_for("Endorsement", "Insurance Line > Endorsements")
    w = SUMMARY_COLS

    def line_attr(name: str) -> Path:
        return item(it, ["Insurance Lines"], name)

    return [
        Table(rows=[
            Row(header=True, cells=[
                head("Insurance Line", w[0]),
                head("Premium ($)", w[1], "s8"),
                head("*TRIA ($)", w[2], "s8"),
                head("Total Premium ($)", w[3], "s8"),
            ]),
            Repeat(lines, "Insurance Line", body=[
                Row(cells=[
                    Cell([Fill(line_attr("Name"))], w[0], bold=True),
                    money(value(line_attr("Premium")), w[1]),
                    money(value(line_attr("Terrorism Premium")), w[2]),
                    money(value(line_attr("Total Premium")), w[3]),
                ]),
            ]),
            Row(cells=[
                head("Total Premium", w[0]),
                money(value(scalar("Total Premium")), w[1]),
                money(value(scalar("Total Terrorism Premium")), w[2]),
                money(value(scalar("Total Premium With Terrorism")), w[3]),
            ]),
        ]),
        # The endorsement list is NOT a column of the summary table -- legacy prints
        # it as indented lines under its insurance line, so it stays a paragraph
        # list. Keeping it out of the table is also what lets the table's own rows
        # stay one line tall.
        Repeat(lines, "Insurance Line", body=[
            Repeat(endorsements, "Endorsement", body=[
                Static("      "),
                Fill(item(end_it, ["Insurance Lines", "Endorsements"], "Description")),
                Break(),
            ]),
        ]),
        Cond(scalar("Total Terrorism Premium").then(*IS_PROVIDED),
             then=[Static("*Terrorism Risk Insurance Act"), Break()]),
        Break(),
    ]


def auto_summary() -> list:
    vehicles = list_path("Vehicles")
    it = iterator_for("Vehicle", "Quote > Vehicles")
    w = VEHICLE_COLS

    def veh(name: str) -> Path:
        return item(it, ["Vehicles"], name)

    counts = ["Light Trucks", "Medium Trucks", "Heavy Trucks", "Extra Heavy Trucks",
              "Trailers", "Buses", "Private Passenger Autos", "All Other Vehicles"]
    return [
        Static("Auto Summary"), Break(),
        Static("* Leased Vehicle"), Break(),
        Table(rows=[
            Row(header=True, cells=[
                head("Veh", w[0]), head("State", w[1]), head("Year", w[2]),
                head("Make/Model", w[3]), head("Class Code", w[4]),
                head("VIN", w[5]), head("Veh Total Prem", w[6], "s8"),
            ]),
            Repeat(vehicles, "Vehicle", body=[
                Row(cells=[
                    # The asterisk is a marker, not a value: it prints only for a
                    # leased unit, and it shares the cell with the vehicle number.
                    Cell([Fill(veh("Number")),
                          Cond(veh("Is Leased").then(*IS_PROVIDED),
                               then=[Static(" *")])], w[0]),
                    Cell(value(veh("State")), w[1]),
                    Cell(value(veh("Year")), w[2]),
                    Cell(value(veh("Make And Model")), w[3]),
                    Cell(value(veh("Class Code")), w[4]),
                    Cell(value(veh("VIN")), w[5]),
                    money(value(veh("Premium")), w[6]),
                ]),
            ]),
        ]),
        *labelled("Total Auto Premium\t", scalar("Auto Vehicles Premium")),
        *labelled("Auto Insurance Line Premium\t", scalar("Auto Line Premium")),
        *labelled("Total Vehicle Premium\t", scalar("Auto Total Premium")),
        *labelled("Total Vehicles On Policy\t", scalar("Vehicle Count")),
        Break(),
        Static("VEHICLE TYPES SUMMARY"), Break(),
        *[x for c in counts for x in labelled(c + "\t", scalar(c))],
        Break(),
    ]


def coverage_table(rows_repeat: Repeat, total_label: str = "",
                   total_path: Path | None = None) -> Table:
    """The Coverage / Limit / Deductible / Premium table, which the packet draws
    twice -- once for the insurance line and once per vehicle."""
    w = COVERAGE_COLS
    rows: list = [
        Row(header=True, cells=[
            head("Coverage / Description", w[0]),
            head("Limit Amount ($)", w[1], "s8"),
            head("Deductible ($)", w[2], "s8"),
            head("Premium ($)", w[3], "s8"),
        ]),
        rows_repeat,
    ]
    if total_path is not None:
        rows.append(Row(cells=[
            head(total_label, w[0]),
            Cell([], w[1]), Cell([], w[2]),
            money(value(total_path), w[3]),
        ]))
    return Table(rows=rows)


def auto_detail() -> list:
    symbols = [
        ("LIABILITY", "Liability Symbols"),
        ("PERSONAL INJURY PROTECTION", "Personal Injury Protection Symbols"),
        ("AUTO MEDICAL PAYMENTS", "Medical Payments Symbols"),
        ("UNINSURED MOTORIST COVERAGE", "Uninsured Motorists Symbols"),
        ("UNDERINSURED MOTORISTS COVERAGE", "Underinsured Motorists Symbols"),
        ("PHYSICAL DAMAGE COMPREHENSIVE COVERAGE", "Comprehensive Symbols"),
        ("PHYSICAL DAMAGE SPECIFIED CAUSES OF LOSS COVERAGE",
         "Specified Causes Of Loss Symbols"),
        ("PHYSICAL DAMAGE COLLISION COVERAGE", "Collision Symbols"),
        ("PHYSICAL DAMAGE TOWING AND LABOR COVERAGE", "Towing And Labor Symbols"),
    ]
    w = COVERAGE_COLS

    line_covs = list_path("Line Coverages")
    lc_it = iterator_for("Line Coverage", "Quote > Line Coverages")

    def lc(name: str) -> Path:
        return item(lc_it, ["Line Coverages"], name)

    vehicles = list_path("Vehicles")
    v_it = iterator_for("Vehicle", "Quote > Vehicles")
    v_covs = v_it.then("Coverages", qm.guid(qm.key("Vehicles", "Coverages")))
    vc_it = iterator_for("Coverage", "Vehicle > Coverages")

    def veh(name: str) -> Path:
        return item(v_it, ["Vehicles"], name)

    def vc(name: str) -> Path:
        return item(vc_it, ["Vehicles", "Coverages"], name)

    vw = cols(14, 30, 10, 20, 12, 14)   # the vehicle identification block

    return [
        Static("Commercial Auto"), Break(),
        Static("COVERED AUTO SYMBOLS"), Break(),
        *[x for label, attr in symbols for x in labelled(label + "\t", scalar(attr))],
        Break(),
        coverage_table(
            Repeat(line_covs, "Line Coverage", body=[
                Row(cells=[
                    Cell([Fill(lc("Description"))], w[0]),
                    money(value(lc("Limit")), w[1]),
                    money(value(lc("Deductible")), w[2]),
                    money(value(lc("Premium")), w[3]),
                ]),
            ]),
            "Total Commercial Auto Insurance Line Premium",
            scalar("Auto Line Premium")),
        Break(),
        Repeat(vehicles, "Vehicle", body=[
            Static("Commercial Auto - Vehicle # "), Fill(veh("Number")),
            Static("   State: "), Fill(veh("State")), Break(),
            Table(rows=[
                Row(cells=[
                    Cell([Fill(veh("Year"))], vw[0]),
                    Cell([Fill(veh("Make And Model"))], vw[1]),
                    head("VIN:", vw[2]),
                    Cell([Fill(veh("VIN"))], vw[3]),
                    Cell([], vw[4]), Cell([], vw[5]),
                ]),
                Row(cells=[
                    head("Class Code:", vw[0]),
                    Cell(value(veh("Class Code")), vw[1]),
                    Cell([], vw[2]), Cell([], vw[3]),
                    head("Cost New ($):", vw[4]),
                    money(value(veh("Cost New")), vw[5]),
                ]),
                Row(cells=[
                    head("Territory:", vw[0]),
                    Cell(value(veh("Territory")), vw[1]),
                    Cell([], vw[2]), Cell([], vw[3]),
                    head("Stated Cost ($):", vw[4]),
                    money(value(veh("Stated Cost")), vw[5]),
                ]),
            ]),
            coverage_table(
                Repeat(v_covs, "Coverage", body=[
                    Row(cells=[
                        Cell([Fill(vc("Description"))], w[0]),
                        money(value(vc("Limit")), w[1]),
                        money(value(vc("Deductible")), w[2]),
                        money(value(vc("Premium")), w[3]),
                    ]),
                ]),
                "Total Vehicle Premium", veh("Premium")),
            Break(),
        ]),
    ]


def forms_schedule() -> list:
    forms = list_path("Forms")
    it = iterator_for("Form", "Quote > Forms")
    w = FORMS_COLS

    def f(name: str) -> Path:
        return item(it, ["Forms"], name)

    return [
        Static("FORMS AND ENDORSEMENT SCHEDULE"), Break(),
        Table(rows=[
            Row(header=True, cells=[
                head("Coverage line", w[0]), head("Form Number", w[1]),
                head("Ed. Date", w[2]), head("Description", w[3]),
            ]),
            Repeat(forms, "Form", body=[
                Row(cells=[
                    Cell(value(f("Coverage Line")), w[0]),
                    Cell(value(f("Form Number")), w[1]),
                    Cell(value(f("Edition Date")), w[2]),
                    Cell(value(f("Description")), w[3]),
                ]),
            ]),
        ]),
    ]


def furniture() -> Furniture:
    """The running header and footer -- the GhostDraft equivalent of FORM.DAT's OX
    and OY flags, which the HTML assembler reproduces by repeating QTE_HDR on every
    page and pinning QTE_FTR to row 25200.

    The page number is an RTF field, not a value: `PAGE` and `NUMPAGES` are
    evaluated by the renderer, which is why nothing has to supply them. That is how
    the ISO templates print "Page 1 of 10" and it is what the HTML assembler has to
    compute for itself after pagination.
    """
    hw = cols(60, 40)
    fw = cols(70, 30)
    return Furniture(
        header=[
            Cell([Fill(scalar("Insured Name")),
                  Cond(scalar("Insured Name 2").then(*IS_PROVIDED),
                       then=[Break(), Fill(scalar("Insured Name 2"))])], hw[0]),
            Cell([Static("Quote #   "), Fill(scalar("Quote Number"))], hw[1],
                 style="s8"),
        ],
        footer=[
            Cell([], fw[0]),
            Cell([Static("Page "), PageNumber(),
                  Static(" of "), PageNumber(total=True)], fw[1], style="s8"),
        ])


def spec() -> list:
    return [
        *cover(),
        *premium_summary(),
        *auto_summary(),
        *auto_detail(),
        *forms_schedule(),
    ]


# -------------------------------------------------------------------------- CLI


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--package", default=PACKAGE)
    ap.add_argument("--out", default=os.path.join(REPO, "output", "quote-poc",
                                                  "Quote Proposal.gd"))
    ap.add_argument("--title", default="Quote Proposal")
    args = ap.parse_args(argv)

    libs = [os.path.join(args.package, "Concept Libraries", f)
            for f in os.listdir(os.path.join(args.package, "Concept Libraries"))
            if f.endswith(".gdm")]
    model = Model.load(os.path.join(args.package, "model.xml"), concept_libs=libs)
    print(f"model   {model.name!r} v{model.version}")

    env = Envelope(args.package)
    # The project's own templates are Studio-side and carry no compiled <styleMap>, so
    # the defaults are named rather than inferred -- see STYLE_LIB above.
    env.default_style_id = env.default_style_id or STYLE_LIB
    env.annotation_style_id = env.annotation_style_id or ANNOTATION_LIB

    em = Emitter(model, qm.LIB)
    markup, rtf, resolved = em.build(spec(), args.title, furniture=furniture())

    if em.errors:
        print(f"\nBINDINGS THAT DO NOT RESOLVE ({len(em.errors)}):")
        for e in em.errors[:40]:
            print("  ", e)
        return 1

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="") as fh:
        fh.write(wrap(rtf, markup, args.title, env=env, roots=em.roots,
                      library=qm.LIB, furniture=em.furniture))

    print(f"\nwrote {args.out} ({os.path.getsize(args.out):,} bytes)")
    print(f"instructions declared  {em._next - 1}")
    print(f"bindings resolved      {len(resolved)}")
    print(f"domain models declared {len(em.roots)}   "
          + ", ".join(env.domain_names.get(g, g) for g in em.roots))

    bad = verify(args.out)
    print()
    if bad:
        print(f"GRAMMAR CHECK FAILED ({len(bad)}):")
        for b in bad[:20]:
            print("  ", b)
        return 1
    print("GRAMMAR CHECK PASSED -- all five rules re-derived from the written file")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
