"""Build a Products review pack: side-by-side comparisons chosen to answer real questions.

The automated gates are calibrated against just four forms Products accepted in August, which
is a thin basis for a 4,462-form migration. This assembles a sample designed to widen that,
and it is deliberately NOT a random draw -- a random sample of a 94%-green library is mostly
forms we already believe are fine, which asks the reviewer for nothing.

The sample is built from three groups, each with a question attached:

  image-bearing   No automated check can see artwork -- FAP2PDF embeds no images at all -- so
                  these are the one thing only a person can sign off. Note that the LEGACY
                  column has a blank box where the logo goes; ours is the correct one.
  gate failures   Forms our checks reject. Several look identical by eye, which would mean
                  the threshold is mis-calibrated. A reviewer's "these are fine" is worth
                  more than any further tuning.
  gate passes     A spread across strata, to catch anything the gates are blind to.

Selection is deterministic (sorted, fixed rules) so the same dashboard yields the same pack.
It de-duplicates by form FAMILY -- four variants of BOPDEC1 look like four forms and teach one
thing -- and excludes composable fragments, because FAP2PDF cannot validly render those
(FORM-STUDIO-PLAN section 12) so a reviewer would be judging a broken reference.

Usage:  python tools/reviewpack.py [--n-image 6] [--n-fail 5] [--n-pass 2]
Reads output/fidelity-dashboard.csv; writes output/review-pack/.
"""
import collections
import csv
import pathlib
import re
import shutil
import subprocess
import sys

REPO = pathlib.Path(__file__).resolve().parent.parent
OUT = REPO / "output"
PACK = OUT / "review-pack"
COMPARE = OUT / "compare"
DASH = OUT / "fidelity-dashboard.csv"

# Prefixes of composable fragments: FAP2PDF renders these nearly blank, so they are not a
# fair thing to put in front of a reviewer.
FRAGMENT_PREFIXES = ("PSUM-", "PSCP-", "PSBP-", "QCPP", "QFRM", "QBOP", "BQ-", "CPPQ", "CPQ",
                     "FRMQ", "QTE_", "PSDF-", "PSFA-")


def num(row, key, default=None):
    v = row.get(key)
    return float(v) if v not in (None, "", "None") else default


def family(name):
    """Crude family key so near-identical variants collapse: BOPDEC1B -> BOPDEC1."""
    m = re.match(r"([A-Za-z\-_]+\d{0,2})", name)
    return (m.group(1) if m else name).upper()


def is_fragment(name):
    return any(name.upper().startswith(p) for p in FRAGMENT_PREFIXES)


def select(rows, n_image, n_fail, n_pass):
    picked, seen, fams = [], set(), collections.Counter()

    def take(label, cands, n):
        got = 0
        for r in cands:
            nm = r["form"]
            if nm in seen or fams[family(nm)] >= 1:
                continue
            seen.add(nm)
            fams[family(nm)] += 1
            picked.append((label, r))
            got += 1
            if got >= n:
                break

    usable = [r for r in rows if not is_fragment(r["form"])]

    # Most artwork first: a bigger logo gives the reviewer more to judge.
    img = sorted((r for r in usable if r["has_image"] == "True" and r["verdict"] == "green"),
                 key=lambda r: -(num(r, "ink_px") or 0))
    take("image (no automated check can verify)", img, n_image)

    # Worst first: if the worst looks fine, the threshold is wrong.
    fp = sorted((r for r in usable if r["verdict"] == "FAIL:placement"),
                key=lambda r: num(r, "t2_pct", 100))
    fi = sorted((r for r in usable if r["verdict"] == "FAIL:ink"),
                key=lambda r: num(r, "ink_pct", 100))
    take("FAILS our placement check", fp, max(1, n_fail - 2))
    take("FAILS our rules check", fi, min(2, n_fail))

    by_stratum = collections.defaultdict(list)
    for r in usable:
        if r["verdict"] == "green":
            by_stratum[r["stratum"]].append(r)
    for st in sorted(by_stratum):
        take(f"passes / {st}", sorted(by_stratum[st], key=lambda r: -(num(r, "ink_px") or 0)),
             n_pass)
    return picked


def main(n_image, n_fail, n_pass):
    if not DASH.exists():
        sys.exit(f"no {DASH} -- run tools/dashboard.py first")
    rows = list(csv.DictReader(DASH.open(encoding="utf-8")))
    picked = select(rows, n_image, n_fail, n_pass)

    PACK.mkdir(parents=True, exist_ok=True)
    forms = [r["form"] for _, r in picked]
    subprocess.run([sys.executable, str(REPO / "tools" / "sidebyside.py"), *forms,
                    "--dpi", "150"], cwd=str(REPO), check=False)

    copied, missing = 0, []
    for f in forms:
        src = COMPARE / f"{f}_p1_compare.png"
        if src.exists():
            shutil.copy2(src, PACK / src.name)
            copied += 1
        else:
            missing.append(f)

    manifest = PACK / "manifest.csv"
    with manifest.open("w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["form", "why it is in the pack", "stratum", "placement %", "rules %", "verdict"])
        for label, r in picked:
            w.writerow([r["form"], label, r["stratum"], r.get("t2_pct"), r.get("ink_pct"),
                        r["verdict"]])

    print(f"\n{copied} comparisons in {PACK}")
    if missing:
        print(f"  MISSING (no cached render): {', '.join(missing)}")
    print(f"  wrote {manifest}")
    print("  NOTE: write/refresh README.md by hand -- the framing is the point of the pack,")
    print("        especially the warning that the legacy column has no logos.")


if __name__ == "__main__":
    a = sys.argv[1:]
    def opt(name, d):
        return int(a[a.index(name) + 1]) if name in a else d
    main(opt("--n-image", 6), opt("--n-fail", 5), opt("--n-pass", 2))
