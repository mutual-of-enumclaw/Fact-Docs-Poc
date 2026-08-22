"""Content gate for the GhostDraft (.gd) path: does the .gd carry every FAP character?

Three bugs found via the Form Studio HTML pipeline (CP1252 decoding, enclosing-quote
stripping, reading-order banding) all lived in the SHARED FAP parser in core/, so they
silently corrupted this .gd path too -- it just had no content gate to notice. This is
that gate.

It is a self-consistency check, not a parity check: a .gd cannot be rendered locally, so
instead of comparing against a legacy render we assert that every character the FAP
declares as static text survives into the .gd's RTF body. That is exactly the class of
defect those three bugs were.

Two things this had to get right, both of which are the recurring failure mode of every
metric in this project (see FORM-STUDIO-PLAN sections 14, 19):

  * Resolve RTF escapes BEFORE stripping control words, or the escapes are eaten as
    control words and the gate reports drops that do not exist.
  * Discard the RTF HEADER groups (font table, colour table, stylesheet). Their text
    ("Times New Roman", numbers, punctuation) is not document content, and because this
    compares character multisets, surplus characters MASK real drops -- measured: with
    the header included, deleting three 'o's from a body word showed up as a shortfall of
    only one.

Usage:  python tools/gdcontent.py [FORM ...]              (default: the golden .gd suite)
        python tools/gdcontent.py --dir <path> [FORM ...]  (any directory of .gd files)
Exit code is non-zero if any form drops content, so it can gate CI.
"""
import collections
import pathlib
import re
import sys

REPO = pathlib.Path(r"C:\src\fact-pdf-tools")
GOLDEN = REPO / "demo" / "regression" / "golden"
FORMS = pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS")

# Static-text records only. F, (field) records carry no static content -- their value
# arrives from data at render time.
REC = re.compile(r"^(M,TT|T),\((\d+),(\d+),(\d+),(\d+)\),\((\d+),[^)]*\),(\d+),(.*)$")

# RTF groups whose text is metadata, not document content.
SKIP_DESTINATIONS = ("fonttbl", "colortbl", "stylesheet", "info", "generator",
                     "listtable", "listoverridetable", "revtbl", "pgptbl", "xmlnstbl")


def fap_chars(form):
    p = FORMS / f"{form}.FAP"
    if not p.exists():
        return None
    bag = collections.Counter()
    for raw in p.read_bytes().decode("cp1252", errors="replace").splitlines():
        m = REC.match(raw.strip())
        if m:
            bag.update(re.sub(r"\s+", "", m.group(8)))
    return bag


def rtf_text(s):
    """Minimal brace-aware RTF text extractor.

    Walks the stream once so control words, escapes and skipped destinations are all
    handled in the right order. Returns only document text.
    """
    out = []
    i, n = 0, len(s)
    depth = 0
    skip_until = None          # brace depth to return to when skipping a destination
    while i < n:
        c = s[i]
        if c == "{":
            depth += 1
            i += 1
            continue
        if c == "}":
            if skip_until is not None and depth <= skip_until:
                skip_until = None
            depth -= 1
            i += 1
            continue
        if c == "\\":
            # Escaped literal?
            if i + 1 < n and s[i + 1] in "{}\\":
                if skip_until is None:
                    out.append(s[i + 1])
                i += 2
                continue
            # \'hh hex escape
            if i + 1 < n and s[i + 1] == "'" and i + 3 < n:
                hexpart = s[i + 2:i + 4]
                try:
                    ch = bytes([int(hexpart, 16)]).decode("cp1252", "replace")
                except ValueError:
                    ch = ""
                if skip_until is None:
                    out.append(ch)
                i += 4
                continue
            # control word / control symbol
            m = re.match(r"\\([a-zA-Z]+)(-?\d+)?[ ]?", s[i:])
            if not m:
                i += 2          # control symbol such as \* or \~
                continue
            word, param = m.group(1), m.group(2)
            if word == "u" and param is not None:
                if skip_until is None:
                    out.append(chr(int(param) % 65536))
            elif word in SKIP_DESTINATIONS and skip_until is None:
                skip_until = depth
            elif word in ("par", "line", "tab", "cell", "row"):
                if skip_until is None:
                    out.append(" ")
            i += m.end()
            continue
        if skip_until is None:
            out.append(c)
        i += 1
    return "".join(out)


def gd_chars(form):
    p = GOLDEN / f"{form}.gd"
    if not p.exists():
        return None
    text = p.read_text(encoding="utf-8", errors="replace")
    bag = collections.Counter()
    for m in re.finditer(r"<rtf[^>]*>(.*?)</rtf>", text, re.S):
        body = re.sub(r"<!\[CDATA\[(.*?)\]\]>", r"\1", m.group(1), flags=re.S)
        for ent, ch in (("&lt;", "<"), ("&gt;", ">"), ("&quot;", '"'),
                        ("&apos;", "'"), ("&amp;", "&")):
            body = body.replace(ent, ch)
        txt = rtf_text(body)
        # Binding placeholders (%[1], %[2] ...) are markers we GENERATE, not FAP
        # content. Dropping them takes the surplus to zero on most forms, which is
        # what keeps this gate sensitive: surplus characters mask real drops.
        txt = re.sub(r"%\[\d+\]", "", txt)
        bag.update(re.sub(r"\s+", "", txt))
    return bag


def main(forms):
    print(f"{'form':<18}{'FAP chars':>10}{'.gd chars':>10}{'surplus':>9}   dropped")
    bad = empty = 0
    for form in forms:
        fap, gd = fap_chars(form), gd_chars(form)
        if fap is None or gd is None:
            print(f"{form:<18}{'-':>10}{'-':>10}{'-':>9}   (missing FAP or golden .gd)")
            continue
        if not fap:
            empty += 1
        dropped = fap - gd
        if dropped:
            bad += 1
        detail = "".join(f"{c!r}x{n} " for c, n in dropped.most_common(8)) or "none"
        # Surplus is reported because it is what can hide a drop; it should stay small.
        print(f"{form:<18}{sum(fap.values()):>10}{sum(gd.values()):>10}"
              f"{sum((gd - fap).values()):>9}   {detail}")
    print(f"\n{len(forms) - bad}/{len(forms)} forms carry every FAP character into the .gd")
    # Report vacuous passes explicitly. A form whose FAP declares no static text
    # (field-only fragments, empty stubs) passes trivially and proves nothing, so
    # folding it into the headline would overstate coverage.
    print(f"   of those, {empty} have no FAP static text at all (vacuous pass) -- "
          f"real coverage is {len(forms) - empty - bad}/{len(forms) - empty} forms with content")
    return bad


if __name__ == "__main__":
    args = sys.argv[1:]
    if args and args[0] == "--dir":
        GOLDEN = pathlib.Path(args[1])
        args = args[2:]
    args = args or sorted(p.stem for p in GOLDEN.glob("*.gd"))
    sys.exit(1 if main(args) else 0)
