# Handoff — fact-pdf-tools

Two separate initiatives live in this repo. **Which one you are continuing depends on the branch.**

| branch | initiative | plan doc |
|---|---|---|
| `main` | FAP → **GhostDraft `.gd`** export (Quote concept model, 419 quote forms) | `PROGRESS.md` §6 |
| `features/form-studio-p0` ← **you are here** | **Form Studio**: FAP → HTML → Chromium PDF, with a measured fidelity gate | `FORM-STUDIO-PLAN.md` |

The six auto-loaded memory files describe the **`main`** GhostDraft work. They are still accurate for
that branch and largely irrelevant to Form Studio.

---

## Form Studio (`features/form-studio-p0`)

**Goal:** convert legacy Documaker FAP/DDT forms into a modern, editable, schema-bound format,
deterministically, and prove the output is indistinguishable from the legacy render. Read
`FORM-STUDIO-PLAN.md` end to end — it is the design *and* the running lab notebook, and §10–35 record
what was measured, including the negative results. Sections are append-only; do not rewrite history.

### Where it stands (1,000-form stratified sweep, all 1000 rendered, 946 scored)

Run `python tools/dashboard.py` for the live version — it writes
`output/fidelity-dashboard.{csv,md}` with a per-form and per-stratum breakdown.

| gate | result |
|---|---|
| **Green on every gate** | **893/946 (94%)** |
| **Tier 1 — nothing dropped** (content) | **913/913** ✅ |
| **Tier 2 — ≥90% of glyphs within 3.0pt** (placement) | **878/914**, median 99.6% |
| **Non-text ink — ≥90% of the legacy's non-glyph ink** | **553/571**, median 100.0% |
| Tier 2 at the 1.0pt quality bar (diagnostic, not a gate) | 552/914, median 92.6% |
| Non-text ink precision, and glyph shape (diagnostics) | **100.0%** · 0.85 |

Every stratum is above 78% green; images is the best at 56/60. Two honest caveats the tools print
rather than hide: non-text ink **skips 142 of 300 forms** for having under 200 non-text pixels (not
counted as passes), (the 12 forms that FAP2PDF used to fail on are fixed — see §27; `sweep.py` now copies the Documaker
fonts into the work directory).

**Content and vertical placement are closed.** Tier 1 is 259/259, and `|dy| > 1pt` is 0.0% of glyphs on
every accepted form. Everything still open is horizontal placement or missing artwork.

The 3.0pt gate is set by the worst accepted form (`A0238C`, 90.6%) and is **not** a claim that 3pt is
good. Drive the 1.0pt column down; do not loosen the gate.

**The baseline mystery is solved (§18).** Documaker anchors the text baseline to the **bottom** of the
declared box (`row2`), not the top — measured at −0.09pt with stdev 0.06 over 17,193 records. The old
"per-form sign flip" was just box height varying per form. All calibration is **deleted**; it was fitted
to the old anchor and would now corrupt the geometry. Do not reintroduce it.

### Non-text ink (§21–23, 26)

`tools/nontextink.py` exists because Tier 1 and Tier 2 are both text-only — a form could be missing its
entire logo, or every rule on the page, and still score ~100%. It found four defects in two days:

- **`M,PX`** — a line record nested in a text area, silently discarded; 315 of 4210 forms rendered with
  no rules at all. It draws horizontal edges only, never a rectangle (§21, §23).
- **`X,` style** — a non-zero style means the box is **filled**, from a measured shade table (§22).
- **box sizing** — `.box` used content-box, so every rectangle was ~1pt oversized (§23).
- **`M,I`** — bullets, 634 records across 60 forms, nothing drawn for them (§24).

Remaining on this axis: 4 ink failures of 158. `MC1690C` (70.7%) is the image gap. `EG0421D2` (56.4%)
and `M7450B` (76.8%) are `M,PX` edge-rule misses — **that rule is an 80%-accurate heuristic, not a
decoded format (§26)**; read that section before trusting it. `EP04453R` is 85.5%.

**Images are now implemented (§28).** The `.LOG` format is decoded — all 76 assets on disk, and every
reference in the library resolves (205 forms, 255 records, 0 unresolved). But **the harness cannot check
them**: FAP2PDF embeds no images at all, so the PDF channel is not a valid oracle for artwork and image
fidelity needs visual sign-off rather than a gate.

### Also open: intra-run horizontal drift (§19, §25)



`dx` accumulates along a run — run STARTS are exact, then drift reaches −7.4pt by the fortieth glyph on
`BAN02INT`. This is the one remaining named text defect, and it is what holds the 1.0pt quality bar at
156/260.

**FIVE mechanisms have been tried and every one measured worse or a wash. §19 and §25 have the numbers
— do not repeat them:** dropping the FXR correction, per-word FXR offsets, multiplicative correction via
point size, box-fit `scaleX` (±15%), and box-fit with a ±5% tight-box guard. The shipped additive
`letter-spacing` stays **because it measures best, not because it is the truest model.**

