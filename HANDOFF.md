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
`FORM-STUDIO-PLAN.md` end to end — it is the design *and* the running lab notebook, and §10–20 record
what was measured, including the negative results. Sections are append-only; do not rewrite history.

### Where it stands (120-form stratified sweep, 109 scored)

| gate | result |
|---|---|
| **Tier 1 — nothing dropped** (content) | **109/109** ✅ |
| **Tier 2 — ≥90% of glyphs within 3.0pt** (placement) | **106/109** |
| Tier 2 at the 1.0pt quality bar (diagnostic, not a gate) | 66/109 |
| (diagnostic only) stream identical incl. order | 95/109 |

Tier 1 is closed: zero real content defects. **Vertical placement is closed too** — `|dy| > 1pt` is
0.0% of glyphs on every accepted form. Everything still open is horizontal.

The 3.0pt gate is set by the worst accepted form (`A0238C`, 90.6%) and is **not** a claim that 3pt is
good — that form genuinely carries ~3pt of intra-run drift Products accepted by eye. Drive the 1.0pt
column down; do not loosen the gate.

**The baseline mystery is solved (§18).** Documaker anchors the text baseline to the **bottom** of the
declared box (`row2`), not the top — measured at −0.09pt with stdev 0.06 over 17,193 records, holding
for every font id and both element kinds. The old "per-form sign flip" was just box height varying per
form. All calibration is **deleted** (`tools/calibrate.py` and both JSON tables); it was fitted to the
old anchor and would now corrupt the geometry. Do not reintroduce it.

### What is left: intra-run horizontal drift (§19)

`dx` accumulates along a run — `A0238C` goes from −0.08pt at a run's first glyph to −2.70pt by its
fortieth — and is worst on bold and large sizes (Arial-BoldMT 18pt: median −3.16pt). This is the one
remaining named defect.

**Four mechanisms have been tried and all measured worse or a wash. §19 has the numbers — do not
repeat them:** dropping the FXR correction (doubled the error), per-word FXR offsets, multiplicative
correction via point size (a wash), and box-fit `scaleX` (much worse). The shipped additive
`letter-spacing` stays **because it measures best, not because it is the truest model.**

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
- **Ask what a metric is blind to, not just what it reports.** Run-level Tier 2 only checked where each
  run *started*, so it could not see drift inside a run at all — its good scores were meaningless.
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

1. **Intra-run horizontal drift** (§19) — the only named fidelity defect left, and what keeps the Tier 2
   gate at 3.0pt instead of 1.0pt. Read §19's two failed attempts before starting.
2. ~~A parser-hardening pass with the content gate pointed at `.gd` output~~ **DONE (§20)** --
   `tools/gdcontent.py` gates it: 8/8 goldens and 301/301 quote forms with content match the FAP
   character for character, surplus 0. Run it after any `core/` parser change.
3. **P1 fidelity:** images/logos (`G,` → `.LOG`, undecoded; 548 forms) and dense multi-column grids.
   Note the sweep *disproved* the assumption that images were the top blocker — they score mid-pack.
4. **A declarative system-value registry** (total pages, current page, edition, print date) resolved by
   rule, belonging with the binding layer (§3) — not a heuristic.
5. **Packet assembly** — ~31% of the library are fragments, so a converted form is not always a
   deliverable document.
