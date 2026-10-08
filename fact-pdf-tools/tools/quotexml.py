#!/usr/bin/env python3
"""
quotexml.py -- the packet data document -> GhostDraft Server XML.

`demo -- quote-data` emits what the packet needs, keyed by FAP field name, and
tools/htmlpacket.py renders it. This turns the SAME document into the Server XML the
GhostDraft template binds to, so both renderers are fed from one source and any value
that differs between them is a bug in a renderer rather than a data question.

The element names are not invented here: they come from tools/quotemodel.py, the same
declaration that generates the concept library and the compiled model. A list lives at
`<Name><Items><Element>…`, which is what `gdmodel.resolve_list` reports
(`Quote/Vehicles/Items/Vehicle`) and what the package XSD confirms.

**This file is also the contract for fact-docgen.** Whatever produces the Server XML in
production -- an `ISectionBuilder` in the DocGen project -- has to produce exactly this
shape. Running this against a real quote gives that team a worked example rather than a
specification to interpret.

Usage
    demo -- quote-data BAP000080307 tst Pending output/quote-poc/BAP000080307.json
    python tools/quotexml.py output/quote-poc/BAP000080307.json \
        --out output/quote-poc/BAP000080307.serverxml
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from typing import Any, Sequence
from xml.sax.saxutils import escape

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import quotemodel as qm  # noqa: E402


# --------------------------------------------------------------- the data document


class Data:
    """Instances of one image, in packet order, as `quote-data` wrote them."""

    def __init__(self, doc: dict) -> None:
        self.policy = doc.get("PolicyNumber") or doc.get("policyNumber") or ""
        self.images: dict[str, dict] = {}
        for name, entry in (doc.get("Images") or doc.get("images") or {}).items():
            self.images[name.upper()] = {
                "parent": entry.get("Parent") or entry.get("parent"),
                "instances": entry.get("Instances") or entry.get("instances") or [],
            }

    def rows(self, image: str, parent_index: int | None = None) -> list[dict[str, str]]:
        e = self.images.get(image.upper())
        if not e:
            return []
        out = []
        for r in e["instances"]:
            if parent_index is not None:
                if int(r.get("ParentIndex", r.get("parentIndex", -1))) != parent_index:
                    continue
            out.append({k: v for k, v in
                        (r.get("Fields") or r.get("fields") or {}).items()})
        return out

    def one(self, image: str) -> dict[str, str]:
        r = self.rows(image)
        return r[0] if r else {}


# ------------------------------------------------------------------- XML emission


class Xml:
    def __init__(self) -> None:
        self.parts: list[str] = []
        self.depth = 0

    def open(self, tag: str) -> "Xml":
        self.parts.append("  " * self.depth + f"<{tag}>")
        self.depth += 1
        return self

    def close(self, tag: str) -> "Xml":
        self.depth -= 1
        self.parts.append("  " * self.depth + f"</{tag}>")
        return self

    def leaf(self, tag: str, value: str) -> "Xml":
        """An EMPTY value is omitted, not written as an empty element. GhostDraft's
        `is provided` test -- which every conditional in the template turns on -- is
        false for an absent element and true for an empty one, so writing `<Limit/>`
        would print a label with nothing after it."""
        if value is None or str(value) == "":
            return self
        self.parts.append("  " * self.depth
                          + f"<{tag}>{escape(str(value))}</{tag}>")
        return self

    def __str__(self) -> str:
        return "\n".join(self.parts) + "\n"


def eid(name: str) -> str:
    """The element name for a model member — `Make And Model` -> `MakeAndModel`."""
    return qm.model_id(name)


def emit_list(x: Xml, list_name: str, element: str, rows: Sequence[dict],
              write_row) -> None:
    """`<Vehicles><Items><Vehicle>…</Vehicle>…</Items></Vehicles>`, omitted entirely
    when there are no rows so `is provided` is false for the whole table."""
    if not rows:
        return
    x.open(eid(list_name)).open("Items")
    for i, row in enumerate(rows):
        x.open(eid(element))
        write_row(x, row, i)
        x.close(eid(element))
    x.close("Items").close(eid(list_name))


# ------------------------------------------------------- data document -> model


# The page-3 summary rows, and the CDM line each stands for. `_A` is the TRIA variant,
# which is the one the reference document prints and the one this reads.
SUMMARY_ROWS = [
    ("QCPPSUM_CA_A", "Commercial Auto", "CAPRM", "CATERR", "CAPREM", "QCPPSUMDTL_CA"),
    ("QCPPSUM_CP_A", "Commercial Property", "CPPRM", "CPTERR", "CPPREM", "QCPPSUMDTL_CP"),
    ("QCPPSUM_GL_A", "Commercial General Liability", "GLPRM", "GLTERR", "GLPREM",
     "QCPPSUMDTL_GL"),
    ("QCPPSUM_CR_A", "Commercial Crime", "CRPRM", "CRTERR", "CRPREM", "QCPPSUMDTL_CR"),
    ("QCPPSUM_IM_A", "Commercial Inland Marine", "IMPRM", "IMTERR", "IMPREM",
     "QCPPSUMDTL_IM"),
    ("QCPPSUM_PL_A", "Commercial Professional Liability", "PLPRM", "PLTERR", "PLPREM",
     "QCPPSUMDTL_PL"),
]

VEHICLE_TYPES = [
    ("Light Trucks", "LIGHT"), ("Medium Trucks", "MEDIUM"),
    ("Heavy Trucks", "HEAVY"), ("Extra Heavy Trucks", "XHEAVY"),
    ("Trailers", "TRAILERS"), ("Buses", "BUSES"),
    ("Private Passenger Autos", "PPTS"), ("All Other Vehicles", "ALLOTHERS"),
]

SYMBOLS = [
    ("Liability Symbols", "LIABCAS", 4),
    ("Personal Injury Protection Symbols", "PIPCAS", 2),
    ("Medical Payments Symbols", "MPCAS", 2),
    ("Uninsured Motorists Symbols", "UMCAS", 3),
    ("Underinsured Motorists Symbols", "UNCAS", 2),
    ("Comprehensive Symbols", "COMPCAS", 2),
    ("Specified Causes Of Loss Symbols", "SCLCAS", 2),
    ("Collision Symbols", "COLCAS", 2),
    ("Towing And Labor Symbols", "TLCAS", 2),
]


def build(d: Data) -> str:
    x = Xml()
    x.open(qm.ROOT)

    hdr = d.one("QTE_HDR")
    cover = d.one("QTE_COVER_A")
    x.leaf(eid("Quote Number"), hdr.get("POLICYNBR", ""))
    x.leaf(eid("Insured Name"), hdr.get("INSURED NAME1", ""))
    x.leaf(eid("Insured Name 2"), hdr.get("INSURED NAME2", ""))
    x.leaf(eid("Agent Name"), cover.get("AGENT NAME", ""))
    x.leaf(eid("Agent Phone"), cover.get("AGENT PHONE", ""))
    x.leaf(eid("Proposal Period"), cover.get("PROPOSAL PERIOD", ""))

    total = d.one("QCPPSUM_TOTAL_A")
    x.leaf(eid("Total Premium"), total.get("TOTPREM", ""))
    x.leaf(eid("Total Terrorism Premium"), total.get("TOTTERR", ""))
    x.leaf(eid("Total Premium With Terrorism"), total.get("TOTALPREMIUM", ""))

    ftr = d.one("QCPP_CAVS_FTR")
    x.leaf(eid("Auto Vehicles Premium"), ftr.get("CA_VEHS_TOTAL_PREM", ""))
    x.leaf(eid("Auto Line Premium"), ftr.get("CA_INSL_TOTAL_PREM", ""))
    x.leaf(eid("Auto Total Premium"), ftr.get("CA_TOTAL_PREM", ""))
    x.leaf(eid("Vehicle Count"), ftr.get("TOTVEHS", ""))

    vts = d.one("QCPP_CAVS_VTS")
    for attr, field in VEHICLE_TYPES:
        x.leaf(eid(attr), vts.get(field, ""))

    # The endorsement benefit schedules the packet selected. quote-data already
    # decided which edition applies, so presence of the image is the flag.
    if d.rows("QTE_EA9911E"):
        x.leaf(eid("EA9911 Schedule"), "Yes")

    sym = d.one("QTE_COVAUTOSYM")
    for attr, prefix, cells in SYMBOLS:
        joined = " ".join(v for v in
                          (sym.get(f"{prefix}{i}", "") for i in range(1, cells + 1))
                          if v)
        x.leaf(eid(attr), joined)

    # ---- Insurance Lines, with their endorsements nested
    lines = []
    for image, name, prem, terr, tot, detail in SUMMARY_ROWS:
        rows = d.rows(image)
        if not rows:
            continue
        r = rows[0]
        lines.append((name, r.get(prem, ""), r.get(terr, ""), r.get(tot, ""),
                      [e.get("DESCRIPTION", "") for e in d.rows(detail, 0)]))

    def write_line(xx: Xml, row: Any, _i: int) -> None:
        name, prem, terr, tot, endorsements = row
        xx.leaf(eid("Name"), name)
        xx.leaf(eid("Premium"), prem)
        xx.leaf(eid("Terrorism Premium"), terr)
        xx.leaf(eid("Total Premium"), tot)
        emit_list(xx, "Endorsements", "Endorsement",
                  [{"d": e} for e in endorsements if e],
                  lambda x2, r2, _j: x2.leaf(eid("Description"), r2["d"]))

    emit_list(x, "Insurance Lines", "Insurance Line", lines, write_line)

    # ---- Vehicles, with their coverages nested.
    # The summary row and the detail block are two images of the SAME unit, joined on
    # position -- which is safe because both are emitted from Policy.InsuredAssets in
    # InsuredAssetNumber order.
    summary = d.rows("QCPP_CAVS_VEHDET_B")
    detail = d.rows("QCPP_CAV_B", 0)

    def write_vehicle(xx: Xml, row: Any, i: int) -> None:
        s, det = row
        xx.leaf(eid("Number"), s.get("VEH NUM", ""))
        xx.leaf(eid("State"), s.get("VEH STATE", ""))
        xx.leaf(eid("Year"), s.get("VEH YEAR", ""))
        xx.leaf(eid("Make And Model"), s.get("VEH MM", ""))
        xx.leaf(eid("Class Code"), s.get("VEH CLASS", ""))
        xx.leaf(eid("VIN"), s.get("VEH VIN", ""))
        xx.leaf(eid("Territory"), det.get("VEH1 TERRITORY", ""))
        xx.leaf(eid("Cost New"), det.get("VEH1 COSTNEW", ""))
        xx.leaf(eid("Stated Cost"), det.get("VEH1STCOST", ""))
        xx.leaf(eid("Premium"), s.get("VEH TOT PREM", ""))
        # `LV` is the leased marker the DDT prints as "*"; the template turns the
        # asterisk on with `is provided`, so the VALUE only has to be present.
        xx.leaf(eid("Is Leased"), "Yes" if s.get("LV", "") else "")
        emit_list(xx, "Coverages", "Coverage", d.rows("QCPP_CAV1", i),
                  lambda x2, c, _j: (x2.leaf(eid("Description"), c.get("COVERAGE", ""))
                                     .leaf(eid("Limit"), c.get("LIMIT", ""))
                                     .leaf(eid("Deductible"), c.get("DEDUCTIBLE", ""))
                                     .leaf(eid("Premium"), c.get("PREMIUM", ""))))

    pairs = [(s, detail[i] if i < len(detail) else {}) for i, s in enumerate(summary)]
    emit_list(x, "Vehicles", "Vehicle", pairs, write_vehicle)

    # ---- the line-level coverage table
    emit_list(x, "Line Coverages", "Line Coverage", d.rows("QCPP_CAA", 0),
              lambda xx, c, _i: (xx.leaf(eid("Description"), c.get("COVERAGE", ""))
                                 .leaf(eid("Limit"), c.get("LIMIT", ""))
                                 .leaf(eid("Deductible"), c.get("DEDUCTIBLE", ""))
                                 .leaf(eid("Premium"), c.get("PREMIUM", ""))))

    # ---- the forms schedule: three row images, one table
    forms: list[dict[str, str]] = []
    for r in d.rows("QTE_CPP95DP"):
        forms.append({"line": r.get("dpCOVLINE", ""), "num": r.get("dpFORMNUM", ""),
                      "ed": r.get("dpEDATE", ""), "desc": r.get("dpFORMNAME", "")})
    for image in ("QTE_CPP95IL", "QTE_CPP95BD"):
        for r in d.rows(image):
            forms.append({"line": r.get("COVLINE", ""), "num": r.get("FORMNUM", ""),
                          "ed": r.get("EDATE", ""), "desc": r.get("FORMNAME", "")})
    emit_list(x, "Forms", "Form", forms,
              lambda xx, f, _i: (xx.leaf(eid("Coverage Line"), f["line"])
                                 .leaf(eid("Form Number"), f["num"])
                                 .leaf(eid("Edition Date"), f["ed"])
                                 .leaf(eid("Description"), f["desc"])))

    x.close(qm.ROOT)
    return '<?xml version="1.0" encoding="utf-8"?>\n' + str(x)


# -------------------------------------------------------------------------- CLI


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("data", help="the quote-data JSON")
    ap.add_argument("--out")
    args = ap.parse_args(argv)

    d = Data(json.load(open(args.data, encoding="utf-8")))
    xml = build(d)
    out = args.out or os.path.splitext(args.data)[0] + ".serverxml"
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    open(out, "w", encoding="utf-8").write(xml)

    import re
    print(f"policy   {d.policy}")
    print(f"elements {len(re.findall(r'<[A-Za-z]', xml))}")
    for tag in ("InsuranceLine", "Endorsement", "Vehicle", "Coverage",
                "LineCoverage", "Form"):
        n = len(re.findall(rf"<{tag}>", xml))
        if n:
            print(f"  {tag:<14}{n}")
    print(f"wrote    {out} ({os.path.getsize(out):,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
