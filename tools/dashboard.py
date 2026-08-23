"""Fidelity dashboard: every gate, per form and per stratum, from the cached sweep.

Runs the three acceptance gates over `output/sweep-work/` and writes
`output/fidelity-dashboard.{csv,md}`. Reads cached artefacts only -- it never renders --
so it is cheap to re-run after changing a gate. Re-run `tools/sweep.py` first if the
EMITTER changed, or the artefacts are stale and every number here is about the old build.

The three gates, and what each is blind to (which is the point of running all of them):

  Tier 1  contentdiff  - does our render SAY the same thing?   Blind to placement.
  Tier 2  tier2        - is every glyph in the right PLACE?    Blind to non-glyph ink.
  Ink     nontextink   - are the rules, shading and artwork there?  Blind to text.
  Shape   glyphshape   - are they the RIGHT glyphs? DIAGNOSTIC only, not a gate.

A form is "green" only if it passes all three GATES. The shape column is reported
alongside because a wrong font is invisible to every gate (section 27), but it is too
noisy per-glyph to gate on -- see its module docstring.

Usage:  python tools/dashboard.py [FORM ...]
"""
import csv
import pathlib
import statistics
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
import contentdiff  # noqa: E402
import nontextink   # noqa: E402
import tier2        # noqa: E402
import glyphshape   # noqa: E402

OUT = pathlib.Path(r"C:\src\fact-pdf-tools\output")
SWEEP = OUT / "fidelity-sweep.csv"


def collect(forms, strata):
    rows = []
    for f in forms:
        t1 = contentdiff.compare(f)
        t2 = tier2.score(f)
        t2t = tier2.score(f, tier2.DIAG_TOL_PT)
        ink = nontextink.score(f)
        gs = glyphshape.score(f)
        rows.append({
            "form": f,
            "stratum": strata.get(f, "?"),
            "t1_nothing_dropped": None if t1 is None else t1["nothing_dropped"],
            "t2_pct": None if t2 is None else t2["pct"],
            "t2_tight_pct": None if t2t is None else t2t["pct"],
            "ink_pct": None if ink is None else ink["pct"],
            "ink_px": None if ink is None else ink["ink"],
            "has_image": None if ink is None else ink["img"],
            "shape_median": None if gs is None else gs["median"],
        })
    return rows


def verdict(r):
    """Green only if every gate that could be evaluated passed."""
    if r["t1_nothing_dropped"] is False:
        return "FAIL:content"
    if r["t2_pct"] is not None and r["t2_pct"] < tier2.THRESHOLD_PCT:
        return "FAIL:placement"
    if r["ink_pct"] is not None and r["ink_pct"] < nontextink.THRESHOLD_PCT:
        return "FAIL:ink"
    if r["t1_nothing_dropped"] is None and r["t2_pct"] is None and r["ink_pct"] is None:
        return "unscored"
    return "green"


def pct(n, d):
    return f"{100 * n / d:.0f}%" if d else "-"


