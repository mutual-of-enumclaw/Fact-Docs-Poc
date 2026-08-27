#!/usr/bin/env python3
"""
htmlpacket.py -- assemble a Documaker packet into ONE HTML document.

`emit-html` converts a single FAP image.  A quote proposal is not a single image:
it is 160 of them, spliced together by rules that live in FORM.DAT and the DDTs
(see tools/ddtpacket.py).  This is the assembler those rules describe.

What it implements, all read from the legacy metadata rather than hand-written:

  * ORDER      -- FORM.DAT gives the top-level image list per form entry; each
                  image's DDT `PNTAddImgAfterCurImg` rules splice in its children.
  * STACKING   -- `;SetOrigin;Rel+0,Max+0;` means "flow underneath what is already
                  placed".  `;SetOrigin;Abs+0,Abs+25200;` pins an image (the footer)
                  to an absolute row.
  * HEIGHT     -- `;SetImageDimensions;98,0,<h>,<w>,<yOff>,<xOff>;`, the same tuple
                  the FAP's own `H,` line carries.
  * PAGINATION -- the FORM.DAT flags: `OX` marks the image that repeats as the page
                  HEADER, `OY` the one pinned as the FOOTER.  A body image that will
                  not fit above the footer starts a new page.

Repetition counts and field values are DATA -- how many coverages, how many vehicles,
what each one costs.  `demo -- quote-data <POLICY>` emits them from a real CDM quote
and `--data` consumes that here; an image the document does not mention falls back to
`--repeat IMAGE=N`, default one, so unmapped boilerplate still renders.

Usage
    # build the demo once so fragments emit fast
    dotnet build demo/FapPdfTools.Demo.csproj -c Release

    # shape only, no data
    python tools/htmlpacket.py --lob CPP --form "QUOTE CPPSUM.1" \
        --out output/quote-poc/cppsum.html --pdf

    # populated from a real quote (scope=Pending)
    demo/bin/Release/net9.0/FapPdfTools.Demo.exe quote-data BAP000080307 tst Pending \
        output/quote-poc/BAP000080307.json
    python tools/htmlpacket.py --lob CPP \
        --form "QUOTE CPPSUM.2" --form "QUOTE CPPCA.1" \
        --data output/quote-poc/BAP000080307.json \
        --out output/quote-poc/wy-quote.html --pdf
"""

from __future__ import annotations

import argparse
import glob
import json
import os
import re
import subprocess
import sys
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional, Sequence, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ddtpacket as dp  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

FAP_PER_PT = 2400.0 / 72.0
PAGE_W_FAP = 20400 + 1800 + 1800   # 612pt at 2400dpi -- the H, width is the text box
PAGE_H_FAP = 26400                 # 792pt
PAGE_W_PT = 612.0
PAGE_H_PT = 792.0

CHROME = r"C:\Program Files\Google\Chrome\Application\chrome.exe"

SECTION_RE = re.compile(
    r'<section class="form-page"[^>]*>(.*?)</section>', re.S)
FONTFACE_RE = re.compile(r"@font-face\{.*?\}", re.S)
FAMILY_RE = re.compile(r"font-family:'([^']+)'")


# --------------------------------------------------------------- fragment emission


def find_demo_exe() -> Optional[str]:
    pats = [
        os.path.join(REPO, "demo", "bin", "Release", "net*", "FapPdfTools.Demo.exe"),
        os.path.join(REPO, "demo", "bin", "Debug", "net*", "FapPdfTools.Demo.exe"),
    ]
    for pat in pats:
        hits = sorted(glob.glob(pat))
        if hits:
            return hits[-1]
    return None


