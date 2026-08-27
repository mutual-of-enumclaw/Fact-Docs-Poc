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

Repetition counts are data (how many coverages, how many vehicles).  Until the CDM
list sources are wired up, pass them with `--repeat IMAGE=N` or `--repeats file.json`;
the default is one instance per image, which is enough to see the packet's shape.

Usage
    # build the demo once so fragments emit fast
    dotnet build demo/FapPdfTools.Demo.csproj -c Release

    python tools/htmlpacket.py --lob CPP --form "QUOTE CPPSUM.1" \
        --out output/quote-poc/cppsum.html --pdf

    python tools/htmlpacket.py --lob CPP --packet quote \
        --repeats output/quote-poc/repeats.json \
        --out output/quote-poc/cpp-quote.html --pdf
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
        self._fonts: Dict[str, str] = {}
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
        """Return (body html, {family: @font-face rule}) for one image."""
        if image in self._body:
            return self._body[image], {}
        path = self._emit(image)
        if path is None:
            self._body[image] = ""
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


def expand(image: str, repeats: Dict[str, int], role: str = "body",
           depth: int = 0, driver: str = "",
           seen: Optional[frozenset] = None) -> List[Placement]:
    """Depth-first expansion of an image and its PNTAddImgAfterCurImg children,
    each repeated `repeats[image]` times (default 1)."""
    seen = frozenset() if seen is None else seen
    if image in seen:
        return []
    rules = dp.read_ddt(dp.DEFAULT_DDTDIR, image)
    n = max(0, int(repeats.get(image, 1)))
    out: List[Placement] = []
    for i in range(1, n + 1):
        out.append(Placement(image, depth, role, driver, i, n))
        for child in rules.children:
            out.extend(expand(child.image, repeats, role, depth + 1,
                              str(child.driver) if child.driver else "",
                              seen | {image}))
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


class Assembler:
    def __init__(self, cache: FragmentCache) -> None:
        self.cache = cache
        self.pages: List[List[Placed]] = []
        self.fonts: Dict[str, str] = {}
        self._geom: Dict[str, dp.ImageRules] = {}

    def geom(self, image: str) -> dp.ImageRules:
        if image not in self._geom:
            self._geom[image] = dp.read_ddt(dp.DEFAULT_DDTDIR, image)
        return self._geom[image]

    def assemble_form(self, headers: List[str], footers: List[str],
                      body: List[Placement], start_new_page: bool = True) -> None:
        """Lay one FORM.DAT entry out over as many pages as its body needs."""
        footer_top = PAGE_H_FAP
        for f in footers:
            g = self.geom(f)
            if g.origin_y_mode.lower() == "abs":
                footer_top = min(footer_top, g.origin_y_val)
            else:
                footer_top = min(footer_top, PAGE_H_FAP - g.height)

        page: List[Placed] = []
        y = 0

        def open_page() -> None:
            nonlocal page, y
            page = []
            y = 0
            for h in headers:
                g = self.geom(h)
                top = y + g.y_offset
                page.append(Placed(h, len(self.pages), top, g.height,
                                   "header", 1, ""))
                y = top + g.height
            for f in footers:
                g = self.geom(f)
                top = g.origin_y_val if g.origin_y_mode.lower() == "abs" \
                    else PAGE_H_FAP - g.height
                page.append(Placed(f, len(self.pages), top, g.height,
                                   "footer", 1, ""))

        def close_page() -> None:
            if page:
                self.pages.append(page)

        open_page()
        for p in body:
            g = self.geom(p.image)
            if g.origin_y_mode.lower() == "abs":
                # Pinned: it does not participate in the flow.
                page.append(Placed(p.image, len(self.pages), g.origin_y_val,
                                   g.height, p.role, p.instance, p.driver))
                continue
            h = g.height
            if y + h > footer_top and y > 0:
                close_page()
                open_page()
            top = y + g.y_offset
            page.append(Placed(p.image, len(self.pages), top, h,
                               p.role, p.instance, p.driver))
            y = top + h
        close_page()

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

    exe = find_demo_exe()
    print(f"fragment emitter: {exe or 'dotnet run (slow -- build Release first)'}")
    cache = FragmentCache(args.cache, exe)
    asm = Assembler(cache)

    for name in wanted:
        entry = entries.get(name.upper())
        if entry is None:
            print(f"! '{name}' is not a FORM.DAT entry for {args.lob}")
            continue
        headers = [i for i, f in entry.placements if "OX" in f]
        footers = [i for i, f in entry.placements if "OY" in f]
        body_images = [i for i, f in entry.placements
                       if "OX" not in f and "OY" not in f]
        body: List[Placement] = []
        for img in body_images:
            body.extend(expand(img, repeats))
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
    print(f"wrote {args.out} ({os.path.getsize(args.out):,} bytes)")

    if args.pdf:
        pdf = os.path.splitext(args.out)[0] + ".pdf"
        if to_pdf(args.out, pdf):
            print(f"wrote {pdf} ({os.path.getsize(pdf):,} bytes)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
