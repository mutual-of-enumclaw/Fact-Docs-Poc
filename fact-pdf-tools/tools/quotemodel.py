#!/usr/bin/env python3
"""
quotemodel.py -- ONE declaration of the Quote concept model, three artefacts.

For the POC the quote packet is a SINGLE GhostDraft template carrying every element
(Products, 2026-08-27: "make one form that has all these elements on it; in the future
we can figure out how to split them out more intelligently"). A single template needs a
single bindable root, so everything hangs off `Quote` -- insured, agent, totals, and the
four repeating tables.

Three things have to agree exactly or nothing renders, and they are all generated here
from `MODEL` below so they cannot drift:

  1. the concept library  (`.gdm`)   -- what GhostDraft Studio edits and CreateSnapshot
                                        validates markup against
  2. the compiled model   (`model.xml`) -- what a template's GUID paths resolve through,
                                        and what tools/gdauthor.py binds against
  3. the Server XML                   -- what fact-docgen's builder must emit; the
                                        element names come from the same declaration

GUIDs are derived the same way `core/Infrastructure/ProjectConcepts.cs` derives them --
MD5 of "fact-pdf-tools/QuoteLib/<key>", laid into a GUID little-endian -- so the C# and
Python sides agree without a shared file. `Quote` itself is
bffb5190-0d36-e2ce-68e2-79a723e733b8.

The `.gdm` grammar for a repeating table, read off the real Model Library:

    <concept xsi:type="ListConcept" name="<Lib>_Quote_Vehicles" elementName="Vehicle"
             hiddenConceptName="<Lib>_Quote_Vehicles_Elements">
      <selectors>… First / Last …</selectors>
      <elementKinds><conceptName><Lib>_Quote_Vehicles_Elements</conceptName></elementKinds>
    </concept>
    <concept name="<Lib>_Quote_Vehicles_Elements"> <attributes>…</attributes> </concept>

and the parent points at it with an `attributeState="AttributeWithKinds"` attribute.

Usage
    python tools/quotemodel.py --gdm   output/concept-library/"Model Library (with Quote).gdm"
    python tools/quotemodel.py --model output/quote-poc/quote-model.xml
    python tools/quotemodel.py --print
"""

from __future__ import annotations

import argparse
import hashlib
import os
import sys
import uuid
from dataclasses import dataclass, field
from typing import Sequence

LIB = "Model Library"
ROOT = "Quote"


def guid(key: str) -> str:
    """Same derivation as ProjectConcepts.Guid -- MD5 of the namespaced key, laid into
    a Guid the way .NET's `new Guid(byte[])` does (little-endian first three fields)."""
    h = hashlib.md5(("fact-pdf-tools/QuoteLib/" + key).encode("utf-8")).digest()
    return str(uuid.UUID(bytes_le=h))


# --------------------------------------------------------------------- declaration


@dataclass
class Attr:
    """A leaf. `type` is a GhostDraft base concept name."""
    name: str
    type: str = "Text"


@dataclass
class ListOf:
    """A repeating table. `element` is the singular noun GhostDraft shows in the UI and
    uses as the Server XML item element name."""
    name: str
    element: str
    attrs: Sequence[Attr] = ()
    lists: Sequence["ListOf"] = ()


# Everything the eight assembled pages put on paper. Names are the reader's words, not
# the FAP field names -- the FAP name is an accident of a 1990s form, and this becomes
# the integration contract with fact-docgen.
MODEL_ATTRS: Sequence[Attr] = (
    Attr("Quote Number"),
    Attr("Insured Name"),
    Attr("Insured Name 2"),
    Attr("Agent Name"),
    Attr("Agent Phone"),
    Attr("Proposal Period"),
    Attr("Effective Date"),
    Attr("Expiration Date"),
    # page 2 -- premium summary
    Attr("Total Premium", "Currency"),
    Attr("Total Terrorism Premium", "Currency"),
    Attr("Total Premium With Terrorism", "Currency"),
    # page 3 -- auto summary
    Attr("Auto Vehicles Premium", "Currency"),
    Attr("Auto Line Premium", "Currency"),
    Attr("Auto Total Premium", "Currency"),
    Attr("Vehicle Count"),
    Attr("Light Trucks"),
    Attr("Medium Trucks"),
    Attr("Heavy Trucks"),
    Attr("Extra Heavy Trucks"),
    Attr("Trailers"),
    Attr("Buses"),
    Attr("Private Passenger Autos"),
    Attr("All Other Vehicles"),
    # covered auto symbols -- one string per coverage, the cells joined
    Attr("Liability Symbols"),
    Attr("Personal Injury Protection Symbols"),
    Attr("Medical Payments Symbols"),
    Attr("Uninsured Motorists Symbols"),
    Attr("Underinsured Motorists Symbols"),
    Attr("Comprehensive Symbols"),
    Attr("Specified Causes Of Loss Symbols"),
    Attr("Collision Symbols"),
    Attr("Towing And Labor Symbols"),
    # Presence, not content: the benefit schedule for this endorsement is a page of
    # static wording, and it prints only when the quote carries the form. The
    # template tests it with `is provided`, so any value at all turns the page on.
    Attr("EA9911 Schedule"),
)