class FragmentCache:
    """emit-html output per image, cached on disk and keyed by FAP mtime."""

    def __init__(self, cachedir: str, exe: Optional[str]) -> None:
        self.dir = cachedir
        self.exe = exe
        os.makedirs(cachedir, exist_ok=True)
        self._body: Dict[str, str] = {}
        self._faces: Dict[str, Dict[str, str]] = {}
        self.emitted = 0
        self.reused = 0
        self.failed: List[str] = []

    def _emit(self, image: str) -> Optional[str]:
        out = os.path.join(self.dir, image + ".html")
        fap = os.path.join(dp.DEFAULT_FAPDIR, image + ".FAP")
        if (os.path.exists(out) and os.path.exists(fap)
                and os.path.getmtime(out) >= os.path.getmtime(fap)):
            self.reused += 1
            return out
        if self.exe:
            cmd = [self.exe, "emit-html", image, out]
        else:
            cmd = ["dotnet", "run", "--project",
                   os.path.join(REPO, "demo", "FapPdfTools.Demo.csproj"),
                   "--", "emit-html", image, out]
        res = subprocess.run(cmd, cwd=REPO, capture_output=True, text=True)
        if res.returncode != 0 or not os.path.exists(out):
            self.failed.append(image)
            return None
        self.emitted += 1
        return out

    def get(self, image: str) -> Tuple[str, Dict[str, str]]:
        """Return (body html, {family: @font-face rule}) for one image.

        Both halves are cached and BOTH are returned every call: the measuring
        pass calls this before the rendering pass does, and an earlier version
        handed the faces out only once, which silently produced a font-less
        document."""
        if image in self._body:
            return self._body[image], self._faces[image]
        path = self._emit(image)
        if path is None:
            self._body[image] = ""
            self._faces[image] = {}
            return "", {}
        html = open(path, encoding="utf-8").read()
        m = SECTION_RE.search(html)
        body = m.group(1).strip() if m else ""
        faces: Dict[str, str] = {}
        for face in FONTFACE_RE.findall(html):
            fam = FAMILY_RE.search(face)
            if fam:
                faces.setdefault(fam.group(1), face)
        self._body[image] = body
        self._faces[image] = faces
        return body, faces


# ------------------------------------------------------------------- graph expansion


@dataclass
class Placement:
    image: str
    depth: int
    role: str          # "header" | "footer" | "body"
    driver: str        # the extract table + filter that drives repetition, for the log
    instance: int      # 1-based, within its parent
    count: int
    fields: Dict[str, str]


class PacketData:
    """The data half of the packet, as emitted by `demo -- quote-data`.

    An image PRESENT in the document has an authoritative instance count -- zero
    included, which is how the conditional variants get pruned. An image ABSENT
    from it falls back to the `--repeat` override, default one, so unmapped
    boilerplate still renders.
    """

    def __init__(self, doc: Optional[dict]) -> None:
        self.images: Dict[str, dict] = {}
        self.policy = ""
        if not doc:
            return
        self.policy = doc.get("PolicyNumber") or doc.get("policyNumber") or ""
        for name, entry in (doc.get("Images") or doc.get("images") or {}).items():
            self.images[name.upper()] = {
                "parent": entry.get("Parent") or entry.get("parent"),
                "instances": entry.get("Instances") or entry.get("instances") or [],
            }

    def knows(self, image: str) -> bool:
        return image.upper() in self.images

    def instances(self, image: str, parent_index: int) -> List[Dict[str, str]]:
        """Field maps for each instance of `image` under parent instance
        `parent_index` (-1 at the root)."""
        entry = self.images.get(image.upper())
        if entry is None:
            return []
        rows = entry["instances"]
        if entry["parent"]:
            rows = [r for r in rows
                    if int(r.get("ParentIndex", r.get("parentIndex", -1)))
                    == parent_index]
        return [dict(r.get("Fields") or r.get("fields") or {}) for r in rows]


