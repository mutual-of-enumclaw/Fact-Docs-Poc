"""Tier 1 acceptance gate: does our render say exactly what the legacy render says?

A missing space is a CONTENT error -- it can change what a filed form states -- while a
uniform 1pt offset is invisible to a reviewer. Pixel metrics rank those backwards. This
compares the page's text in reading order, so it catches dropped spaces, dropped glyphs
and wrong characters, and ignores sub-pixel placement entirely.

Declared `F,` field regions are removed from BOTH renders first -- a blank legacy render
shows placeholder chrome (underscore fill, Documaker-computed page counts) that a filled
production document never has. See field_boxes().

Usage:  python tools/contentdiff.py [FORM ...]     (default: every form in the last sweep)
"""
import collections
import csv
import difflib
import pathlib
import re
import sys

import fitz

WORK = pathlib.Path(r"C:\src\fact-pdf-tools\output\sweep-work")
POS_TOL = 1.5   # pt; a spacing-only merge whose ends agree within this is an extraction artefact
LINE_TOL = 2.5  # pt; a baseline band, wide enough to absorb sub-point render differences
# Padding on a declared field rect. A glyph's bbox is the font's line box, so it is
# taller than the field's declared height; 4pt vertical covers that without reaching
# the next line. Deliberately small -- see the M7901A note in field_boxes().
FIELD_PAD_X, FIELD_PAD_Y = 1.5, 4.0
SWEEP = pathlib.Path(r"C:\src\fact-pdf-tools\output\fidelity-sweep.csv")

FIELD_RE = re.compile(
    r'<section class="form-page" data-page="(\d+)">|'
    r'<span class="abs field" data-field="[^"]*" data-maxlen="\d+" '
    r'style="left:([-\d.]+)pt;top:([-\d.]+)pt;width:([-\d.]+)pt;height:([-\d.]+)pt"')


FORMS = pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS")
FAP_TEXT_RE = re.compile(r"^(?:M,TT|T),\([^)]*\),\([^)]*\),\d+,(.*)$")


def fap_static_char_count(form, ch):
    """How many times `ch` appears in the FAP's own static text.

    This is the authority for fill characters. Documaker renders an UNFILLED field as a
    run of underscores whose length follows the field's BOX, not its declared length, so
    the fill spills outside the rect field_boxes() can exclude -- 210 stray underscores
    on IM74054O against 58 declared characters. Rather than widen the geometry until it
    swallows real static text, count what the FAP actually declares: every underscore in
    the source must appear in our render, and any EXCESS in the legacy render is fill by
    construction. Returns None if the FAP cannot be read, in which case no allowance is
    made.
    """
    p = FORMS / f"{form}.FAP"
    if not p.exists():
        return None
    n = 0
    for raw in p.read_bytes().decode("cp1252", errors="replace").splitlines():
        m = FAP_TEXT_RE.match(raw.strip())
        if m:
            n += m.group(1).count(ch)
    return n


def field_boxes(form):
    """page index -> padded rects of every FAP `F,` field, read from the emitted HTML.

    Comparing BLANK renders conflates "we lost content" with "Documaker drew
    placeholder chrome": an unfilled field renders as underscore fill in the legacy
    PDF, and Documaker-computed system values (total page count) land in a field we
    do not populate. Neither is a content defect, and a filed form's real deliverable
    is the FILLED document. So both sides are compared with field regions removed.

    Caveat, measured on M7901A: Documaker's underscore fill can extend well beyond
    the declared rect (a 100pt field filling ~250pt), so a residue survives. Widening
    the padding far enough to catch it would start swallowing real static text, which
    is the wrong trade for a filed-forms gate -- the residue is reported instead.
    """
    html = WORK / f"{form}.html"
    if not html.exists():
        return {}
    out, page = {}, -1
    for m in FIELD_RE.finditer(html.read_text(encoding="utf-8", errors="replace")):
        if m.group(1):
            page = int(m.group(1)) - 1
            out.setdefault(page, [])
        else:
            x, y, w, h = (float(m.group(i)) for i in (2, 3, 4, 5))
            out.setdefault(page, []).append(
                (x - FIELD_PAD_X, y - FIELD_PAD_Y, x + w + FIELD_PAD_X, y + h + FIELD_PAD_Y))
    return out