MODEL_LISTS: Sequence[ListOf] = (
    ListOf("Insurance Lines", "Insurance Line",
           attrs=(Attr("Name"),
                  Attr("Premium", "Currency"),
                  Attr("Terrorism Premium", "Currency"),
                  Attr("Total Premium", "Currency")),
           lists=(ListOf("Endorsements", "Endorsement",
                         attrs=(Attr("Description"),)),)),
    ListOf("Vehicles", "Vehicle",
           attrs=(Attr("Number"), Attr("State"), Attr("Year"), Attr("Make And Model"),
                  Attr("Class Code"), Attr("VIN"), Attr("Territory"),
                  Attr("Cost New", "Currency"), Attr("Stated Cost", "Currency"),
                  Attr("Premium", "Currency"), Attr("Is Leased")),
           lists=(ListOf("Coverages", "Coverage",
                         attrs=(Attr("Description"), Attr("Limit"),
                                Attr("Deductible"), Attr("Premium", "Currency"))),)),
    ListOf("Line Coverages", "Line Coverage",
           attrs=(Attr("Description"), Attr("Limit"),
                  Attr("Deductible"), Attr("Premium", "Currency"))),
    ListOf("Forms", "Form",
           attrs=(Attr("Coverage Line"), Attr("Form Number"),
                  Attr("Edition Date"), Attr("Description"))),
)


# ------------------------------------------------------------------------ naming


def concept_name(*path: str) -> str:
    return "_".join([LIB, ROOT, *path]) if path else f"{LIB}_{ROOT}"


def key(*path: str) -> str:
    """The GUID key. `Quote` alone for the root, `Quote.X.Y` below it — matching
    ProjectConcepts.AttrGuid("<attr>") = guid("Quote.<attr>")."""
    return ".".join([ROOT, *path]) if path else ROOT


def model_id(name: str) -> str:
    """`Model Library_Quote_Vehicles` -> `ModelLibrary_Quote_Vehicles`, the id form the
    compiled model uses."""
    return name.replace(" ", "")


def _x(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;")
             .replace(">", "&gt;").replace('"', "&quot;"))


# --------------------------------------------------------------- the .gdm fragment