def expand(image: str, repeats: Dict[str, int], data: PacketData,
           role: str = "body", depth: int = 0, driver: str = "",
           parent_index: int = -1,
           seen: Optional[frozenset] = None) -> List[Placement]:
    """Depth-first expansion of an image and its PNTAddImgAfterCurImg children.

    The DDT says WHICH image repeats and over which extract table; the data
    document says HOW MANY TIMES and with what values."""
    seen = frozenset() if seen is None else seen
    if image in seen:
        return []
    rules = dp.read_ddt(dp.DEFAULT_DDTDIR, image)

    if data.knows(image):
        rows = data.instances(image, parent_index)
    else:
        rows = [{} for _ in range(max(0, int(repeats.get(image.upper(), 1))))]

    out: List[Placement] = []
    for i, fields in enumerate(rows):
        out.append(Placement(image, depth, role, driver, i + 1, len(rows), fields))
        for child in rules.children:
            out.extend(expand(child.image, repeats, data, role, depth + 1,
                              str(child.driver) if child.driver else "",
                              i, seen | {image}))
    return out


# ------------------------------------------------------------------------- assembly


@dataclass
class Placed:
    image: str
    page: int
    top_fap: int
    height_fap: int
    role: str
    instance: int
    driver: str
    fields: Dict[str, str]