def in_field(x0, y0, x1, y1, boxes):
    """Is this glyph's centre inside a field rect? Centre, not overlap: PyMuPDF's
    boxes abut their neighbours, so an overlap test would leak into adjacent text."""
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    return any(bx0 <= cx <= bx1 and by0 <= cy <= by1 for bx0, by0, bx1, by1 in boxes)


def page_chars(pdf, page, boxes):
    """Non-space characters of a page in reading order, split into
    (kept, excluded-as-field-region).

    Character granularity is required: a word-level test cannot separate field fill
    from real text because PyMuPDF merges a field's underscore run together with the
    static label beside it into one 'word'.

    Ordered by the same baseline band as page_text(), NOT by PDF content-stream
    order -- the two renders emit their drawing operations in different sequences, so
    stream order makes the order-sensitive diagnostic report differences that reading
    order does not have.
    """
    d = fitz.open(str(pdf))
    if page >= d.page_count:
        return None, None
    kept, excl = [], []
    for blk in d[page].get_text("rawdict")["blocks"]:
        for ln in blk.get("lines", []):
            for sp in ln.get("spans", []):
                for ch in sp.get("chars", []):
                    # Control characters are PDF artefacts, not content: FAP2PDF emits a
                    # stray U+001B on 372nsN50. PyMuPDF's word extractor drops them, so
                    # keeping them here would fail a form the word-level gate passed.
                    if not ch["c"].strip() or ch["c"] < " ":
                        continue
                    x0, _, _, y1 = ch["bbox"]
                    (excl if in_field(*ch["bbox"], boxes) else kept).append((ch["c"], x0, y1))

    ordered, band = [], None
    for c in sorted(kept, key=lambda t: (t[2], t[1])):
        if band is None or abs(c[2] - band[0]) > LINE_TOL:
            band = (c[2], [])
            ordered.append(band)
        band[1].append(c)
    return ([c[0] for _, cs in ordered for c in sorted(cs, key=lambda t: t[1])],
            [c[0] for c in excl])


def page_text(pdf, page, boxes=()):
    """Text in reading order, with runs of whitespace collapsed to one space.

    Collapsing does NOT hide a missing space: 'Agreement under' stays two words while
    'Agreementunder' stays one, so the diff still fires.
    """
    d = fitz.open(str(pdf))
    if page >= d.page_count:
        return None

    # Cluster words into lines before ordering. Sorting on a rounded y is not safe:
    # the two renders differ by fractions of a point, so words on the SAME visual line
    # sort into different sequences and the diff reports phantom moves. Group by a
    # baseline band instead, then order left-to-right within the band.
    words = sorted((w for w in d[page].get_text("words")
                    if not in_field(w[0], w[1], w[2], w[3], boxes)),
                   key=lambda w: (w[3], w[0]))
    lines, band = [], None
    for w in words:
        if band is None or abs(w[3] - band[0]) > LINE_TOL:
            band = (w[3], [])
            lines.append(band)
        band[1].append(w)
    return [(re.sub(r"\s+", " ", w[4]).strip(), w[0], w[2])
            for _, ws in lines for w in sorted(ws, key=lambda w: w[0])
            if w[4].strip()]


