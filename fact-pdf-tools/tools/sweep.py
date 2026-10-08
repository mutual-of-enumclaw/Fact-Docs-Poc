"""Fidelity sweep: batch legacy-vs-ours parity scoring across a stratified form sample.

For each form:  FAP2PDF (legacy reference) -> emit-html -> Chromium PDF -> parity score.
Writes a per-form CSV plus a markdown summary — the P0 "fidelity dashboard skeleton".

Usage:
    python tools/sweep.py [n_per_stratum] [outdir]

Deterministic: strata are sorted and sampled at fixed intervals, so the same
coverage report always yields the same sample.
"""
import csv
import pathlib
import subprocess
import sys
import traceback

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import parity  # noqa: E402  (raster / pad_to / dilate / best_shift)

REPO = pathlib.Path(r"C:\src\fact-pdf-tools")
MSTRRES = pathlib.Path(r"C:\src\FaCT-DocProd-Development\mstrres")
FORMS = MSTRRES / "MOEC0" / "FORMS"
FXR = MSTRRES / "MOEC0" / "DEFLIB" / "REL103.FXR"
FAP2PDF = pathlib.Path(r"C:\src\FaCT-DocProd-Development\Dll\FAP2PDF.EXE")
CHROME = pathlib.Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")
DEMO = REPO / "demo" / "bin" / "Debug" / "net9.0" / "FapPdfTools.Demo.exe"
COVERAGE = REPO / "output" / "coverage-report.csv"

# Every form a human has actually looked at and given a verdict on. The stratified sample is
# drawn from construct counts and has no reason to contain any of them -- EB2410A and EB22489Q
# were NOT in the 1,000-form sample, so a full sweep left their `_ours.pdf` rendered by the
# PREVIOUS build while every gate reported on them from that stale artefact. The project's
# first rule is to validate against this set, which cannot be done from a stale render, so the
# set is pinned into every sweep regardless of what the strata pick.
ACCEPTED = [
    # accepted by Products, August 2026
    "EB2410A", "A0238C", "EB22489Q", "P0010G",
    # reviewed 2026-08-23: "look identical" despite Tier 2 rating them 42-45%
    "BAN01", "BANSPECH",
    # reviewed 2026-08-23 with a defect named, so they must stay measurable
    "IM74561R", "M7902AA",
]


def load_inventory():
    """form -> construct counts, from the existing coverage report."""
    rows = {}
    with COVERAGE.open(newline="", encoding="utf-8", errors="replace") as fh:
        for r in csv.DictReader(fh):
            try:
                rows[r["form"]] = {
                    k: int(r[k] or 0)
                    for k in ("pages", "fields", "gridRects", "images", "textAreas")
                }
            except ValueError:
                continue
    return rows