The key lesson from attempt 5: **a better width model is not a better placement model.** That attempt
cut the run-width error 5.4× (0.87% → 0.16% median over 1,512 runs) and still regressed every accepted
form, because matching a run's total width says nothing about where glyphs land inside it. Anything that
adjusts a single number per run is exhausted.

Two things about the legacy render are now measured facts, and both are the foundation for a fifth
attempt:
- The correction is **multiplicative** — per-character advances between two legacy spans of one face
  relate by a pure ratio with cv **0.0000**, versus 0.15–0.40 for the additive model.
- Documaker **fits each token to its declared FAP box** (`col2 − col1`), not to the FXR width table:
  median ratio 0.9996–1.0057, 89–93% of records inside 2%. The catch is the tail — a declared box is
  often padding rather than a tight fit, and nothing in the record distinguishes the two, which is why
  box-fitting everything backfires.

A correct fix needs **per-glyph positioning driven by the legacy PDF's actual TJ offsets** — reproducing
the layout rather than re-deriving it. Whole-run scale factors are exhausted.

### Hard-won rules — do not relearn these

- **Validate any new gate against a form Products accepted** (`EB2410A`, `A0238C`, `EB22489Q`,
  `P0010G`) *before* trusting it. **Four** separate metrics have now reported defects the render did
  not have — ink IoU (scores an eyeball-perfect form at 0.247), word-level diffing, reading-order
  comparison, and run-level Tier 2 (220/220 of its "missing" strings were present on the page). A gate
  that invents work is worse than no gate.
- **A metric that imposes its own tokenisation will lie to you.** Both Tier 1 and Tier 2 had to move to
  glyph/character level for exactly this reason. Compare marks and positions, not strings.
- **Ask what a check is blind to, not just what it reports.** This has now bitten FOUR times:
  **nothing measures glyph identity** — DocuDings symbols rendered as Latin letters and every gate
  passed, because Tier 1 compares character codes, Tier 2 compares positions, and the ink gate masks
  text out (§27). The other three: run-level Tier 2 could not see drift *inside* a run; Tier 1 and Tier 2 are both
  text-only and could not see a missing logo or a missing rule; and the golden `.gd` suite reported
  "8 OK, no regressions" through a real parser change because none of its forms used the construct.
  A suite that does not contain the construct cannot guard it. This happened TWICE in one day (M,PX and
  X, shading), so check suite coverage against the construct inventory rather than assuming it.
- **Ink IoU is never pass/fail.** Tier 1 (content) then Tier 2 (placement) are the gates; IoU and the
  overlay images are for human smoke-checking only.
- **Any comparison that linearises a page needs a baseline band, not a rounded coordinate** — this has
  bitten three times.
- **Never measure a fix without checking the artefact is newer than the binary.** This has now bitten
  twice. The second time, the re-render script *had* the check but only covered the sweep list, while
  the forms being compared came from a different list and were stale. **Put the check at the point of
  measurement, not the point of rendering** — `output/sweep-work/` caches artefacts per form, and the
  accepted-form set is not a subset of the sweep sample.
- **Correlation generates a hypothesis; it never closes one.** Twice a font-id correlation suggested a
  cause that measurement refuted.
- **How accurate a heuristic must be depends on which way it FAILS.** The `M,PX` edge rule ships at 80%
  because being wrong there draws a spurious rule (costs precision, never recall). The identical-accuracy
  `X,(24,24)` rule was reverted because being wrong there drops real edges — and recall is what gates
  (§32). Ask what the wrong answer costs before judging the hit rate.
- **A new measurement's first surprising result is more likely a bug in the measurement.** Six metrics
  here have now reported defects the render did not have. The glyph diagnostic (§30) put five forms at
  wrong-font levels; their fonts were fine and the crop was wrong. Check the accused before filing it.
- **Test a new gate for SENSITIVITY, not just for passing.** Delete characters deliberately and confirm
  it fires. Building the .gd gate (§20) caught two ways it could have stayed silent -- escapes eaten as
  control words, and surplus non-content text absorbing real drops (3 deleted letters showed as 1).
- **A measured fact does not guarantee an exploitable fix.** Section 19 established two true things
  about Documaker's layout, and both faithful implementations still measured worse than the crude
  approximation they replaced. Keep whichever measures best, and say plainly that it is not the truest
  model.
- Determinism rule 4 is binding: when inference cannot decide, emit Layer A verbatim and flag
  `needs-review`. Never pattern-guess on document content (this is why "Page N of" is *not* special-cased).

### Environment gotchas

- **`FAP2PDF.EXE` (the legacy oracle) cannot render filled forms** — it takes only `/I=` and `/X=`.
  Filled references would need a full `GENDAW32.EXE` Documaker job (INI + extract data).
- It also needs `FSISYS.INI` **in the working directory** (`sweep.py` copies it in), and under Git Bash
  use `-I=` not `/I=` (MSYS mangles the path).