class Assembler:
    def __init__(self, cache: FragmentCache, flow: str = "content") -> None:
        self.cache = cache
        self.flow = flow
        self._extent: Dict[str, Optional[int]] = {}
        self.pages: List[List[Placed]] = []
        self.fonts: Dict[str, str] = {}
        self._geom: Dict[str, dp.ImageRules] = {}
        self.filled = 0
        self.shrunk = 0
        self.no_span: List[str] = []
        self.unverified: List[str] = []

    def geom(self, image: str) -> dp.ImageRules:
        if image not in self._geom:
            self._geom[image] = dp.read_ddt(dp.DEFAULT_DDTDIR, image)
        return self._geom[image]

    # `;SetOrigin;Rel+0,Max+0;` places an image at the running MAXIMUM Y, and the
    # question is what that maximum is measured against: the image's DECLARED box
    # (SetImageDimensions) or the ink it actually puts down.
    #
    # It is the ink. The declared boxes are design-time allocations and they are
    # consistently larger -- QTE_BILLINFO_A declares 10,500 units and draws 6,369.
    # Measured against the Products reference document, page 3: with declared
    # heights our WY quote's summary page runs to 25,500 units against a footer
    # pinned at 25,200, so the payment-plan table spills to a second page. The
    # reference fits MORE rows than we have (16 endorsements against 14) on the one
    # page, which the declared-height model cannot do at any row count. By ink the
    # same page measures 19,644 and fits, as it should.
    _STYLE = re.compile(r'style="([^"]*)"')
    _TOP = re.compile(r"top:(-?[\d.]+)pt")
    _HEIGHT = re.compile(r"height:([\d.]+)pt")
    _FONTSIZE = re.compile(r"font-size:([\d.]+)pt")

    def advance(self, image: str) -> int:
        """How far this image moves the flow, in FAP units."""
        declared = self.geom(image).height
        if self.flow != "content":
            return declared
        if image not in self._extent:
            self._extent[image] = self._measure(image)
        ink = self._extent[image]
        return declared if ink is None else ink

    def _measure(self, image: str) -> Optional[int]:
        body, _ = self.cache.get(image)
        if not body:
            return None
        bottom = None
        for m in self._STYLE.finditer(body):
            style = m.group(1)
            top = self._TOP.search(style)
            if not top:
                continue
            h = self._HEIGHT.search(style)
            size = self._FONTSIZE.search(style)
            extent = float(h.group(1)) if h else (
                float(size.group(1)) if size else 0.0)
            edge = float(top.group(1)) + extent
            bottom = edge if bottom is None else max(bottom, edge)
        return None if bottom is None else int(round(bottom * FAP_PER_PT))

    def assemble_form(self, headers: List[Placement], footers: List[Placement],
                      body: List[Placement]) -> None:
        """Lay one FORM.DAT entry out over as many pages as its body needs."""
        footer_top = PAGE_H_FAP
        for f in footers:
            g = self.geom(f.image)
            if g.origin_y_mode.lower() == "abs":
                footer_top = min(footer_top, g.origin_y_val)
            else:
                footer_top = min(footer_top, PAGE_H_FAP - self.advance(f.image))

        page: List[Placed] = []
        y = 0

        def open_page() -> None:
            nonlocal page, y
            page = []
            y = 0
            for h in headers:
                g = self.geom(h.image)
                top = y + g.y_offset
                page.append(Placed(h.image, len(self.pages), top,
                                   self.advance(h.image), "header", 1, "",
                                   h.fields))
                y = top + self.advance(h.image)
            for f in footers:
                g = self.geom(f.image)
                top = g.origin_y_val if g.origin_y_mode.lower() == "abs" \
                    else PAGE_H_FAP - g.height
                page.append(Placed(f.image, len(self.pages), top,
                                   self.advance(f.image), "footer", 1, "",
                                   f.fields))

        def close_page() -> None:
            if page:
                self.pages.append(page)

        open_page()
        for p in body:
            g = self.geom(p.image)
            if g.origin_y_mode.lower() == "abs":
                # Pinned: it does not participate in the flow.
                page.append(Placed(p.image, len(self.pages), g.origin_y_val,
                                   self.advance(p.image), p.role, p.instance,
                                   p.driver, p.fields))
                continue
            h = self.advance(p.image)
            if y + h > footer_top and y > 0:
                close_page()
                open_page()
            top = y + g.y_offset
            page.append(Placed(p.image, len(self.pages), top, h,
                               p.role, p.instance, p.driver, p.fields))
            y = top + h
        close_page()

    # ------------------------------------------------------------- populating

    # Same contract as the C# `fill-html`: splice the value between an EMPTY field
    # span's tags, then verify by re-reading the result rather than by counting
    # replacements. Deliberately index-and-splice, not a regex replacement
    # template -- `.NET Regex.Replace` with "$1" ate a span when the value started
    # with a digit and still reported success (HANDOFF-QUOTE-POC.md section 7), and
    # Python's `\1` has the same hazard.
    FIELD_SPAN = (r'(<span class="abs field" data-field="%s"[^>]*>)</span>')

    def fill(self, frag: str, pl: Placed) -> str:
        for name, value in pl.fields.items():
            if value == "":
                continue
            rx = re.compile(self.FIELD_SPAN % re.escape(name), re.I)
            m = rx.search(frag)
            if not m:
                self.no_span.append(f"{pl.image}.{name}")
                continue
            opening = self._autosize(m.group(1), value)
            esc = (value.replace("&", "&amp;").replace("<", "&lt;")
                        .replace(">", "&gt;"))
            frag = frag[:m.start()] + opening + esc + "</span>" + frag[m.end():]
            if f">{esc}</span>" not in frag:
                self.unverified.append(f"{pl.image}.{name}")
            else:
                self.filled += 1
        return frag

    # A value wider than its box shrinks to fit, which is what the accepted
    # fillable-PDF path does: the AcroForm widget auto-sizes its text into the
    # rectangle rather than spilling over the next label.
    _WIDTH = re.compile(r"width:([0-9.]+)pt")
    _SIZE = re.compile(r"font-size:([0-9.]+)pt")
    ADVANCE_EM = 0.5

    def _autosize(self, opening: str, value: str) -> str:
        w = self._WIDTH.search(opening)
        s = self._SIZE.search(opening)
        if not (w and s):
            return opening
        box, cur = float(w.group(1)), float(s.group(1))
        if box <= 1 or not value:
            return opening
        need = len(value) * cur * self.ADVANCE_EM
        if need <= box:
            return opening
        fitted = max(5.0, box / (len(value) * self.ADVANCE_EM))
        self.shrunk += 1
        return opening.replace(f"font-size:{s.group(1)}pt",
                               f"font-size:{fitted:.2f}pt")

    # ------------------------------------------------------------------ rendering

    def html(self, title: str) -> str:
        body_parts: List[str] = []
        for pageno, placements in enumerate(self.pages, start=1):
            body_parts.append(
                f'<section class="form-page" data-page="{pageno}">')
            for pl in placements:
                frag, faces = self.cache.get(pl.image)
                for fam, rule in faces.items():
                    self.fonts.setdefault(fam, rule)
                if pl.fields:
                    frag = self.fill(frag, pl)
                top_pt = pl.top_fap / FAP_PER_PT
                body_parts.append(
                    f'<div class="frag" data-image="{pl.image}" '
                    f'data-role="{pl.role}" data-instance="{pl.instance}" '
                    f'data-driver="{_esc(pl.driver)}" '
                    f'style="top:{top_pt:.3f}pt">')
                body_parts.append(frag)
                body_parts.append("</div>")
            body_parts.append("</section>")

        css = "\n".join(self.fonts[k] for k in sorted(self.fonts))
        return (
            "<!doctype html>\n<html><head><meta charset=\"utf-8\">"
            f"<title>{_esc(title)}</title>\n<style>\n{css}\n"
            f"@page{{size:{PAGE_W_PT:g}pt {PAGE_H_PT:g}pt;margin:0}}\n"
            "html,body{margin:0;padding:0;background:#fff;"
            "-webkit-print-color-adjust:exact}\n"
            f".form-page{{position:relative;width:{PAGE_W_PT:g}pt;"
            f"height:{PAGE_H_PT:g}pt;overflow:hidden;page-break-after:always}}\n"
            f".frag{{position:absolute;left:0;width:{PAGE_W_PT:g}pt}}\n"
            ".abs{position:absolute;white-space:pre;margin:0;padding:0}\n"
            ".rule{position:absolute;background:#000}\n"
            ".box{position:absolute;border:solid #000;box-sizing:border-box}\n"
            ".shade{position:absolute}\n"
            ".bullet{position:absolute;background:#000;border-radius:50%}\n"
            ".img{position:absolute}\n"
            "</style></head><body>\n"
            + "\n".join(body_parts)
            + "\n</body></html>\n")


