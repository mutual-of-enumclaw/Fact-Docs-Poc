# Handoff — fact-pdf-tools

Two separate initiatives live in this repo. **Which one you are continuing depends on the branch.**

| branch | initiative | plan doc |
|---|---|---|
| `main` | FAP → **GhostDraft `.gd`** export (Quote concept model, 419 quote forms) | `PROGRESS.md` §6 |
| `features/form-studio-p0` ← **you are here** | **Form Studio**: FAP → HTML → Chromium PDF, with measured fidelity gates | `FORM-STUDIO-PLAN.md` |

The six auto-loaded memory files describe the **`main`** GhostDraft work. Still accurate for that
branch, largely irrelevant here.

---

## Form Studio (`features/form-studio-p0`)

**Goal:** convert legacy Documaker FAP/DDT forms into a modern, editable, schema-bound format,
deterministically, and prove the output is indistinguishable from the legacy render.

`FORM-STUDIO-PLAN.md` is the design *and* the running lab notebook. §10–36 record what was measured,
**including the negative results** — read §19, §25, §32 and §34 before attempting anything on the
geometry axes, because they document five failed fixes and seven refuted hypotheses. Sections are
append-only; do not rewrite history.

### Where it stands — 1,000-form stratified sweep

| measure | result | gates? |
|---|---|---|
| **Tier 1 — nothing dropped** (content) | **912/913** | yes |
| **Line placement** (§36) | **794/807**, median 100% | candidate |
| Tier 2 — glyphs within 3.0pt | 878/914 | yes (superseded?) |
| Tier 2 at the 1.0pt quality bar | 552/914 | no, diagnostic |
| **Non-text ink** — recall / precision | **538/571** / median **100%** | recall gates |
| Glyph shape | median 0.85 | no, diagnostic |
| `.gd` content gate | 10/10 goldens, 301/301 quote forms | yes |

All 1000 render without error. `.gd` golden suite green. `emit-html` byte-identical across runs.

### THE OPEN DECISION — read this first

**Does line placement replace Tier 2 as the placement gate?** It is the one thing blocking a clean
statement of what is left, and it is Products' call, not an engineering one.

Products reviewed a sample (2026-08-23) and gave three verdicts that are now the project's ground truth:

| form | verdict | Tier 2 | line placement |
|---|---|---|---|
| `BAN01` | "looks identical" | 45.5% | **100%** |
| `BANSPECH` | "looks identical" | 42.2% | **100%** |
| `IM74561R` | "missing lines in various places" | 48.2% | **51%** (correctly fails) |
| `M7902AA` | boxes where legacy underlines | passes | 89.5% |

Tier 2 disagrees with the human on two of four; line placement agrees on all four, and on the four
forms accepted in August. **No Tier 2 tolerance fixes this** — BAN01/BANSPECH do not reach 90% even at
8pt, more than a line height. The quantity is wrong, not the threshold.

If line placement is adopted: most of the 36 Tier 2 failures resolve and the real remaining list is 13.

### The 13 remaining line-placement failures, all classified

| class | n | forms |
|---|---:|---|
| **Missing tab-leader fill lines** (REAL) | 4 | `IM74561R` `IM79014O` `M7208A` `IM74054O` |
| **Missing space at a run boundary** (REAL) | 6 | `IM7213OM`, `EB9909SCHEDA`, `EP990{7,8,9}SCHED*`, `EP9910SCHEDC` |
| Two-line header cell 2.55pt high (small) | 3 | `M7902AA` `FM7902AB` `M7902ABa` |

**Missing space** is the best next fix: clearest mechanism, affects legibility (`Replaced-- The`,
`EB 99 09 06 16Includes copyrighted material`). Note **Tier 1 cannot see it** — it strips all
whitespace before comparing, so a lost space can never fail it. Only line placement catches it.

**Tab leaders** are decoded but deferred: `M,P1` is a tab stop whose third field is a leader character
(95 = `_`, 46 = `.`). Documaker fills from the current position to the stop with it; the FAP contains
no underscores at all. Only **24 of 4210 forms** use one, and reproducing it needs the text-flow model
this design deliberately avoids.

### Other open work

- **`X,` edge semantics are only partly decoded** (§31, §32, §36). `(20,20)`, `(50,50)`, `(15,15)`,
  `(33,33)`, `(30,30)`, `(36,36)`, `(40,40)` are rectangles. A `(24,24)`/`(25,25)` record sharing its
  row with **≥3 siblings** is an underline row (97%, now implemented). A **lone** record in those
  groups is a rectangle only 58–80% of the time — genuinely undecoded. `FapLine.Group` carries the
  group through the parser.
- **Intra-run horizontal drift** (§19, §25). Five mechanisms tried, all worse or a wash. Anything that
  adjusts a single number per run is exhausted; only per-glyph positioning is untried. Products says
  it is invisible, so this may not be worth fixing at all.
- **A declarative system-value registry** (total pages, current page, edition, print date). The last
  known content gap, currently papered over by the Tier 1 fill allowance.
- **Packet assembly** — ~31% of the library are fragments, so a converted form is not always a
  deliverable document, and FAP2PDF is not a valid oracle for one.
- **Image sign-off.** Images render (§28) but **no gate can check them** — FAP2PDF embeds none.
- **P3 onward** — binding layer, editor, runtime (§8) are all still ahead.

### Done, so nobody redoes it