def main(forms, strata):
    rows = collect(forms, strata)
    for r in rows:
        r["verdict"] = verdict(r)

    scored = [r for r in rows if r["verdict"] != "unscored"]
    green = [r for r in scored if r["verdict"] == "green"]

    def gate(key, thresh):
        vals = [r for r in rows if r[key] is not None]
        ok = [r for r in vals if r[key] >= thresh]
        return len(ok), len(vals), [r[key] for r in vals]

    t2ok, t2n, t2v = gate("t2_pct", tier2.THRESHOLD_PCT)
    tiok, tin, tiv = gate("t2_tight_pct", tier2.THRESHOLD_PCT)
    inkok, inkn, inkv = gate("ink_pct", nontextink.THRESHOLD_PCT)
    t1v = [r for r in rows if r["t1_nothing_dropped"] is not None]
    t1ok = [r for r in t1v if r["t1_nothing_dropped"]]

    lines = [
        "# Fidelity dashboard", "",
        f"Forms: **{len(rows)}** · scored by at least one gate: **{len(scored)}** · "
        f"green on every gate: **{len(green)}** ({pct(len(green), len(scored))})", "",
        "| gate | passing | median |", "|---|---|---|",
        f"| Tier 1 — nothing dropped | **{len(t1ok)}/{len(t1v)}** | — |",
        f"| Tier 2 — glyphs within {tier2.TOL_PT}pt | **{t2ok}/{t2n}** | "
        f"{statistics.median(t2v):.1f}% |" if t2v else "| Tier 2 | — | — |",
        f"| Tier 2 quality bar ({tier2.DIAG_TOL_PT}pt, not a gate) | {tiok}/{tin} | "
        f"{statistics.median(tiv):.1f}% |" if tiv else "| Tier 2 tight | — | — |",
        f"| Non-text ink | **{inkok}/{inkn}** | {statistics.median(inkv):.1f}% |"
        if inkv else "| Non-text ink | — | — |",
        (lambda v: f"| Glyph shape (diagnostic, not a gate) | — | {statistics.median(v):.2f} |"
         if v else "| Glyph shape | — | — |")(
            [r["shape_median"] for r in rows if r["shape_median"] is not None]),
        "",
        f"Non-text ink skips forms under {nontextink.MIN_INK} non-text pixels — "
        f"{sum(1 for r in rows if r['ink_pct'] is None)} of {len(rows)} here — because "
        "there is too little to judge. They are NOT counted as passes.", "",
        "## By stratum", "",
        "| stratum | n | green | Tier 1 | Tier 2 | ink |", "|---|---:|---|---|---|---|",
    ]
    for st in sorted({r["stratum"] for r in rows}):
        sub = [r for r in rows if r["stratum"] == st]
        g = [r for r in sub if r["verdict"] == "green"]
        a = [r for r in sub if r["t1_nothing_dropped"]]
        b = [r for r in sub if r["t2_pct"] is not None and r["t2_pct"] >= tier2.THRESHOLD_PCT]
        c = [r for r in sub if r["ink_pct"] is not None and r["ink_pct"] >= nontextink.THRESHOLD_PCT]
        bn = sum(1 for r in sub if r["t2_pct"] is not None)
        cn = sum(1 for r in sub if r["ink_pct"] is not None)
        an = sum(1 for r in sub if r["t1_nothing_dropped"] is not None)
        lines.append(f"| {st} | {len(sub)} | {len(g)} | {len(a)}/{an} | {len(b)}/{bn} | {len(c)}/{cn} |")

    fails = [r for r in scored if r["verdict"] != "green"]
    fails.sort(key=lambda r: (r["verdict"], r["ink_pct"] if r["ink_pct"] is not None else 999))
    if fails:
        lines += ["", f"## Failures ({len(fails)})", "",
                  "| form | stratum | why | Tier 2 | ink |", "|---|---|---|---|---|"]
        for r in fails:
            t2 = "-" if r["t2_pct"] is None else f"{r['t2_pct']:.1f}%"
            ink = "-" if r["ink_pct"] is None else f"{r['ink_pct']:.1f}%"
            lines.append(f"| {r['form']} | {r['stratum']} | "
                         f"{r['verdict'].split(':')[1]} | {t2} | {ink} |")

    md = OUT / "fidelity-dashboard.md"
    md.write_text("\n".join(lines) + "\n", encoding="utf-8")
    csv_path = OUT / "fidelity-dashboard.csv"
    with csv_path.open("w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    print("\n".join(lines[:16]))
    print(f"\nWrote {md}\nWrote {csv_path}")
    return len(fails)


if __name__ == "__main__":
    args = sys.argv[1:]
    strata = {}
    if SWEEP.exists():
        for r in csv.DictReader(SWEEP.open(encoding="utf-8")):
            strata[r["form"]] = r["category"]
        if not args:
            args = [r for r, _ in strata.items()
                    if (nontextink.WORK / f"{r}.PDF").exists()]
    main(args, strata)
