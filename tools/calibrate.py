"""Measure the per-FontId baseline correction between Documaker and Chromium.

Chromium places a baseline inside a line box using font metrics we cannot reliably
predict from the TTF tables, and the FXR ascent differs per font id -- so instead of
deriving the offset we measure it, once, and store it.

Method: emit HTML with NO correction applied, render it, then for every text run that
appears in both our PDF and the legacy FAP2PDF render, take (legacyBaseline -
ourBaseline). Group by the data-fid on the emitting span and take the median.

Deterministic: fixed form list, sorted, median (not mean) so a few mismatches cannot
drag a font id. Writes output/font-calibration.json, which emit-html then applies.

Usage:  python tools/calibrate.py [n_forms]
"""
import collections
import json
import pathlib
import re
import statistics
import subprocess
import sys

import fitz

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import sweep  # noqa: E402  (paths + runner helpers)

SPAN = re.compile(
    r'<span class="abs" data-fid="(\d+)" style="left:([\d.]+)pt;top:([-\d.]+)pt;'
    r"font-family:'([^']+)';font-size:([\d.]+)pt")


def our_spans(html_path):
    """(fid, left, top, size) for every emitted text run, in document order."""
    txt = html_path.read_text(encoding="utf-8")
    out = []
    for m in SPAN.finditer(txt):
        # the run's text follows the style attribute
        end = txt.index(">", m.end()) + 1
        text = txt[end:txt.index("</span>", end)]
        out.append((int(m.group(1)), float(m.group(2)), float(m.group(3)),
                    float(m.group(5)), text.strip()))
    return out


def pdf_baselines(path):
    """text -> list of (x, baselineY), page 1 only."""
    d = fitz.open(str(path))
    out = collections.defaultdict(list)
    for b in d[0].get_text("dict")["blocks"]:
        for line in b.get("lines", []):
            for s in line["spans"]:
                t = s["text"].strip()
                if t:
                    out[t].append((s["origin"][0], s["origin"][1]))
    return out


def main(n_forms):
    work = sweep.REPO / "output" / "calib-work"
    work.mkdir(parents=True, exist_ok=True)
    (work / "FSISYS.INI").write_bytes((sweep.MSTRRES / "MOEC0" / "FSISYS.INI").read_bytes())

    cal_file = sweep.REPO / "output" / "font-calibration.json"
    if cal_file.exists():
        cal_file.unlink()  # calibrate against the RAW model, never a corrected one

    inv = sweep.load_inventory()
    forms = [f for f, _ in sweep.stratify(inv, max(2, n_forms // 4))][:n_forms]
    print(f"Calibrating over {len(forms)} forms (raw model, no correction applied)\n")

    deltas = collections.defaultdict(list)
    for i, name in enumerate(forms, 1):
        try:
            fap = sweep.FORMS / f"{name}.FAP"
            if not fap.exists():
                continue
            (work / f"{name}.FAP").write_bytes(fap.read_bytes())
            sweep.run([str(sweep.FAP2PDF), f"-I={name}.FAP", f"-X={sweep.FXR}"], work)
            ref = work / f"{name}.PDF"
            if not ref.exists() or ref.stat().st_size == 0:
                continue

            html = work / f"{name}.html"
            sweep.run([str(sweep.DEMO), "emit-html", name, str(html)], sweep.REPO)
            if not html.exists():
                continue
            our_pdf = work / f"{name}_ours.pdf"
            sweep.run([str(sweep.CHROME), "--headless", "--disable-gpu",
                       "--no-pdf-header-footer", f"--print-to-pdf={our_pdf}",
                       html.as_uri()], work, timeout=180)
            if not our_pdf.exists():
                continue

            leg, ours = pdf_baselines(ref), pdf_baselines(our_pdf)
            emitted = {t: (fid, left) for fid, left, _, _, t in our_spans(html) if t}
            hits = 0
            for text, (fid, left) in emitted.items():
                if text not in leg or text not in ours:
                    continue
                # unambiguous matches only: one occurrence each side, same column
                if len(leg[text]) != 1 or len(ours[text]) != 1:
                    continue
                lx, ly = leg[text][0]
                ox, oy = ours[text][0]
                if abs(lx - ox) > 0.5:      # different column -> not the same run
                    continue
                deltas[fid].append(ly - oy)
                hits += 1
            print(f"[{i}/{len(forms)}] {name:<17} matched {hits:>4} runs", flush=True)
        except Exception as exc:  # noqa: BLE001
            print(f"[{i}/{len(forms)}] {name:<17} error: {type(exc).__name__}: {exc}")

    # Robust estimate. A raw median over noisy matches produced corrections that
    # OVER-shot a whole group of forms (measured: dy=-4px), which is worse than not
    # correcting at all. So: cluster around the median, keep only the tight core, and
    # emit a correction ONLY when that core is both large and dominant. Anything that
    # fails these gates is left uncorrected rather than corrected wrongly.
    # MIN_SHARE is deliberately strict. Font id 16010 (the most common body font)
    # measured a 60% core -- i.e. bimodal, so a single scalar cannot describe it --
    # and applying its median regressed the `fields` stratum from 0.212 to 0.124.
    # A bimodal font id is left uncorrected until the second mode is understood.
    MIN_SAMPLES, CORE_TOL, MIN_SHARE = 8, 0.75, 0.90
    table = {}
    print(f"\n{'fontId':>7}{'n':>6}{'core':>6}{'share':>7}{'corr':>9}{'stdev':>8}  verdict")
    for fid in sorted(deltas):
        vals = deltas[fid]
        med = statistics.median(vals)
        core = [v for v in vals if abs(v - med) <= CORE_TOL]
        share = len(core) / len(vals)
        sd = round(statistics.pstdev(core), 3) if len(core) > 1 else 0.0
        if len(core) >= MIN_SAMPLES and share >= MIN_SHARE:
            corr = round(statistics.median(core), 3)
            table[str(fid)] = corr
            verdict = "applied"
        else:
            corr, verdict = 0.0, f"SKIPPED (n={len(core)}, share={share:.0%})"
        print(f"{fid:>7}{len(vals):>6}{len(core):>6}{share:>7.0%}{corr:>9.3f}{sd:>8.3f}  {verdict}")

    cal_file.write_text(json.dumps(table, indent=1, sort_keys=True), encoding="utf-8")
    print(f"\nWrote {cal_file} ({len(table)} font ids)")


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 20)