def gdm_concepts() -> str:
    """Every `<concept>` the Quote model adds, in library order."""
    out: list[str] = []

    def leaf_attrs(attrs: Sequence[Attr], path: Sequence[str]) -> str:
        rows = []
        for a in attrs:
            g = guid(key(*path, a.name))
            rows.append(
                f'          <attribute name="{_x(a.name)}" guid="{g}" locked="false"'
                f' attributeState="RenderingBehaviour" hiddenConceptName="">\n'
                f'            <attributeRefs>\n'
                f'              <conceptName>{_x(a.type)}</conceptName>\n'
                f'            </attributeRefs>\n'
                f'          </attribute>')
        return "\n".join(rows)

    def list_ref(lst: ListOf, path: Sequence[str]) -> str:
        cn = concept_name(*path, lst.name)
        g = guid(key(*path, lst.name))
        return (f'          <attribute name="{_x(lst.name)}" guid="{g}" locked="false"'
                f' attributeState="AttributeWithKinds" hiddenConceptName="{_x(cn)}">\n'
                f'            <attributeRefs>\n'
                f'              <conceptName>{_x(cn)}</conceptName>\n'
                f'            </attributeRefs>\n'
                f'          </attribute>')

    def emit_class(name: str, g: str, attrs: Sequence[Attr],
                   lists: Sequence[ListOf], path: Sequence[str]) -> None:
        body = [leaf_attrs(attrs, path)] + [list_ref(l, path) for l in lists]
        out.append(
            f'      <concept name="{_x(name)}" guid="{g}" locked="false"'
            f' isHidden="true" isShared="false">\n'
            f'        <attributes>\n' + "\n".join(b for b in body if b) + "\n"
            f'        </attributes>\n'
            f'      </concept>')
        for l in lists:
            emit_list(l, path)

    def emit_list(lst: ListOf, path: Sequence[str]) -> None:
        here = [*path, lst.name]
        cn = concept_name(*here)
        items = cn + "_Elements"
        # The list itself. `First`/`Last` are the singleton selectors every real
        # ListConcept in the library carries; GhostDraft generates them and a list
        # without them is not what Studio writes.
        out.append(
            f'      <concept xsi:type="ListConcept" name="{_x(cn)}"'
            f' guid="{guid(key(*here))}" locked="false" isHidden="true"'
            f' isShared="false" elementName="{_x(lst.element)}"'
            f' hiddenConceptName="{_x(items)}">\n'
            f'        <selectors>\n'
            f'          <selector name="First" guid="{guid(key(*here, "sel.First"))}"'
            f' locked="false" grammer="Adjective" autoGeneratedType="First"'
            f' singleton="true" unique="false" hiddenConceptName=""'
            f' selectorElementName="" selectorType="SingletonOfDegree">\n'
            f'            <selectorKinds />\n'
            f'          </selector>\n'
            f'          <selector name="Last" guid="{guid(key(*here, "sel.Last"))}"'
            f' locked="false" grammer="Adjective" autoGeneratedType="Last"'
            f' singleton="true" unique="false" hiddenConceptName=""'
            f' selectorElementName="" selectorType="SingletonOfDegree">\n'
            f'            <selectorKinds />\n'
            f'          </selector>\n'
            f'        </selectors>\n'
            f'        <elementKinds>\n'
            f'          <conceptName>{_x(items)}</conceptName>\n'
            f'        </elementKinds>\n'
            f'      </concept>')
        emit_class(items, guid(key(*here, "Elements")), lst.attrs, lst.lists, here)

    emit_class(concept_name(), guid("concept:" + ROOT), MODEL_ATTRS, MODEL_LISTS, ())
    return "\n".join(out)


def gdm_domain_model() -> str:
    return (f'      <domainModel name="{ROOT}" guid="{guid(ROOT)}" locked="false"'
            f' onlyAccessableThroughSubscription="false"'
            f' hiddenConceptName="{_x(concept_name())}" />')


def splice_gdm(base_path: str, out_path: str) -> tuple[int, int]:
    base = open(base_path, encoding="utf-8-sig").read()
    concepts = gdm_concepts()
    domain = gdm_domain_model()
    if f'name="{concept_name()}"' in base:
        raise SystemExit("base library already declares the Quote concept -- "
                         "point --base at the untouched Model Library.gdm")
    i = base.index("</concepts>")
    base = base[:i] + concepts + "\n    " + base[i:]
    j = base.index("</domainModels>")
    base = base[:j] + domain + "\n    " + base[j:]
    os.makedirs(os.path.dirname(os.path.abspath(out_path)), exist_ok=True)
    open(out_path, "w", encoding="utf-8").write(base)
    return concepts.count("<concept "), concepts.count('xsi:type="ListConcept"')


# ------------------------------------------------------------- the compiled model


NS = "http://ghostdraft.korbitec.com/server/model-1"