def _esc(s: str) -> str:
    return (s.replace("&", "&amp;").replace("<", "&lt;")
             .replace(">", "&gt;").replace('"', "&quot;"))


# ------------------------------------------------------------------------------ CLI


def to_pdf(html_path: str, pdf_path: str) -> bool:
    if not os.path.exists(CHROME):
        print(f"  (chrome not found at {CHROME}; skipping PDF)")
        return False
    url = "file:///" + os.path.abspath(html_path).replace("\\", "/")
    res = subprocess.run(
        [CHROME, "--headless", "--disable-gpu", "--no-pdf-header-footer",
         f"--print-to-pdf={os.path.abspath(pdf_path)}", url],
        capture_output=True, text=True)
    ok = os.path.exists(pdf_path)
    if not ok:
        print("  chrome failed:", (res.stderr or "").strip()[:400])
    return ok


def main(argv: Sequence[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--lob", default="CPP")
    ap.add_argument("--form", action="append", default=[])
    ap.add_argument("--packet")
    ap.add_argument("--formdat", default=dp.DEFAULT_FORMDAT)
    ap.add_argument("--out", default=os.path.join(REPO, "output", "quote-poc",
                                                  "packet.html"))
    ap.add_argument("--cache", default=os.path.join(REPO, "output", "quote-poc",
                                                    "frag"))
    ap.add_argument("--repeat", action="append", default=[],
                    metavar="IMAGE=N")
    ap.add_argument("--repeats", help="JSON file of {image: count}")
    ap.add_argument("--data",
                    help="packet data document from `demo -- quote-data` -- "
                         "instance counts and field values from a real quote")
    ap.add_argument("--flow", choices=["content", "declared"], default="content",
                    help="what `SetOrigin Max+0` advances by: the ink an image "
                         "actually puts down (default, and what matches the "
                         "reference document) or its declared box height")
    ap.add_argument("--pdf", action="store_true")
    args = ap.parse_args(argv)

    repeats: Dict[str, int] = {}
    if args.repeats:
        repeats.update({k.upper(): int(v) for k, v in
                        json.load(open(args.repeats, encoding="utf-8")).items()})
    for spec in args.repeat:
        k, _, v = spec.partition("=")
        repeats[k.strip().upper()] = int(v or 1)

    wanted = list(args.form)
    if args.packet:
        key = args.lob.upper()
        if args.packet.lower() == "quote" and key in dp.QUOTE_PACKETS:
            wanted = dp.QUOTE_PACKETS[key] + wanted
        else:
            print(f"unknown packet '{args.packet}' for {key}", file=sys.stderr)
            return 2
    if not wanted:
        ap.error("give --form or --packet")

    entries = {e.name.upper(): e for e in dp.read_formdat(args.formdat)
               if e.lob.upper() == args.lob.upper()}

    data = PacketData(json.load(open(args.data, encoding="utf-8"))
                      if args.data else None)
    if args.data:
        print(f"packet data: {data.policy}  {len(data.images)} image(s) mapped")

    exe = find_demo_exe()
    print(f"fragment emitter: {exe or 'dotnet run (slow -- build Release first)'}")
    cache = FragmentCache(args.cache, exe)
    asm = Assembler(cache, flow=args.flow)

    def one(image: str, role: str) -> List[Placement]:
        rows = data.instances(image, -1) if data.knows(image) else [{}]
        return [Placement(image, 0, role, "", 1, 1, rows[0] if rows else {})]

    for name in wanted:
        entry = entries.get(name.upper())
        if entry is None:
            print(f"! '{name}' is not a FORM.DAT entry for {args.lob}")
            continue
        headers: List[Placement] = []
        footers: List[Placement] = []
        body: List[Placement] = []
        for img, flags in entry.placements:
            if "OX" in flags:
                headers.extend(one(img, "header"))
            elif "OY" in flags:
                footers.extend(one(img, "footer"))
            else:
                body.extend(expand(img, repeats, data))
        first_page = len(asm.pages)
        asm.assemble_form(headers, footers, body)
        print(f"{name:<22} {len(body):>4} placement(s) -> "
              f"page {first_page + 1}..{len(asm.pages)}")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    html = asm.html(os.path.basename(args.out))
    open(args.out, "w", encoding="utf-8").write(html)
    total = sum(len(p) for p in asm.pages)
    print(f"\n{len(asm.pages)} page(s), {total} image placement(s), "
          f"{len(asm.fonts)} face(s)")
    print(f"fragments: {cache.emitted} emitted, {cache.reused} reused"
          + (f", {len(cache.failed)} FAILED: {', '.join(cache.failed[:10])}"
             if cache.failed else ""))
    if args.data:
        print(f"filled   {asm.filled} value(s), verified in the output"
              + (f", {asm.shrunk} auto-sized into their box" if asm.shrunk else ""))
        if asm.no_span:
            print(f"NO SPAN  {len(asm.no_span)}: "
                  + ", ".join(sorted(set(asm.no_span))[:12]))
        if asm.unverified:
            print(f"NOT VERIFIED {len(asm.unverified)}: "
                  + ", ".join(sorted(set(asm.unverified))[:12]))
    print(f"wrote {args.out} ({os.path.getsize(args.out):,} bytes)")

    if args.pdf:
        pdf = os.path.splitext(args.out)[0] + ".pdf"
        if to_pdf(args.out, pdf):
            print(f"wrote {pdf} ({os.path.getsize(pdf):,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