def compare(form):
    legacy, ours = WORK / f"{form}.PDF", WORK / f"{form}_ours.pdf"
    if not legacy.exists() or not ours.exists():
        return None
    try:
        npages = fitz.open(str(legacy)).page_count
    except Exception:
        return None

    # Headline test: does the page say the same thing? Compare the whole character
    # stream with ALL whitespace removed. Word-level diffing mis-attributes characters
    # across token boundaries -- a quote emitted at the end of one span and the start of
    # the next reads as a dropped glyph when nothing was dropped at all. Character
    # equality cannot be fooled that way; word-level detail below is for locating things.
    fields = field_boxes(form)

    chars_l = chars_o = excluded = 0
    chars_equal = True
    # Multiset equality answers "did we DROP anything", independent of reading order.
    # Stream equality also requires the same order -- but multi-column pages legitimately
    # linearise differently in the two renders, so an order difference is usually the
    # comparison's problem, not the render's. Report them separately.
    bag_l, bag_o, filtered_o = (collections.Counter(), collections.Counter(),
                                collections.Counter())
    for p in range(npages):
        boxes = fields.get(p, ())
        kl, el = page_chars(legacy, p, boxes)
        ko, eo = page_chars(ours, p, boxes)
        if kl is None or ko is None:
            continue
        ca, cb = "".join(kl), "".join(ko)
        chars_l += len(ca)
        chars_o += len(cb)
        excluded += len(el) + len(eo)
        bag_l.update(ca)
        bag_o.update(cb)
        filtered_o.update(eo)
        if ca != cb:
            chars_equal = False

    total_l = total_match = artefacts = 0
    examples = []
    for p in range(npages):
        boxes = fields.get(p, ())
        aw, bw = page_text(legacy, p, boxes), page_text(ours, p, boxes)
        if aw is None or bw is None:
            continue
        at, bt = [w[0] for w in aw], [w[0] for w in bw]
        total_l += len(at)
        sm = difflib.SequenceMatcher(None, at, bt, autojunk=False)
        for tag, i1, i2, j1, j2 in sm.get_opcodes():
            if tag == "equal":
                total_match += i2 - i1
                continue

            # Spacing-only difference? Then the SAME characters are present and the
            # only question is whether the ink is in the right place. We emit one
            # positioned span per token while Documaker emits a separate text-showing
            # op per token, so PyMuPDF merges neighbours whose boxes abut even when
            # the render is correct. Failing that would reject renders already
            # accepted by eye -- the exact mistake that disqualified ink IoU.
            la, lb = "".join(at[i1:i2]), "".join(bt[j1:j2])
            if la and la == lb and i2 > i1 and j2 > j1:
                lx0, lx1 = aw[i1][1], aw[i2 - 1][2]
                ox0, ox1 = bw[j1][1], bw[j2 - 1][2]
                if abs(lx0 - ox0) <= POS_TOL and abs(lx1 - ox1) <= POS_TOL:
                    total_match += i2 - i1      # same glyphs, same place -> passes
                    artefacts += i2 - i1
                    continue
                if len(examples) < 3:
                    examples.append(f"p{p+1} SPACING+MOVED: {' '.join(at[i1:i2])[:34]!r} "
                                    f"x {lx0:.1f}-{lx1:.1f} vs {ox0:.1f}-{ox1:.1f}")
                continue

            if len(examples) < 3:
                examples.append(f"p{p+1} {tag}: legacy={' '.join(at[i1:i2])[:40]!r} "
                                f"ours={' '.join(bt[j1:j2])[:40]!r}")
    if total_l == 0:
        return None
    # The exclusion decides per render, from that render's own glyph positions, so a
    # glyph straddling a field boundary can be filtered on one side only -- measured on
    # DFP0014F, whose field rect overlaps its own static label and whose 'y' lands
    # 0.1pt inside our box and outside legacy's. Discounting what we filtered ourselves
    # means the filter can never MANUFACTURE a drop, only ever miss one. That is the
    # right direction: a gate that fails a correct render is the mistake that
    # disqualified ink IoU.
    dropped = (bag_l - bag_o) - filtered_o
    # Fill-character allowance. Only excuse a missing fill char once our render is proved
    # to carry every one the FAP declares, so a genuinely dropped underscore still fails
    # while Documaker's unfilled-field fill does not.
    fill_excused = 0
    for ch in ("_",):
        if dropped.get(ch):
            declared = fap_static_char_count(form, ch)
            # Our TOTAL count, including chars inside field rects: those were excluded
            # from the comparison but they are still characters we rendered.
            ours_total = bag_o.get(ch, 0) + filtered_o.get(ch, 0)
            if declared is not None and ours_total >= declared:
                fill_excused += dropped[ch]
                del dropped[ch]
    return {"form": form, "words": total_l, "artefacts": artefacts,
            "nothing_dropped": not dropped, "field_chars_excluded": excluded,
            "fill_excused": fill_excused,
            "dropped": "".join(f"{c}x{n} " for c, n in dropped.most_common(6)),
            "chars_equal": chars_equal, "chars": chars_l, "chars_ours": chars_o,
            "match_pct": round(100 * total_match / total_l, 1), "examples": examples}