The baseline anchor (§18), shading (§22), `M,PX` rules and box sizing (§21, §23), `M,I` bullets (§24),
the DocuDings font substitution and the 0-byte oracle (§27), `.LOG` image decoding (§28 — all 76
assets, every library reference resolves), the `.gd` content gate (§20), sub-point rectangles (§35),
sibling-row underlines (§36).

---

## Hard-won rules — do not relearn these

- **Validate any new gate against the forms Products accepted** (`EB2410A`, `A0238C`, `EB22489Q`,
  `P0010G`, and now `BAN01`, `BANSPECH`) *before* trusting it. **Seven** metrics here have reported
  defects the render did not have. A gate that invents work is worse than no gate.
- **A gate that cannot FAIL is worse still.** Two shipped that way: the Tier 1 fill allowance was
  vacuous on forms declaring no fields (`0 >= 0`), hiding 808 missing characters until a human noticed;
  and a leader-collapse regex deleted leader runs instead of collapsing them, which would have hidden
  the same defect. Test sensitivity deliberately — delete characters and confirm the gate fires.
- **Ask what a check is BLIND to.** Five blind spots so far: Tier 2 could not see drift inside a run;
  Tier 1 and Tier 2 are text-only and could not see a missing logo or rule; **Tier 1 strips whitespace
  so it cannot see a missing space**; nothing measured glyph identity (a wrong font passed everything);
  and the `.gd` golden suite passed real parser changes because no form in it used the construct.
- **Measure both directions.** A recall-only ink gate cannot see ink you INVENT — and it actively
  *rewards* over-drawing, which is why boxes-instead-of-underlines survived until a human looked.
- **How accurate a heuristic must be depends on WHICH WAY it fails.** An 80% rule ships where being
  wrong draws a spurious rule; a 92% rule was reverted where being wrong drops real edges.
- **A better model of one quantity is not a better model of another.** A change that cut run-width
  error 5.4× still regressed glyph placement on every accepted form.
- **Never let a metric impose its own tokenisation.** Tier 1 had to go to character level, Tier 2 to
  glyph level, and line placement needed a grouping-tolerant match after **six** rounds of threshold
  tuning traded one artefact for another.
- **Correlation generates a hypothesis; it never closes one.** Prefer finding the mechanism over
  fitting a correction — eight calibration variants failed before the real cause (box-bottom anchoring)
  turned out to be a one-line geometry fix.
- **Never measure a fix without confirming the artefact is newer than the binary**, at the point of
  MEASUREMENT rather than of rendering. `output/sweep-work/` caches per form, and the set you compare
  is usually not the set you re-rendered. This has bitten twice.
- Determinism rule 4 is binding: when inference cannot decide, emit Layer A verbatim and flag
  `needs-review`. Never pattern-guess on document content (hence "Page N of" is *not* special-cased).

## Environment gotchas

- **`FAP2PDF.EXE` is a deficient oracle in three known ways**: it embeds **no images**, renders
  composable fragments (`PSUM-*`, `PSCP-*`, `QCPP*`, `QFRM*`, `BQ-*`) nearly blank, and substitutes
  **base-14 fonts without embedding them** — so the legacy column's typeface comes from the
  rasterizer, not Documaker. Do not treat a difference in any of those three as our defect.
- It needs `FSISYS.INI` **and the Documaker TTFs** in the working directory (`sweep.py` copies both).
  Without the fonts it prints "created successfully", writes a **0-byte** PDF and exits 0.
- Under Git Bash use `-I=` not `/I=` (MSYS mangles the path).
- `sweep.py` needs an **absolute** output path with **forward slashes** — a `C:\\...` argument gets
  mangled by bash into a bogus directory.
- **Bash heredocs eat backslashes.** Writing Python or C# with `\n`, `\"` or `\\` through a heredoc
  silently corrupts it; this cost several rounds. Use the Write tool, or build strings from `chr(92)`.
- Stale `dotnet run` processes lock DLLs. Kill the listener on :5035 before rebuilding the server.

## Tooling

Sweep artefacts are cached in `output/sweep-work/` (FAP, legacy `.PDF`, `.html`, `_ours.pdf` per form),
so the gates re-measure **without re-rendering**. Re-run `sweep.py` only after changing `emit-html`.

```bash
dotnet run --project demo/FapPdfTools.Demo.csproj -- emit-html EB2410A out.html
python tools/sweep.py 200 "C:/src/fact-pdf-tools/output"   # full pipeline; absolute, forward slashes
python tools/dashboard.py                                  # ALL measures, per form and stratum
python tools/contentdiff.py                                # Tier 1 gate
python tools/lineplace.py                                  # line placement (candidate gate)
python tools/tier2.py                                      # Tier 2 (3.0pt gate + 1.0pt diagnostic)
python tools/nontextink.py                                 # rules/shading/artwork, recall + precision
python tools/glyphshape.py --selftest EB2410A              # glyph identity; prove it reacts to shape
python tools/gdcontent.py                                  # .gd content gate
python tools/defectzoom.py FORM --reason line|ink          # zoom to the disagreeing region
python tools/reviewpack.py                                 # build a Products review pack
python tools/logdecode.py --scan                           # decode every Documaker .LOG
```

After changing `emit-html`, re-render **every form you intend to measure** and confirm each
`_ours.pdf` is newer than the demo binary before reading any score. A full 1,000-form re-render takes
roughly 40 minutes; use a background task.

`dotnet run -- regress` is the golden-file suite for the **`.gd`** generator — run it after any change
to the shared FAP parser in `core/`, since Form Studio and GhostDraft share it. Four parser bugs were
found via Form Studio that had silently corrupted the `.gd` path too.
