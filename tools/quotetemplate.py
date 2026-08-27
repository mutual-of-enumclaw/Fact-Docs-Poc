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
import os
import sys
from typing import Sequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import quotemodel as qm                                     # noqa: E402
from gdauthor import (Break, Cond, Emitter, Envelope, Fill,  # noqa: E402
                      IS_PROVIDED, Path, Repeat, Static, _stable_guid, verify, wrap)
from gdmodel import Model                                    # noqa: E402

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


def cell(path: Path) -> list:
    """A table cell: the value and a tab, or just a tab when it is absent, so the
    columns still line up."""
    return [Cond(path.then(*IS_PROVIDED),
                 then=[Fill(path), Static("\t")],
                 otherwise=[Static("\t")])]


# ------------------------------------------------------------------- the sections


def cover() -> list:
    return [
        Static("This Mutual of Enumclaw Quote is personally prepared for"), Break(),
        Fill(scalar("Insured Name")), Break(),
        *labelled("", scalar("Insured Name 2")),
        Break(),
        Static("Presented by"), Break(),
        Fill(scalar("Agent Name")), Break(),
        *labelled("", scalar("Agent Phone")),
        Break(),
        Static("Proposed Policy Period"), Break(),
        Fill(scalar("Proposal Period")), Break(),
        Break(),
    ]


def premium_summary() -> list:
    lines = list_path("Insurance Lines")
    it = iterator_for("Insurance Line", "Quote > Insurance Lines")
    endorsements = it.then("Endorsements", qm.guid(qm.key("Insurance Lines", "Endorsements")))
    end_it = iterator_for("Endorsement", "Insurance Line > Endorsements")

    def line_attr(name: str) -> Path:
        return item(it, ["Insurance Lines"], name)

    return [
        Static("Insurance Line\tPremium ($)\t*TRIA ($)\tTotal Premium ($)"), Break(),
        Repeat(lines, "Insurance Line", body=[
            Fill(line_attr("Name")), Static("\t"),
            *cell(line_attr("Premium")),
            *cell(line_attr("Terrorism Premium")),
            *cell(line_attr("Total Premium")),
            Break(),
            Repeat(endorsements, "Endorsement", body=[
                Static("    "),
                Fill(item(end_it, ["Insurance Lines", "Endorsements"], "Description")),
                Break(),
            ]),
        ]),
        Static("Total Premium\t"),
        *cell(scalar("Total Premium")),
        *cell(scalar("Total Terrorism Premium")),
        *cell(scalar("Total Premium With Terrorism")),
        Break(),
        # The footnote belongs to the TRIA column, so it prints only when there is one.
        Cond(scalar("Total Terrorism Premium").then(*IS_PROVIDED),
             then=[Static("*Terrorism Risk Insurance Act"), Break()]),
        Break(),
    ]


def auto_summary() -> list:
    vehicles = list_path("Vehicles")
    it = iterator_for("Vehicle", "Quote > Vehicles")

    def veh(name: str) -> Path:
        return item(it, ["Vehicles"], name)

    counts = ["Light Trucks", "Medium Trucks", "Heavy Trucks", "Extra Heavy Trucks",
              "Trailers", "Buses", "Private Passenger Autos", "All Other Vehicles"]
    return [
        Static("Auto Summary"), Break(),
        Static("* Leased Vehicle"), Break(),
        Static("Veh\tState\tYear\tMake/Model\tClass Code\tVIN\tVeh Total Prem"), Break(),
        Repeat(vehicles, "Vehicle", body=[
            Fill(veh("Number")), Static("\t"),
            # The asterisk is a marker, not a value: it prints only for a leased unit.
            Cond(veh("Is Leased").then(*IS_PROVIDED), then=[Static("*")]),
            *cell(veh("State")),
            *cell(veh("Year")),
            *cell(veh("Make And Model")),
            *cell(veh("Class Code")),
            *cell(veh("VIN")),
            *cell(veh("Premium")),
            Break(),
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

    return [
        Static("Commercial Auto"), Break(),
        Static("COVERED AUTO SYMBOLS"), Break(),
        *[x for label, attr in symbols for x in labelled(label + "\t", scalar(attr))],
        Break(),
        Static("Coverage / Description\tLimit Amount ($)\tDeductible ($)\tPremium ($)"),
        Break(),
        Repeat(line_covs, "Line Coverage", body=[
            Fill(lc("Description")), Static("\t"),
            *cell(lc("Limit")), *cell(lc("Deductible")), *cell(lc("Premium")),
            Break(),
        ]),
        *labelled("Total Commercial Auto Insurance Line Premium\t",
                  scalar("Auto Line Premium")),
        Break(),
        Repeat(vehicles, "Vehicle", body=[
            Static("Commercial Auto - Vehicle # "), Fill(veh("Number")),
            Static("  State: "), Fill(veh("State")), Break(),
            Fill(veh("Year")), Static(" "), Fill(veh("Make And Model")),
            Static("\tVIN: "), Fill(veh("VIN")), Break(),
            Static("Class Code: "), Fill(veh("Class Code")),
            Static("\tCost New ($): "),
            *cell(veh("Cost New")),
            Break(),
            Static("Territory: "), Fill(veh("Territory")),
            Static("\tStated Cost ($): "),
            *cell(veh("Stated Cost")),
            Break(),
            Static("Coverage / Description\tLimit Amount ($)\tDeductible ($)\tPremium ($)"),
            Break(),
            Repeat(v_covs, "Coverage", body=[
                Fill(vc("Description")), Static("\t"),
                *cell(vc("Limit")), *cell(vc("Deductible")), *cell(vc("Premium")),
                Break(),
            ]),
            Static("Total Vehicle Premium\t"), Fill(veh("Premium")), Break(),
            Break(),
        ]),
    ]


def forms_schedule() -> list:
    forms = list_path("Forms")
    it = iterator_for("Form", "Quote > Forms")

    def f(name: str) -> Path:
        return item(it, ["Forms"], name)

    return [
        Static("FORMS AND ENDORSEMENT SCHEDULE"), Break(),
        Static("Coverage line\tForm Number\tEd. Date\tDescription"), Break(),
        Repeat(forms, "Form", body=[
            Fill(f("Coverage Line")), Static("\t"),
            Fill(f("Form Number")), Static("\t"),
            *cell(f("Edition Date")),
            Fill(f("Description")),
            Break(),
        ]),
    ]


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
    markup, rtf, resolved = em.build(spec(), args.title)

    if em.errors:
        print(f"\nBINDINGS THAT DO NOT RESOLVE ({len(em.errors)}):")
        for e in em.errors[:40]:
            print("  ", e)
        return 1

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8", newline="") as fh:
        fh.write(wrap(rtf, markup, args.title, env=env, roots=em.roots,
                      library=qm.LIB))

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
