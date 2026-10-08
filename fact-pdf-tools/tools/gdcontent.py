#!/usr/bin/env python3
"""
gdcontent.py -- does the authored template actually say what the forms say?

Products asked how static text could go missing from a template built out of a form
library. It went missing because the sections were transcribed from the RENDERED
PAGES rather than from the forms: whatever I did not happen to look at was never
written, and nothing in the pipeline could tell. The first audit found 97 of the
packet's 210 static strings absent -- including the whole EA 99 11 03 18 benefit
schedule and the whole Available Payment Plans table, two entire pages.

So this is the check that would have caught it, kept as a gate rather than a
one-off. For every image the packet places, it takes the static text `emit-html`
produces from the FAP and asks whether the authored `.gd` contains it.

    python tools/gdcontent.py                       # the CPP Commercial Auto packet
    python tools/gdcontent.py --verbose             # list every missing string

Two escaping details matter and both bit the first version of this script:

  * a `.gd` XML-escapes its RTF, so `1=ANY "AUTO."` is stored as `1=ANY &quot;AUTO.&quot;`
    and a raw comparison reports ten perfectly good strings as missing;
  * the emit-html fragment HTML-escapes the same way.

Unescape both, or the audit invents work.
"""

from __future__ import annotations

import argparse
import html
import os
import re
import sys
from typing import Sequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ddtpacket as dp  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FRAG = os.path.join(REPO, "output", "quote-poc", "frag")
BS = chr(92)

# The FORM.DAT entries the POC packet is assembled from.
PACKET = ["QUOTE COVER.4", "QUOTE CPPSUM.2", "QUOTE CPPCAVS.3",
          "QUOTE CPPCA.3", "QUOTE CPP FORMS.1"]

# Fields, not prose: a fragment span that is only a control character or a lone
# separator is not content anyone would miss.
NOISE = re.compile(r"^[\s\-_.,:;|/\\]*$")

# A gate must never invent work. Two kinds of "missing" are correct by design and
# are declared here rather than left to be re-investigated every run.
#
# SUPPLIED_BY_DATA -- the string is in the FAP because the legacy form hard-codes one
# row per insurance line. Our template has ONE data-driven row and takes the name from
# `Insurance Lines[].Name`, so the words are in the Server XML, not the template.
SUPPLIED_BY_DATA = {
    "QCPPSUM_CP_A", "QCPPSUM_GL_A", "QCPPSUM_CR_A", "QCPPSUM_IM_A", "QCPPSUM_PL_A",
    "QCPPSUM_CA_A",
}

# VARIANTS -- an alternate edition or a state-specific page the packet selects
# BETWEEN. The template carries the one this quote resolves to; carrying all of them
# is a scope decision, not an omission, and it is listed so the decision stays visible.
VARIANTS = {
    "QTE_EA9911F", "QTE_EA9911D", "QTE_EA9910D", "QTE_EA9910E",
    "QTE_AUTOSCHED_MT_A", "QCPPSUM_TERR",
}


def packet_images(lob: str, forms: Sequence[str]) -> list[str]:
    entries = {e.name.upper(): e for e in dp.read_formdat(dp.DEFAULT_FORMDAT)
               if e.lob.upper() == lob.upper()}
    out: list[str] = []
    for name in forms:
        entry = entries.get(name.upper())
        if entry is None:
            continue
        for image in entry.images:
            for node in dp.walk(dp.DEFAULT_DDTDIR, dp.DEFAULT_FAPDIR, image):
                if node.image not in out:
                    out.append(node.image)
    return out


def fragment_text(image: str) -> list[str] | None:
    path = os.path.join(FRAG, image + ".html")
    if not os.path.exists(path):
        return None
    body = open(path, encoding="utf-8").read()
    return [t for t in
            (html.unescape(m.group(1)).strip() for m in
             re.finditer(r'<span class="abs"[^>]*>([^<]*)</span>', body))
            if t and not NOISE.match(t)]


def template_text(path: str) -> str:
    s = open(path, encoding="utf-8").read()
    i, j = s.find("<rtf>"), s.find("</rtf>")
    rtf = s[i:j]
    # Drop embedded pictures before stripping control words -- a hex blob is
    # megabytes of noise and can contain anything.
    rtf = re.sub(r"\{" + re.escape(BS) + r"\*" + re.escape(BS)
                 + r"shppict.*?pngblip\n[0-9a-f]+\}\}\}\}", "", rtf, flags=re.S)
    rtf = html.unescape(rtf)
    plain = re.sub(re.escape(BS) + r"[a-zA-Z]+-?\d*[ ]?", " ", rtf)
    plain = plain.replace("{", " ").replace("}", " ")
    return re.sub(r"\s+", " ", plain).lower()


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--template", default=os.path.join(
        REPO, "output", "quote-poc", "Quote Proposal.gd"))
    ap.add_argument("--lob", default="CPP")
    ap.add_argument("--form", action="append", default=[])
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args(argv)

    tpl = template_text(args.template)
    images = packet_images(args.lob, args.form or PACKET)

    total = present = 0
    missing: dict[str, list[str]] = {}
    nofrag: list[str] = []
    for image in images:
        texts = fragment_text(image)
        if texts is None:
            nofrag.append(image)
            continue
        miss = []
        for t in texts:
            total += 1
            if re.sub(r"\s+", " ", t).lower() in tpl:
                present += 1
            else:
                miss.append(t)
        if miss:
            missing[image] = miss

    real = {k: v for k, v in missing.items()
            if k not in SUPPLIED_BY_DATA and k not in VARIANTS}
    bydata = sum(len(v) for k, v in missing.items() if k in SUPPLIED_BY_DATA)
    variant = {k: v for k, v in missing.items() if k in VARIANTS}

    accounted = present + bydata + sum(len(v) for v in variant.values())
    print(f"{os.path.basename(args.template)}")
    print(f"  packet images checked   {len(images) - len(nofrag)} of {len(images)}")
    print(f"  static strings          {total}")
    print(f"  in the template         {present}")
    print(f"  supplied by data        {bydata}   (the per-line row labels)")
    print(f"  variants not carried    {sum(len(v) for v in variant.values())}"
          f"   ({', '.join(sorted(variant))})" if variant else
          "  variants not carried    0")
    print(f"  ACCOUNTED FOR           {accounted} of {total} ({accounted/total:.0%})")
    if nofrag:
        print(f"  NO FRAGMENT for {len(nofrag)}: {', '.join(nofrag[:8])}"
              " -- run tools/htmlpacket.py to cache them")
    print()
    if not real:
        print("PASS -- nothing the packet's forms carry is unaccounted for")
        return 0
    print(f"MISSING from {len(real)} image(s):")
    for image, miss in sorted(real.items(), key=lambda kv: -len(kv[1])):
        print(f"  {image:<22} {len(miss):>3}")
        for t in (miss if args.verbose else miss[:4]):
            print(f"      {t[:96]}")
        if not args.verbose and len(miss) > 4:
            print(f"      … {len(miss) - 4} more (--verbose)")
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
