"""Build a Products review pack: side-by-side comparisons chosen to answer real questions.

The automated gates are calibrated against just four forms Products accepted in August, which
is a thin basis for a 4,462-form migration. This assembles a sample designed to widen that,
and it is deliberately NOT a random draw -- a random sample of a 94%-green library is mostly
forms we already believe are fine, which asks the reviewer for nothing.

The sample is built from groups, each with ONE question attached, and the groups change as the
open questions change. A pack that re-asks a settled question wastes the only scarce resource
here, which is a reviewer's attention.

Currently open (see FORM-STUDIO-PLAN sections 37-38):

  over-drawn      THE DECISION. We draw a closed box where Documaker draws an open shape --
                  71 forms below 90% vector precision. It is undecidable from the FAP record
                  (section 38), so closing it means building table-structure inference, which
                  is a large piece of work. Whether it is worth doing is a Products judgement:
                  does a fully-ruled table where legacy leaves the sides open read as wrong?
  new: underline  Bit 0 of the A,T1 flag, 911 runs across 186 forms, shipped 2026-08-24 and
                  never seen by a human. Did it land in the right places?
  new: spacing    Leading whitespace inside text tokens, 11,599 records across 1,340 forms,
                  shipped 2026-08-24 and never seen by a human.
  gate choice     BAN01/BANSPECH: Tier 2 rates them 45.5% and 42.2%, line placement 100%, and
                  Products called them identical in August. Included so the verdict is on the
                  record against the CURRENT build, which is what the gate decision rests on.
  image-bearing   No automated check can see artwork -- FAP2PDF embeds no images at all -- so
                  these are the one thing only a person can sign off. Note that the LEGACY
                  column has a blank box where the logo goes; ours is the correct one.

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
    # Match on a separator-normalised name. The list said "PSUM-" and the library also has
    # PSUM_RVPD_DTL, which slipped through into a pack -- its legacy render is 0 characters
    # and 2 drawings, so its 2.3% rule precision is entirely the oracle, and it would have
    # asked a reviewer to judge a blank page.
    n = name.upper().replace("_", "-")
    return any(n.startswith(p.upper().replace("_", "-")) for p in FRAGMENT_PREFIXES)


def unjudgeable(name):
    """True if the LEGACY render is too empty to compare against.

    A name-prefix list is the wrong instrument for this and always will be -- it encodes a
    guess about which forms FAP2PDF renders blank, and it is one new naming convention away
    from being wrong. Ask the artefact instead: if the reference has almost no text on it,
    there is nothing for a person to judge, whatever the form is called.
    """
    import fitz
    pdf = OUT / "sweep-work" / f"{name}.PDF"
    if not pdf.exists():
        return True
    try:
        d = fitz.open(str(pdf))
        chars = sum(len(pg.get_text().strip()) for pg in d)
        d.close()
    except Exception:
        return True
    return chars < 200


VECTORRULES = OUT / "vectorrules.csv"
S = 72.0 / 2400.0


def _fap_counts(form):
    """(underlined runs, leading-space runs) declared by the FAP -- what the fix should show."""
    import re
    f = OUT / "sweep-work" / f"{form}.FAP"
    if not f.exists():
        return 0, 0
    text_re = re.compile(r"^(T|M,TT),\([^)]*\),\([^)]*\),(\d+),(.*)$")
    at1_re = re.compile(r"^A,T1,\s*\"[^\"]*\",\s*-?\d+,\([^)]*\),\s*(-?\d+)")
    lines = f.read_text(encoding="latin-1", errors="replace").split("\n")
    ul = lead = 0
    for i, raw in enumerate(lines):
        m = text_re.match(raw.strip())
        if not m or len(m.group(3).strip()) <= 1:
            continue
        if m.group(3)[:1] == " ":
            lead += 1
        nxt = lines[i + 1].strip() if i + 1 < len(lines) else ""
        a = at1_re.match(nxt)
        if a and (int(a.group(1)) & 1):
            ul += 1
    return ul, lead


def question_groups(rows, n_over, n_new, n_image):
    """The groups that answer the questions actually open right now."""
    picked, seen, fams = [], set(), collections.Counter()
    byform = {r["form"]: r for r in rows}

    def take(label, names, n):
        got = 0
        for nm in names:
            if nm in seen or fams[family(nm)] >= 1 or is_fragment(nm):
                continue
            if nm not in byform or unjudgeable(nm):
                continue
            seen.add(nm)
            fams[family(nm)] += 1
            picked.append((label, byform[nm]))
            got += 1
            if got >= n:
                break

    # 1. THE DECISION: worst over-draw, by vector precision.
    # PURE over-draw only: low precision AND high recall, i.e. we add edges without dropping
    # any. Forms that also MISS edges are a different defect -- A2134FN drops the top rule and
    # draws the bottom one 12pt lower, which lands on the text and reads as a strikethrough --
    # and putting one in this group gets a reaction to the strikethrough instead of an answer
    # about over-drawing. 24 of the 71 low-precision forms are pure.
    over, displaced = [], []
    if VECTORRULES.exists():
        vr = [r for r in csv.DictReader(VECTORRULES.open(encoding="utf-8"))
              if r["precision"] and r["recall"] and float(r["precision"]) < 90]
        vr.sort(key=lambda r: float(r["precision"]))
        over = [r["form"] for r in vr if float(r["recall"]) >= 95]
        displaced = [r["form"] for r in vr if float(r["recall"]) < 95]
    take("DECISION: we draw a closed box, legacy draws an open shape -- does this read as wrong?",
         over, n_over)
    take("SEPARATE DEFECT: a rule drawn ~12pt below where Documaker puts it, landing on the "
         "line of text underneath. Is this as bad as it looks to us?",
         displaced, 2)

    # 2 & 3. The two fixes that no human has seen. Most instances first.
    ul, lead = [], []
    for f in byform:
        if is_fragment(f) or unjudgeable(f):
            continue
        u, l = _fap_counts(f)
        if u:
            ul.append((u, f))
        if l:
            lead.append((l, f))
    # Prefer forms that do NOT also carry the over-draw defect. A reviewer asked "does the
    # text start in the right place?" on a form that is also visibly over-ruled answers "this
    # looks wrong", and we learn nothing about the fix. Sort clean-first, then by instances.
    prec = {}
    if VECTORRULES.exists():
        prec = {r["form"]: float(r["precision"]) for r in
                csv.DictReader(VECTORRULES.open(encoding="utf-8")) if r["precision"]}

    def clean_first(pairs):
        return [f for _, f in sorted(pairs, key=lambda kv: (prec.get(kv[1], 100) < 90, -kv[0]))]

    take("NEW 2026-08-24: underlines, previously not drawn at all -- are they in the right places?",
         clean_first(ul), n_new)
    take("NEW 2026-08-24: leading spaces inside text runs, previously deleted -- is the text "
         "positioned correctly?",
         clean_first(lead), n_new)

    # 4. The gate decision, against the current build.
    take("GATE CHOICE: Products called these identical in August; Tier 2 still rates them "
         "42-45%, line placement 100%. Still identical?",
         ["BAN01", "BANSPECH"], 2)

    # 5. Artwork -- still the one thing no check can see.
    img = sorted((r for r in rows if r.get("has_image") == "True" and r.get("verdict") == "green"),
                 key=lambda r: -(num(r, "ink_px") or 0))
    take("ARTWORK: no automated check can verify this. The LEGACY column has a BLANK BOX where "
         "the logo goes -- that is the oracle's limitation, not our defect.",
         [r["form"] for r in img], n_image)
    return picked


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
    picked = question_groups(rows, n_over=n_fail, n_new=n_pass, n_image=n_image)

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
        w.writerow(["form", "the question", "stratum", "Tier 2 %", "ink recall %",
                    "vector recall %", "vector precision %", "verdict"])
        vrmap = {}
        if VECTORRULES.exists():
            vrmap = {r["form"]: r for r in csv.DictReader(VECTORRULES.open(encoding="utf-8"))}
        for label, r in picked:
            v = vrmap.get(r["form"], {})
            w.writerow([r["form"], label, r["stratum"], r.get("t2_pct"), r.get("ink_pct"),
                        v.get("recall", ""), v.get("precision", ""), r["verdict"]])

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