def main(forms):
    results = [r for r in (compare(f) for f in forms) if r]
    results.sort(key=lambda r: r["match_pct"])

    print(f"{'form':<17}{'words':>7}{'content match':>15}")
    for r in results:
        print(f"{r['form']:<17}{r['words']:>7}{r['match_pct']:>14.1f}%")
        for e in r["examples"]:
            print(f"      {e}")

    # THE GATE. Order-insensitive, so it cannot be perturbed by how a multi-column page
    # happens to linearise -- and it asks the question that actually matters: did any
    # character of the legacy document fail to appear in ours. The order-sensitive figure
    # below moved 40->38->39->38 across calibration changes that altered no content at
    # all, so it is a diagnostic and must never be used as pass/fail.
    nod = [r for r in results if r["nothing_dropped"]]
    ex_total = sum(r["field_chars_excluded"] for r in results)
    ex_forms = sum(1 for r in results if r["field_chars_excluded"])
    print(f"\nTIER 1 GATE -- nothing dropped: {len(nod)}/{len(results)} forms")
    fx = sum(r.get("fill_excused", 0) for r in results)
    if fx:
        nf = sum(1 for r in results if r.get("fill_excused"))
        print(f"   (unfilled-field fill excused: {fx} chars across {nf} forms -- "
              f"our render carries every one the FAP declares)")
    print(f"   (field regions excluded from both sides: {ex_total} chars across {ex_forms} forms)")
    for r in results:
        if not r["nothing_dropped"]:
            print(f"   DROPS {r['form']:<17} {r['dropped']}")

    ident = [r for r in results if r["chars_equal"]]
    print(f"\n(diagnostic, NOT a gate) stream identical incl. order: {len(ident)}/{len(results)} forms")
    for r in results:
        if not r["chars_equal"]:
            print(f"   FAIL {r['form']:<17} legacy {r['chars']} chars vs ours {r['chars_ours']}")

    perfect = [r for r in results if r["match_pct"] == 100.0]
    near = [r for r in results if 99.0 <= r["match_pct"] < 100.0]
    print(f"\n{len(perfect)}/{len(results)} forms 100% content-identical; "
          f"{len(near)} at 99-100%; "
          f"{len(results) - len(perfect) - len(near)} below 99%")

    out = pathlib.Path(r"C:\src\fact-pdf-tools\output\content-diff.csv")
    with out.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=["form", "words", "match_pct", "artefacts", "chars_equal", "nothing_dropped", "dropped", "chars", "chars_ours", "field_chars_excluded", "fill_excused", "examples"])
        w.writeheader()
        for r in results:
            w.writerow({**r, "examples": " | ".join(r["examples"])})
    print(f"Wrote {out}")


if __name__ == "__main__":
    args = sys.argv[1:]
    if not args and SWEEP.exists():
        args = [r["form"] for r in csv.DictReader(SWEEP.open(encoding="utf-8"))
                if r["status"] == "ok"]
    main(args)