def model_xml() -> str:
    """The projection tools/gdauthor.py resolves paths through, and the exact shape
    fact-docgen's Server XML must take."""
    out: list[str] = [
        '<?xml version="1.0" encoding="utf-8"?>',
        f'<m:model name="MoE Quote" id="MoEQuote" version="1.0" xmlns:m="{NS}">',
        '\t<m:root name="__root" id="__root">',
        f'\t\t<m:attribute name="{_x(ROOT)}" id="{_x(ROOT)}" guid="{guid(ROOT)}"'
        f' type="{model_id(concept_name())}" />',
        '\t</m:root>',
    ]

    def emit_class(name: str, attrs: Sequence[Attr], lists: Sequence[ListOf],
                   path: Sequence[str]) -> None:
        out.append(f'\t<m:class name="{_x(name)}" id="{model_id(name)}">')
        for a in attrs:
            out.append(f'\t\t<m:attribute name="{_x(a.name)}" id="{model_id(a.name)}"'
                       f' guid="{guid(key(*path, a.name))}" type="{_x(a.type)}" />')
        for l in lists:
            cn = concept_name(*path, l.name)
            out.append(f'\t\t<m:attribute name="{_x(l.name)}" id="{model_id(l.name)}"'
                       f' guid="{guid(key(*path, l.name))}" type="{model_id(cn)}" />')
        out.append('\t</m:class>')
        for l in lists:
            emit_list(l, path)

    def emit_list(lst: ListOf, path: Sequence[str]) -> None:
        here = [*path, lst.name]
        cn = concept_name(*here)
        item = model_id(cn) + "ListItem"
        out.append(f'\t<m:list name="{_x(cn)}" id="{model_id(cn)}"'
                   f' libraryName="{_x(LIB)}" libraryVersion="0.0"'
                   f' elementType="{item}" elementName="{model_id(lst.element)}"'
                   f' elementId="{model_id(lst.element)}" />')
        out.append(f'\t<m:class name="{_x(cn)}_Elements" id="{item}">')
        for a in lst.attrs:
            out.append(f'\t\t<m:attribute name="{_x(a.name)}" id="{model_id(a.name)}"'
                       f' guid="{guid(key(*here, a.name))}" type="{_x(a.type)}" />')
        for l in lst.lists:
            sub = concept_name(*here, l.name)
            out.append(f'\t\t<m:attribute name="{_x(l.name)}" id="{model_id(l.name)}"'
                       f' guid="{guid(key(*here, l.name))}" type="{model_id(sub)}" />')
        out.append('\t</m:class>')
        for l in lst.lists:
            emit_list(l, here)

    emit_class(concept_name(), MODEL_ATTRS, MODEL_LISTS, ())
    out.append('</m:model>')
    return "\n".join(out) + "\n"


# ------------------------------------------------------------------- description


def describe() -> str:
    lines = [f"{ROOT}  ({guid(ROOT)})"]
    for a in MODEL_ATTRS:
        lines.append(f"  {a.name:<38} {a.type:<9} {guid(key(a.name))}")

    def walk(lst: ListOf, path: Sequence[str], indent: str) -> None:
        here = [*path, lst.name]
        lines.append(f"{indent}{lst.name}[]  <{lst.element}>   {guid(key(*here))}")
        for a in lst.attrs:
            lines.append(f"{indent}  {a.name:<36} {a.type:<9} {guid(key(*here, a.name))}")
        for l in lst.lists:
            walk(l, here, indent + "  ")

    for l in MODEL_LISTS:
        walk(l, (), "  ")
    return "\n".join(lines)


# -------------------------------------------------------------------------- CLI


def main(argv: Sequence[str]) -> int:
    repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    default_base = os.path.join(
        os.path.expanduser("~"), "OneDrive - Mutual of Enumclaw Insurance Company",
        "Documents", "GhostDraft Studio", "Cody's World", "Resources",
        "Model Libraries", "Model Library.gdm")
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--base", default=default_base, help="the untouched Model Library.gdm")
    ap.add_argument("--gdm", nargs="?", const=os.path.join(
        repo, "output", "concept-library", "Model Library (with Quote).gdm"))
    ap.add_argument("--model", nargs="?", const=os.path.join(
        repo, "output", "quote-poc", "quote-model.xml"))
    ap.add_argument("--print", action="store_true", dest="show")
    args = ap.parse_args(argv)

    if args.show or not (args.gdm or args.model):
        print(describe())

    if args.gdm:
        n, lists = splice_gdm(args.base, args.gdm)
        print(f"\nwrote {args.gdm}")
        print(f"  base   {args.base}")
        print(f"  added  {n} concept(s), {lists} of them lists, "
              f"1 domainModel '{ROOT}' ({guid(ROOT)})")

    if args.model:
        os.makedirs(os.path.dirname(os.path.abspath(args.model)), exist_ok=True)
        open(args.model, "w", encoding="utf-8").write(model_xml())
        print(f"wrote {args.model}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