- `FAP2PDF` renders most composable fragments (`QCPP_*`, `QFRM_*`, `BQ-*`) nearly blank, so **it is not
  a valid oracle for a fragment** — a fragment's real appearance only exists in an assembled packet.
- `sweep.py` needs an **absolute** output path (the work dir becomes a `file://` URI for Chromium).
- `R2021C` and `FP0102A` produce 0-byte legacy PDFs; `FAP2PDF` still exits 0. Cause uninvestigated.
- Stale `dotnet run` processes lock DLLs. Kill the listener on :5035 before rebuilding the server.
- Bash heredocs mangle backslashes — write scripts with the Write tool.

### Tooling

Sweep artefacts are cached in `output/sweep-work/` (FAP, legacy `.PDF`, `.html`, `_ours.pdf` per form),
so the gates can be re-measured **without re-rendering**. Re-run `sweep.py` only after changing
`emit-html`.

```bash
dotnet run --project demo/FapPdfTools.Demo.csproj -- emit-html EB2410A out.html
python tools/sweep.py 24 C:\src\fact-pdf-tools\output   # full pipeline; path MUST be absolute
python tools/contentdiff.py                             # Tier 1 gate (uses cached artefacts)
python tools/tier2.py                                   # Tier 2 gate (3.0pt) + 1.0pt quality column
python tools/parity.py legacy.pdf ours.pdf diff         # IoU + overlay images, diagnostics only
python tools/nontextink.py                              # non-text ink gate (logos + rules)
python tools/dashboard.py                               # ALL gates, per form and stratum
python tools/glyphshape.py                              # glyph-shape diagnostic (wrong-font check)
python tools/glyphshape.py --selftest EB2410A           #   ...prove it reacts to shape
python tools/logdecode.py --scan                        # decode every Documaker .LOG asset
python tools/gdcontent.py                               # .gd content gate (goldens)
python tools/gdcontent.py --dir output/quote-forms-gd   # ...or any dir of .gd files
```

There is no calibration step any more — it was deleted in §18. If you change `emit-html`, re-render
**every form you intend to measure** (not just the sweep sample) and confirm each `_ours.pdf` is newer
than the demo binary before reading any score.

`dotnet run -- regress` is the golden-file suite for the **`.gd`** generator — run it after any change
to the shared FAP parser in `core/`, since Form Studio and GhostDraft share it. Three parser bugs found
via Form Studio (CP1252 decoding, quote stripping, reading-order bands) had silently corrupted the
`.gd` path too; `tools/gdcontent.py` (§20) is the content gate that would now catch a fourth. Note that
`output/quote-forms-gd/` is a **cached artefact** — regenerate with `convert-quotes` before measuring it,
or you will score whatever parser built it last (it was three weeks stale when §20 was written).

### Open threads, roughly by value

1. **Intra-run horizontal drift** (§19, §25) — the last named text defect, and what holds the Tier 2
   quality bar at 167/272. **Five mechanisms have been tried and every one measured worse or a wash.**
   Read §19 and §25 before touching it; anything that adjusts a single number per run is exhausted. The
   only untried approach is per-glyph positioning.
2. **`X,` edge semantics** (§31, §32) — the second parameter group decides whether a record draws a
   rectangle, one rule, two rules, or only the verticals, and it is only partly decoded. `(20,20)`,
   `(15,15)`, `(33,33)`, `(36,36)` are rectangles; `(24,24)` under 430 units is rules 92% of the time;
   **`(25,25)` is genuinely mixed across four outcomes over 83 records.** The `M,PX` rule is 85%
   accurate at 1,000-form scale (§34, up from the 80% in §26). `FapLine.Group` already
   carries the group through the parser. Note §32: a 92% rule here was reverted because being wrong
   drops real ink.
3. **A declarative system-value registry** (total pages, current page, edition, print date) resolved by
   rule, belonging with the binding layer (§3) — not a heuristic. This is the last known *content* gap,
   currently papered over by the fill allowance in the Tier 1 gate.
4. **Packet assembly** — ~31% of the library are fragments, so a converted form is not always a
   deliverable document, and FAP2PDF is not a valid oracle for one.
5. **Image sign-off.** Images render (§28) but **no gate can check them** — FAP2PDF embeds none. Someone
   needs to look at a sample of image-bearing forms.
6. **P3 onward** — the binding layer, editor and runtime from §8 are all still ahead. Everything so far
   is P0/P1 fidelity work.

### Done, so nobody redoes them

- Parser hardening with a content gate on `.gd` output (§20) — `tools/gdcontent.py`, 10/10 goldens and
  301/301 quote forms with content, character for character.
- Images: the `.LOG` format is decoded (§28) — all 76 assets, every reference in the library resolves.
- The baseline mystery (§18), shading (§22), `M,PX` rules and box sizing (§21, §23), `M,I` bullets
  (§24), the DocuDings substitution (§27) and the 0-byte oracle (§27).