def stratify(inv, n):
    """Deterministic stratified sample across the construct categories that matter."""
    def pick(pred, label):
        names = sorted(f for f, c in inv.items() if pred(c))
        if not names:
            return []
        step = max(1, len(names) // n)
        return [(x, label) for x in names[::step][:n]]

    strata = [
        (lambda c: c["pages"] == 1 and c["images"] == 0 and c["gridRects"] == 0
         and c["textAreas"] > 0 and c["fields"] == 0, "prose"),
        (lambda c: c["pages"] == 1 and c["images"] == 0 and c["gridRects"] == 0
         and c["fields"] > 0, "fields"),
        (lambda c: c["pages"] == 1 and c["images"] == 0 and c["gridRects"] > 0, "grid"),
        (lambda c: c["pages"] > 1 and c["images"] == 0, "multipage"),
        (lambda c: c["images"] > 0, "images"),
    ]
    out, seen = [], set()
    for pred, label in strata:
        for name, lab in pick(pred, label):
            if name not in seen:
                seen.add(name)
                out.append((name, lab))
    return out


def run(cmd, cwd, timeout=120):
    return subprocess.run(cmd, cwd=str(cwd), capture_output=True, text=True,
                          timeout=timeout, errors="replace")


def score(ref_pdf, our_pdf):
    """Per-page parity, aggregated. Mirrors tools/parity.py's metrics."""
    import fitz
    nref = fitz.open(str(ref_pdf)).page_count
    nour = fitz.open(str(our_pdf)).page_count
    ious, legacy_un, ours_un, shifts = [], [], [], []
    for p in range(max(nref, nour)):
        ra, _ = parity.raster(str(ref_pdf), p)
        oa, _ = parity.raster(str(our_pdf), p)
        if ra is None or oa is None:
            ious.append(0.0)
            continue
        h, w = max(ra.shape[0], oa.shape[0]), max(ra.shape[1], oa.shape[1])
        ra, oa = parity.pad_to(ra, h, w), parity.pad_to(oa, h, w)
        rb, ob = ra <= parity.INK, oa <= parity.INK
        union = np.logical_or(rb, ob).sum()
        ious.append(float(np.logical_and(rb, ob).sum() / union) if union else 1.0)
        rd, od = parity.dilate(rb), parity.dilate(ob)
        ours_un.append(100 * np.logical_and(ob, ~rd).sum() / max(1, ob.sum()))
        legacy_un.append(100 * np.logical_and(rb, ~od).sum() / max(1, rb.sum()))
        shifts.append(parity.best_shift(rb, ob))
    return {
        "legacy_pages": nref, "our_pages": nour,
        "mean_iou": round(float(np.mean(ious)), 3) if ious else 0.0,
        "legacy_unmatched_pct": round(float(np.mean(legacy_un)), 2) if legacy_un else None,
        "ours_unmatched_pct": round(float(np.mean(ours_un)), 2) if ours_un else None,
        "max_shift": max((abs(dy) + abs(dx) for dy, dx in shifts), default=0),
    }


def sweep(sample, work):
    work.mkdir(parents=True, exist_ok=True)
    # FAP2PDF resolves its PDF settings from fsisys.ini in the working directory.
    ini = MSTRRES / "MOEC0" / "FSISYS.INI"
    (work / "FSISYS.INI").write_bytes(ini.read_bytes())
    # FAP2PDF resolves embedded fonts relative to the working directory, and silently
    # writes a 0-BYTE PDF (while exiting 0 and printing "created successfully") when it
    # cannot find one. That is what made 12 of 300 forms unscorable -- they use
    # DocuDing.TTF, the Documaker dingbat face. Copy the faces in so the oracle works.
    for ttf in sorted((MSTRRES / "Fmres" / "deflib").glob("*.TTF")):
        dest = work / ttf.name
        if not dest.exists():
            dest.write_bytes(ttf.read_bytes())
    results = []
    for i, (name, category) in enumerate(sample, 1):
        row = {"form": name, "category": category, "status": "", "detail": ""}
        try:
            fap = FORMS / f"{name}.FAP"
            if not fap.exists():
                row["status"] = "no-fap"
                results.append(row)
                continue
            (work / f"{name}.FAP").write_bytes(fap.read_bytes())

            r = run([str(FAP2PDF), f"-I={name}.FAP", f"-X={FXR}"], work)
            ref_pdf = work / f"{name}.PDF"
            if not ref_pdf.exists():
                row["status"] = "legacy-render-failed"
                row["detail"] = (r.stdout or r.stderr or "")[-160:].replace("\n", " ")
                results.append(row)
                continue

            r = run([str(DEMO), "emit-html", name, str(work / f"{name}.html")], REPO)
            if not (work / f"{name}.html").exists():
                row["status"] = "emit-html-failed"
                row["detail"] = (r.stdout or r.stderr or "")[-160:].replace("\n", " ")
                results.append(row)
                continue

            our_pdf = work / f"{name}_ours.pdf"
            run([str(CHROME), "--headless", "--disable-gpu", "--no-pdf-header-footer",
                 f"--print-to-pdf={our_pdf}", (work / f'{name}.html').as_uri()], work, timeout=180)
            if not our_pdf.exists():
                row["status"] = "chromium-failed"
                results.append(row)
                continue

            row.update(score(ref_pdf, our_pdf))
            row["status"] = "ok"
        except Exception as exc:  # noqa: BLE001 — one bad form must not kill the sweep
            row["status"] = "error"
            row["detail"] = f"{type(exc).__name__}: {exc}"[:160]
            traceback.print_exc()
        results.append(row)
        print(f"[{i}/{len(sample)}] {name:<16} {category:<10} {row['status']:<22} "
              f"iou={row.get('mean_iou','-')} legacy_un={row.get('legacy_unmatched_pct','-')}%",
              flush=True)
    return results


def report(results, outdir):
    cols = ["form", "category", "status", "legacy_pages", "our_pages", "mean_iou",
            "legacy_unmatched_pct", "ours_unmatched_pct", "max_shift", "detail"]
    csv_path = outdir / "fidelity-sweep.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        for r in results:
            w.writerow(r)

    ok = [r for r in results if r["status"] == "ok"]
    lines = ["# Fidelity sweep", "",
             f"Forms attempted: **{len(results)}** · scored: **{len(ok)}** · "
             f"failed: **{len(results) - len(ok)}**", "",
             "## By category", "",
             "| category | n | scored | mean IoU | median legacy-unmatched % | worst |",
             "|---|---:|---:|---:|---:|---|"]
    for cat in sorted({r["category"] for r in results}):
        rs = [r for r in results if r["category"] == cat]
        s = [r for r in rs if r["status"] == "ok"]
        if s:
            iou = np.mean([r["mean_iou"] for r in s])
            un = np.median([r["legacy_unmatched_pct"] for r in s
                            if r["legacy_unmatched_pct"] is not None])
            worst = min(s, key=lambda r: r["mean_iou"])
            lines.append(f"| {cat} | {len(rs)} | {len(s)} | {iou:.3f} | {un:.1f}% | "
                         f"{worst['form']} ({worst['mean_iou']}) |")
        else:
            lines.append(f"| {cat} | {len(rs)} | 0 | — | — | — |")

    fails = [r for r in results if r["status"] != "ok"]
    if fails:
        lines += ["", "## Failures", "", "| form | category | status | detail |", "|---|---|---|---|"]
        for r in fails:
            lines.append(f"| {r['form']} | {r['category']} | {r['status']} | {r.get('detail','')} |")

    md_path = outdir / "fidelity-sweep.md"
    md_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"\nWrote {csv_path}\nWrote {md_path}")
    print("\n".join(lines[:14]))


if __name__ == "__main__":
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 10
    # Must be absolute: the work dir is turned into a file:// URI for Chromium.
    outdir = pathlib.Path(sys.argv[2]).resolve() if len(sys.argv) > 2 else REPO / "output"
    outdir.mkdir(parents=True, exist_ok=True)
    inv = load_inventory()
    sample = stratify(inv, n)
    picked = {f for f, _ in sample}
    pinned = [(f, "accepted") for f in ACCEPTED if f not in picked]
    sample += pinned
    print(f"Inventory: {len(inv)} forms. Sample: {len(sample)} across "
          f"{len(set(c for _, c in sample))} strata "
          f"(+{len(pinned)} pinned from the accepted set: "
          f"{', '.join(f for f, _ in pinned) or 'none needed'}).\n")
    report(sweep(sample, outdir / "sweep-work"), outdir)
