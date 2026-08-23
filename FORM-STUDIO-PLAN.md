# Form Studio — Plan

**Goal:** one app that converts legacy Documaker (FAP/DDT) forms into a modern, editable, schema-bound
format; lets engineers and business authors edit them (text, columns, variable mapping); renders them to
PDF; and eventually serves as the production render path in place of GhostDraft/Documaker.

**Decisions taken from intake (2026-08-16):**

| Question | Answer | Consequence |
|---|---|---|
| Purpose | Migration **and** runtime engine **and** new-form authoring | One codebase, phased rollout; runtime must be fast and deterministic |
| Fidelity | Converted forms **indistinguishable** from legacy at first, **restyle allowed after** | Two-layer document model + automated pixel-parity gate |
| Users | Engineers **and** business authors, different modes | Two editing surfaces over one document model |
| Data source | CDM today, **"Golden Schema" possible later** | Bindings must be schema-neutral; adapters + mapping profiles, never hard-coded paths |
| Ownership | **Engineering does all conversion.** Products does final sign-off, edits forms, and authors new ones | Explicit review/approval workflow with states; author mode is a first-class product surface, not a nicety |
| Output formats | Multiple — HTML, PDF, GhostDraft `.gd`, extensible | Hub-and-spoke: one canonical model, pluggable emitters |
| Implementation | **Fully programmatic and deterministic** | No AI/heuristic guessing in the conversion path; same input → byte-identical output; ambiguity fails loudly |

---

## 1. Build on `fact-pdf-tools`, don't greenfield

Roughly 60% of the hard substrate already exists in this repo and is proven against real policies:

| Capability | Where | State |
|---|---|---|
| FAP parser (fields, static text, text areas w/ per-word tokens, lines, rectangles, page info) | `core/Infrastructure/FormFileClient.cs` | Working, 2400-DPI integer coords |
| FXR font resolution (exact pt size / bold / italic / face) | `core/Infrastructure/FxrFontLibrary.cs` | Working |
| Absolute FAP → PDF renderer (Spire.PDF, AcroForm fields, box auto-fit fixes) | `core/Infrastructure/FapToPdfGenerator.cs` | Working, several fidelity bugs already solved (see `RENDERING_LEARNINGS.md`) |
| **Semantic inference** — paragraph flow, N-column detection, grid/border detection from FAP `X,` rectangles | `core/Infrastructure/FapToGhostDraftGenerator.cs` (1048 lines) | Working, currently emits RTF/`.gd`. **This is the most valuable asset to retarget.** |
| Editable form model + JSON store | `core/Models/FormDefinition.cs`, `FormDefinitionStore.cs` | Absolute-only |
| Drag/select/edit designer UI | `client/src/pages/DesignPage.tsx` | Absolute-only, engineer-grade |
| Catalog (FORM.DAT), convert, scenarios, policy fetch, template fill | `server/Controllers/*` | Working |
| CDM variable population + per-field formats (money/date/percent) | `population/`, `core/Models/FieldValueFormatter.cs` | Working, 4 specific maps + generic header fallback |
| Batch conversion over the whole library | `demo coverage` | 4462 forms parse without error |

**Plan: extend this repo.** New projects to add: `render` (document model → HTML → PDF) and `binding`
(schema-neutral data binding). Rename the product surface to **Form Studio**; keep the assembly names.

---

## 2. Core architecture — the two-layer document

The central tension: **pixel parity demands absolute positioning; editing text and aligning columns demands
semantic structure.** Trying to pick one loses half the requirement. So the document carries both.

```
FAP + DDT + FXR
      │
      ├─► PARSE ──► FapParseResult  (exact 2400-DPI geometry, per-word tokens, fonts)
      │
      ├─► LAYER A: ABSOLUTE   — every element placed at its exact legacy coordinate.
      │                         Guarantees "indistinguishable". Produced 100% mechanically.
      │
      └─► PROMOTE ──► LAYER B: FLOW  — inferred paragraphs, tables, columns, rules.
                                       Optional, per-region, reversible. Enables real editing
                                       and restyling. Uses the existing inference engine.
```

- A converted form starts **100% Layer A** and is byte-for-byte reproducible from the FAP.
- An author "promotes" a region (a block of text, a column group, a grid) to Layer B when they want to
  edit or restyle it. Promotion is per-region and **reversible** — the absolute origin is retained as
  `data-origin` back-references, so a bad promotion can be reverted without re-importing.
- Forms never touched stay pixel-identical forever. Forms that get restyled do so deliberately, region by
  region, with a visible parity score showing what drifted.

### Canonical format: **MoE Form HTML** (a constrained HTML profile)

HTML is the right canonical format — and it should be the *stored source of truth*, not just a view.
Reasons: it renders to PDF through a first-class engine, it is directly editable by mature editor
libraries, it diffs readably in git, and it carries arbitrary metadata on `data-*` attributes.

The risk with raw HTML is metadata sprawl and unrenderable soup. Mitigate with a **strict, versioned
profile** plus a validator that rejects anything outside it:

```html
<section class="form-page" data-page="1" style="width:612pt;height:792pt">

  <!-- Layer A: absolute, exact legacy geometry -->
  <span class="abs" style="left:72pt;top:96.4pt;font:8pt 'Documaker Sans'">POLICY NUMBER</span>

  <!-- A bound field -->
  <span class="abs field"
        style="left:180pt;top:96.4pt;width:96pt;height:9.4pt"
        data-field="POLICYNUM"
        data-bind="policy.number"
        data-provenance="system"
        data-format="text"
        data-maxlen="12"></span>

  <!-- Layer B: promoted flow region, still traceable to its origin -->
  <table class="flow grid" data-origin="rect:14,22" data-cols="left,right,right">
    <tr><td>Bodily Injury</td><td data-bind="coverage.bi.limit" data-format="money:0">…</td></tr>
  </table>

</section>
```

Rules: page size and all Layer-A coordinates in **points**; CSS `@page { size: 8.5in 11in; margin: 0 }`;
no external resources at render time (fonts and images inlined or served from the template store); a typed
C# model round-trips the document (parse with AngleSharp) so server code never string-manipulates HTML.

### Output formats — hub and spoke

The app is not a FAP→PDF converter with a side door; it is a **hub-and-spoke converter**. One canonical
document, an `IFormEmitter` registry, and any number of output formats — including formats we haven't
thought of yet.

```
                    ┌─► HTML          (canonical + web preview)
                    ├─► PDF           (Chromium; the deliverable)
FAP/DDT ─► Document ├─► GhostDraft .gd (FapToGhostDraftGenerator — already built)
   PDF  ─►  Model   ├─► AcroForm PDF  (Spire; fillable hand-off)
   (new authoring)  ├─► DOCX / RTF    (future)
                    └─► JSON          (machine interchange, diffs, tests)
```

Two consequences worth stating:

- **HTML → GD is now a supported route.** Convert legacy → HTML, edit/restyle, *then* emit `.gd`. This
  matters because it de-risks the GhostDraft question entirely: Form Studio can feed GhostDraft during a
  transition period and replace it later, and the decision doesn't have to be made up front. The existing
  1048-line `.gd` generator becomes an emitter behind the same interface rather than a parallel pipeline.
- **Import is also pluggable.** FAP is the first importer; `import-pdf` already exists; new-form authoring
  is just "start with an empty document." An `IFormImporter` interface keeps that symmetric.

Every emitter is a pure function of the document model plus a versioned emitter ruleset — see §7.

### PDF engine

| Option | Verdict |
|---|---|
| **Headless Chromium (Playwright / PuppeteerSharp)** | **Recommended primary.** Correct CSS paged-media, real font shaping, handles both layers with one code path, embeds fonts. Cost: a browser in the container and a warm process pool. |
| Existing Spire.PDF absolute renderer | **Keep as the parity oracle** during migration and as the fallback for AcroForm output. Verify the Spire license tier covers production volume before depending on it. |
| QuestPDF / pure-.NET | Rejected as primary — it would mean re-implementing layout that Chromium already does correctly. |

**AcroForm caveat:** Chromium does not emit fillable fields. Today's interactive/WIP forms (MCS90A, EB2410)
rely on AcroForm editing in the browser. Under Form Studio the user edits in the app instead and the PDF is
flat — which is simpler and removes a whole class of bugs (the box auto-fit / clipping / MaxLength issues
documented in `RENDERING_LEARNINGS.md`). Keep the Spire path for any consumer that genuinely needs a
fillable PDF handed off externally.

---

## 3. Data binding — surviving the Golden Schema

This is the requirement most likely to be designed wrong, and the cheapest to get right up front.

**Never store a CDM path in a form.** Store a **logical binding path** in a Form Studio namespace, and
resolve it through an adapter:

```
Form HTML:      data-bind="policy.number"
                              │
Binding catalog │  logical path → type, label, sample value, format hint
                              │
IFormDataSource ├─► CdmDataSource          policy.number → CDMPolicyView.Policy.Number
                └─► GoldenSchemaDataSource policy.number → <golden path>
```

- `IFormDataSource` resolves a logical path against a runtime payload; one implementation per schema.
- A **mapping profile** (one versioned file per schema) maps logical paths → concrete schema paths.
- Golden Schema migration then = write one adapter + one mapping profile + remap the few hundred distinct
  logical paths. **Zero edits to 4,500 forms.** Unmapped paths are reported as a gap list, not a crash.
- Seed the logical namespace from the DDT provenance work already built (`demo gap`): every field is
  `system` (DAL/table computed), `manual` (WIP entry), or `constant` (mk_hard). Only `system` fields need
  bindings; `manual` becomes author-entered; `constant` becomes literal text.
- **DAL-sourced fields are the hard tail.** Many legacy values come from DAL script computation, not a
  column. Classify them: some resolve to a plain path via `form_resolve_dal`; the rest need a named
  server-side expression owned by engineers. Do not let authors write logic.

---

## 4. The editor

One document model, two modes.

**Shared**
- Page canvas with real page bounds, zoom, rulers, multi-page navigation
- Live PDF preview + **rasterized overlay diff against the legacy PDF** (parity score always visible)
- Sample-data scenarios (extend the existing `ScenarioStore`) and "load from policy #" (already built)
- Undo/redo, per-form version history, diff against the last saved version

**Engineer mode**
- Layer A geometry: exact X/Y/W/H in points, snap-to-grid, align/distribute, multi-select
- **Column alignment tools:** select N elements → align left/right/center edges, equalize spacing, or
  "convert to table" which promotes them into a Layer B `<table>` with real column widths
- Field inspector: name, logical binding (searchable catalog picker), provenance, format
  (`money`/`date`/`percent` — `FieldValueFormatter` exists), max length, overflow behavior
- Raw HTML source view, JSON export, DAL/expression assignment, promotion/revert controls

**Author mode**
- WYSIWYG rich-text editing on promoted (Layer B) regions only — TipTap/ProseMirror over the HTML profile
- Table editor for columns: add/remove column, set width, set per-column alignment
- Variable insertion from a friendly catalog ("Insured name", not `policy.insured.name1`)
- Guardrails: filed/ISO forms have locked regions; promotion of a locked region requires engineer approval;
  a compliance banner when a form's parity score drops below threshold

---

## 5. Fidelity: how "indistinguishable" gets proven

Manual eyeballing does not scale to 4,462 forms. Build the parity harness **in Phase 0**, before bulk
conversion — it is the acceptance mechanism for the entire migration.

**We can generate the reference renders ourselves — no dependency on archived output.** The real Documaker
engine and its resources are on disk locally:

| Asset | Path |
|---|---|
| Documaker generator | `C:\src\FaCT-DocProd-Development\Dll\GENDAW32.EXE` (+ a `DLL_Debug` build with symbols) |
| Commercial resource set | `…\mstrres\MOEC0\` (FSISYS/FSIUSER/AFP INIs); agency set at `…\mstrres\AGCYLNK\` |
| FXR font cross-reference | `…\mstrres\AGCYLNK\DEFLIB\REL103.FXR` |
| **The actual TrueType faces Documaker renders with** | `…\mstrres\Fmres\deflib\` — Arial, Albany, Courier families (`arial.ttf`, `alb*.ttf`, `COURIE*.TTF`, …) |
| Font family definitions | `…\Dll\Fonts.ini` |
| Existing PDF comparison tooling | `C:\src\FaCT-DocProd-Tools\PDFCompareTools\` |

So the harness is a closed loop we control end to end:

1. Render the legacy reference by driving **GENDAW32** over the form's FAP with the MOEC0 resource set.
2. Render the Form Studio HTML → PDF.
3. Rasterize both at 170–300 DPI (**PyMuPDF/`fitz` is already installed and used for this**) and diff
   per-pixel; emit a score, a diff image, and the worst-offending regions.
4. Gate: score below threshold → the form is flagged, not shipped. Publish a **fidelity dashboard** across
   the whole library so conversion progress and blockers are visible.

**Known parity blockers to plan for:**
- **Fonts — largely de-risked.** The real faces are already on disk (`mstrres/Fmres/deflib/`) and are
  standard, embeddable families (Arial, Albany — the metric-compatible Arial clone — and Courier), not
  exotic licensed faces. Work reduces to: convert TTF → woff2, build the FXR `FontId` → face/size/weight
  map (the `FxrFontLibrary` already resolves this), embed via `@font-face`, and verify rendered advance
  widths against the FXR's per-character advances. Confirm redistribution rights for the bundled faces
  before shipping them in a container.
- **Images/logos.** `G,` records point at Documaker `.LOG` files (ASCII header + hex pixels), still
  undecoded. 548 forms carry images; 70 of 107 quote forms do. Options: decode `.LOG`, reuse GhostDraft's
  branding assets (`Logo.gd` extracts cleanly), or capture from a legacy render. Blocks quote-cover parity.
- **Multi-page and dense-grid forms.** 1045 forms are multi-page; the table builder is single-page today.
- **Spire's flatten auto-fit behavior** — already solved for the Spire path; verify it does not recur under
  Chromium (it should not; Chromium honors specified font sizes).

---

## 6. Ownership and the sign-off workflow

Engineering owns conversion; Products owns approval, editing, and new forms. That maps to an explicit
state machine on every form, with permissions attached — not an informal handoff.

```
  IMPORTED ──► CONVERTED ──► IN REVIEW ──► APPROVED ──► PUBLISHED
   (auto)      (engineer)    (engineer     (Products)   (engineer)
                             submits)          │
                                               └──► CHANGES REQUESTED ──┐
                                                                        │
                                        (Products edits) ◄──────────────┘
```

- **Engineering** runs bulk conversion, resolves parity failures, owns bindings, DAL expressions, promotion
  of regions to Layer B, and publishing to the runtime template store.
- **Products** reviews the parity diff, edits copy and layout in author mode, authors new forms, and gives
  the final approval. An approval is recorded against a specific document version hash.
- **Any edit after approval returns the form to IN REVIEW.** Publishing requires an APPROVED state, so a
  restyle can never reach production unreviewed.
- Every state transition is logged with actor, timestamp, version hash, and the parity score at that moment
  — which is also the audit trail for filed forms.

## 7. Determinism contract

The conversion path must be fully programmatic and reproducible. This is a hard constraint on the design,
not an aspiration, so it gets stated as testable rules:

1. **No AI, no ML, no randomized heuristics anywhere in conversion, binding, or emission.** Every rule is
   declarative code with a documented basis in the FAP/DDT/FXR structure.
2. **Same input → byte-identical output.** No timestamps, GUIDs, hostnames, paths, or hash-ordered
   collections in emitted artifacts. IDs are derived deterministically from stable source facts (the
   existing `ProjectConcepts` deterministic-GUID approach is the pattern to follow). Any unavoidable
   volatile field goes in a sidecar manifest, never in the document.
3. **Ordered everywhere.** Sort every collection by an explicit key before emission; never rely on
   dictionary or filesystem enumeration order.
4. **Ambiguity fails loudly.** When inference can't decide (is this a table or two paragraphs?), the
   converter does *not* guess — it emits Layer A verbatim and records a `needs-review` marker for an
   engineer. A wrong silent guess on a filed form is far worse than a flagged one.
5. **Versioned rulesets.** Converter version, inference-ruleset version, emitter version, font-set version,
   and renderer version are recorded in a per-form manifest. Re-running an old version reproduces the old
   output exactly; a ruleset change shows up as an intentional, reviewable diff across the library.
6. **Golden-file regression on every emitter.** Extend the existing `demo regress` harness (which already
   normalizes volatile timestamps and exits non-zero on change) to cover the HTML and PDF emitters, and run
   it in CI.
7. **Pure functions.** Importers and emitters take a model and a ruleset and return bytes. No I/O, no
   ambient config, no clock, no network inside them.

8. **Normalize the PDF trailer.** Chromium's PDF output is pixel-identical run to run but not
   byte-identical: `/CreationDate` and `/ModDate` are the *only* differing bytes (measured, §10). Strip or
   fix them post-render to get byte-level reproducibility.

**One honest caveat:** the final rasterization step is Chromium's, and font rasterization can shift between
browser versions. Pin the Chromium build and the font set, record both in the manifest, and treat a
renderer upgrade as a library-wide re-baseline gated by the parity harness. Everything upstream of
rasterization — model, HTML, `.gd`, JSON — is fully deterministic and diffable.

## 8. Phasing

Estimates are rough and assume a small team; treat sequencing as firmer than duration.

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **P0 — Foundation** (2–3 wks) | Document model + MoE Form HTML profile v1 + validator; `IFormImporter`/`IFormEmitter` registry (FAP in; HTML + PDF out; existing `.gd` generator re-homed behind the interface); Chromium render service; **GENDAW32-driven parity harness** + dashboard skeleton; determinism rules enforced in CI | 10 pilot forms (1 prose, 1 columnar, 1 grid, 1 multi-page, 1 with images, 1 dec page, 4 assorted) convert and score ≥ threshold, or their blockers are named; two consecutive conversion runs produce byte-identical output |
| **P1 — Font & image fidelity** (3–4 wks) | Font strategy resolved and embedded; `.LOG` decode or asset substitution | Pilot set at parity with no font/image caveats |
| **P2 — Bulk conversion** (3–4 wks) | All 4462 forms converted; fidelity dashboard live; triage list by family | ≥90% of forms at parity; remainder categorized with named causes |
| **P3 — Binding layer** (3–4 wks) | `IFormDataSource`, binding catalog, CDM adapter + mapping profile, DDT provenance import | A real policy renders a converted form with correct variable data end-to-end |
| **P4 — Editor v1, engineer mode** (4–6 wks) | Promotion/revert, geometry + alignment tools, field inspector, source view, versioning | An engineer can restyle a form's columns and re-render without regressing untouched regions |
| **P5 — Editor v2, author mode + sign-off** (5 wks) | WYSIWYG on flow regions, table/column editing, friendly variable picker, guardrails, scenarios; the §6 state machine, permissions, parity-diff review screen, and approval audit trail | A Products reviewer approves a converted form and edits another one unassisted; approval is recorded against a version hash |
| **P6 — Runtime** (4–6 wks) | Template store keyed form+edition, warm Chromium pool, render API, DocGen integration via the existing `FormSystem.PdfTools` seam, packet assembly | A form family renders in the live pipeline behind a flag, with parity gate green |
| **P7 — Authoring new forms** (3 wks) | Blank-page authoring, form templates, publish workflow | A net-new form ships without Documaker Studio |

**Hard rule carried forward from the POC:** no FAP parsing at request time. Conversion is offline; runtime
loads a stored template and binds data.

---

## 9. Risks and open questions

| Risk | Impact | Mitigation |
|---|---|---|
| ~~Documaker font faces unavailable~~ — **resolved**, the TTFs are on disk | — | Confirm redistribution rights only |
| `.LOG` image decode fails | Quote covers and 548 forms can't reach parity | Substitute GhostDraft branding assets; escalate the photographic banner to marketing |
| Chromium in the deployment environment | Blocks runtime phase | Container-based (the repo already has an AKS path via `fact-acr-to-aks`); validate early with a spike |
| Spire.PDF license tier | Legal/production risk on the fallback path | Confirm entitlement before P2 |
| Filed/ISO forms require regulatory sign-off on any visual change | Restyle could create a compliance problem | Locked regions + parity gate + the §6 approval state machine (Products owns final sign-off) |
| Chromium version drift changes rasterization | Silent parity regressions | Pin build + fonts in the manifest; renderer upgrade = gated library-wide re-baseline (§7) |
| Inference "guesses" wrong on a filed form | Wrong document, silently | Determinism rule 4: ambiguity emits Layer A verbatim + `needs-review`, never a guess |
| Golden Schema arrives mid-build | Rework | The adapter/profile design is precisely the insurance; keep zero CDM types in the document model |
| DAL-computed fields | A long tail that isn't "data" | Classify in P3; engineer-owned named expressions, never author-authored logic |
| Scope — this is four products (converter, editor, binder, renderer) | Timeline | Phase gates above; each phase is independently useful |

**Resolved:** reference renders come from our own local GENDAW32 (§5). Fonts are on disk (§5). Products
owns compliance sign-off; engineering owns conversion (§6). GhostDraft no longer needs an up-front
decision — it is one emitter among several (§2), so Form Studio can feed it during a transition and
replace it later.

**Open — to measure, not to ask:** runtime volume and per-form latency budget, which size the Chromium
pool. Derive these from the existing DocGen/DocProd batch statistics (documents per batch, batch window)
rather than from an opinion. Task for P6.

---

## 10. P0 spike — executed 2026-08-16

**Form:** `EB2410A` (2 pages, 304 text runs, 9 rules/boxes, 6 fields, bordered schedule table).
**Verdict: the approach works. Page geometry matched exactly on the first attempt, and every residual
traced to a specific, fixable cause — no unknowns.**

### What was built and run

| Step | Tool | Result |
|---|---|---|
| Legacy reference render | **`FAP2PDF.EXE`** (Documaker's own FAP→PDF, in `…\Dll\`) — better than driving GENDAW32: it renders a single FAP with no extract data or job setup | `EB2410A.PDF`, 2 pages |
| FAP → MoE Form HTML | new `demo emit-html <FORM> [out]`, reusing `FormFileClient` + `FxrFontLibrary`; embeds the Documaker TTFs as base64 `@font-face` | 2.9 MB HTML |
| HTML → PDF | headless Chrome `--print-to-pdf` | 154 KB PDF |
| Parity scoring | new `parity.py` — PyMuPDF raster at 150 DPI, binarized ink IoU, 1px-tolerance unmatched %, best whole-page shift, overlay/unmatched PNGs | see below |

### Measured progression

| | mean IoU | legacy-unmatched (p1, 1px tol) | systematic shift |
|---|---|---|---|
| First run | 0.261 | 27.75% | **dy=3px, dx=0 (both pages)** |
| + baseline correction | 0.491 | 8.07% | dy=0, dx=0 |
| + encoding & font fixes | **0.528** | **5.18%** | dy=0, dx=0 |

Page size matched exactly (612×792 pt) from the first run, on both pages.

### Root causes found (all three fixed or named)

1. **Baseline placement — fixed empirically, needs a principled fix.** Our text sat a constant 1.44pt
   high on every page. Chromium's half-leading places the baseline differently from Documaker. A measured
   `translateY(1.44pt)` removed the offset entirely (best-shift went to 0,0). **P0 work:** replace the
   constant with exact ascent/descent parsed from the TTF (`hhea`/`OS/2`) so the baseline is computed, not
   calibrated.
2. **FAP files are Windows-1252, not UTF-8 — fixed.** Bytes `0x93`/`0x94` (curly quotes) and `0x96` (en
   dash) were decoding to U+FFFD and rendering as `◆` throughout the body text. Fixed in
   `FormFileClient.ReadLinesAsync` with a dependency-free CP1252 decode. **This was a real pre-existing bug
   in the parser**, affecting the `.gd` path too, not just this spike.
3. **The legacy PDF channel substitutes base-14 Helvetica — important, unresolved.** `FAP2PDF` embeds
   `Helvetica`/`Helvetica-Bold` (Type1, WinAnsiEncoding) rather than the Documaker TTFs. Arial is
   metric-compatible with Helvetica, which is exactly why Arial body text matched well and `Univers ATT`
   headings drifted. Remapping Univers → Arial improved the score. **Two consequences:**
   - *Which legacy channel we match is an explicit parity setting.* The PDF channel substitutes fonts; a
     print/AFP channel would use the real faces. Pick and record it per comparison.
   - *The remaining residue is concentrated in bold runs* as accumulating horizontal drift within a token
     — an advance-width mismatch. The deterministic fix is to treat the **FXR width table as the
     authority** (`FxrFont.MeasureFap` already exposes it) and correct per-run advances, rather than
     trusting whichever face gets substituted. This confirms the §5 "verify advance widths against the
     FXR" item is required work, not a nicety.

### Determinism — verified

- `emit-html` run twice → **byte-identical HTML** (`md5` match).
- Same HTML rendered twice by Chromium → **pixel-identical** (`maxdiff = 0` on both pages), identical file
  size, differing **only** in `/CreationDate` and `/ModDate`. Hence determinism rule 8.

### What this does and does not prove

Proven: FAP → HTML → Chromium PDF reproduces legacy page geometry, text placement, rules, and bordered
tables; we can generate our own legacy references locally; the parity harness works and is diagnostic
(it localized every defect); output is deterministic.

Not yet proven: forms with images (`G,` records), dense multi-column grids, multi-page flow forms, filled
(variable-data) renders, and whether the residual bold drift fully clears once FXR-width correction lands.
`EB2410A` is a text-and-table form — a deliberately favourable but representative starting case.

### Reproduce

```bash
dotnet run --project demo/FapPdfTools.Demo.csproj -- emit-html EB2410A out.html
FAP2PDF.EXE -I=EB2410A.FAP -X=REL103.FXR          # note: -I not /I under Git Bash (MSYS path mangling)
chrome.exe --headless --disable-gpu --no-pdf-header-footer --print-to-pdf=ours.pdf file:///out.html
python parity.py EB2410A.PDF ours.pdf diff
```

---

## 11. Fidelity sweep — 50 forms, 2026-08-16

`tools/sweep.py` batches the whole pipeline (FAP2PDF → `emit-html` → Chromium → parity) across a
deterministic stratified sample drawn from the existing coverage report, and writes
`output/fidelity-sweep.{csv,md}`. This is the P0 fidelity-dashboard skeleton.

**Result: 48 of 50 scored, mean page-1 IoU 0.286. Every page count matched — including a 15-page form.**
The low mean is explained almost entirely by two mechanical defects, not by content loss.

| stratum | n | mean IoU | median legacy-unmatched |
|---|---:|---:|---:|
| multipage | 9 | 0.431 | 3.1% |
| prose | 9 | 0.297 | 37.0% |
| images | 10 | 0.266 | 44.6% |
| grid | 10 | 0.252 | 51.8% |
| fields | 10 | 0.212 | 46.5% |

### Defect 1 — residual baseline offset (25 of 48 forms)

The `max_shift` column splits the sample almost perfectly:

| | n | mean IoU | median legacy-unmatched |
|---|---:|---:|---:|
| shift = 0 | 18 | **0.497** | 6.8% |
| shift ≥ 2 | 29 | **0.157** | 55.4% |

Re-scoring each form after applying its own detected shift lifts **mean IoU 0.286 → 0.434**, with 25 forms
improving by >0.05 and several by >0.5 (`PSBP-QPREMDT2A-A` +0.61, `CPQ2-GL2` +0.58). This confirms the §10
finding at scale: the hard-coded 1.44pt correction is right only for the font sizes in EB2410A. Half-leading
is proportional to line-height, so **no single constant can be correct** — the baseline must be computed
from the TTF `hhea`/`OS/2` ascent. This is the single highest-value fix in the project.

### Defect 2 — fragment forms get a sliver page (15 of 48 forms, 31%)

15 forms declare a partial page height in their FAP `H,` record and we honour it literally, producing pages
like **612×14pt** where the legacy renders 612×792. Every one of them scores 0.02–0.17; they *are* the
bottom of the table. These are composable fragments (`QCPP_*`, `QFRM_*`, `BQ-*`, headers/footers/totals)
that Documaker assembles onto a page.

Fix: render a fragment onto a full page box rather than its own bounding height. **Wider consequence:** at
~31% of the library, forms are not 1:1 with pages, which directly promotes the "packet assembly" item from
§5 — a converted fragment is not a deliverable document on its own.

### Defect 3 — bold advance drift

Unchanged from §10 and still outstanding: treat the FXR width table as authoritative via
`FxrFont.MeasureFap` rather than trusting the substituted face's advances.

### Also found

- `R2021C` and `FP0102A` produced **empty** legacy PDFs — FAP2PDF exits 0 but writes a 0-byte file. Cause
  not yet investigated; the harness now reports them rather than crashing.
- `sweep.py` requires an **absolute** output path (the work dir becomes a `file://` URI for Chromium).

### Revised risk ranking

The plan assumed images were the top fidelity blocker. **They are not** — the images stratum (0.266) scores
mid-pack and its low scores are mostly defect 1 and 2, not missing artwork. Ranking by measured cost:
baseline offset → fragment page size → bold advances → images. Images remain a real gap for quote covers
but should not lead the work.

```bash
python tools/sweep.py 10 C:\src\fact-pdf-tools\output    # 10 per stratum; path must be absolute
```

---

## 12. Defect fixes — one confirmed, one NEGATIVE result (2026-08-16)

### Defect 2 (fragment page box) — FIXED, confirmed

`emit-html` now composes a fragment onto the printer page instead of honouring its
declared section extent (landscape preserved). **Page-size mismatches went 15/48 → 0/48.**

Caveat worth knowing: this did **not** move those forms' scores, because FAP2PDF renders
most fragments nearly blank. Three references carry <1500 ink pixels (`QCPP_GL3_A` = 112px,
`EF1001A_SCHF_DTL` = 134px, `QBOP_SE07` = 684px) and cannot be meaningfully compared at all.
**A fragment's true appearance only exists inside an assembled packet, so FAP2PDF is not a
valid oracle for it.** The harness should exclude near-blank references from scoring rather
than reporting them as failures.

### Defect 1 (baseline offset) — NOT FIXED. Three approaches, all a wash.

| approach | result |
|---|---|
| Fixed 1.44pt constant (from the EB2410A spike) | mean IoU **0.292** |
| Baseline solved from TTF OS/2 ascent + zero half-leading | **worse** — EB2410A fell 0.528 → 0.177 |
| Documaker line box + measured per-FontId correction | **0.288–0.301** depending on gating |

Net movement across four full sweeps: **none**. Each variant improved some strata and
regressed others (prose/multipage up, fields/images down) — the signature of correcting the
wrong axis.

What was learned, so nobody repeats it:

- **Chromium's in-box baseline placement does not follow the OS/2 `usWinAscent`/`usWinDescent`
  metrics.** Measured k/size ≈ 0.83–0.85 em where OS/2 predicts 0.905. Deriving the baseline
  from font tables is therefore not viable; it must be measured.
- **Per-FontId correction is structurally insufficient for at least one font.** Font 16010 —
  the most common body font, 933 samples — has a **bimodal** delta distribution (60% core).
  A single scalar cannot describe it, and applying its median regressed `fields` 0.212 → 0.124.
  It is now left uncorrected (`MIN_SHARE = 0.90` in `tools/calibrate.py`).
- The corrections that *are* stable are physically sensible and agree with the spike: font
  14010 → **1.50pt** vs the 1.44pt constant measured by hand on EB2410A.

### The real conclusion: we have been optimising the wrong axis

Perfectly aligning every page vertically (measured by re-scoring at each form's own detected
shift) only reaches **mean IoU ≈ 0.44**. So even with the baseline solved, **~56% of the
mismatch remains** — it is not vertical.

The evidence points at **defect 3, horizontal advance widths**, which has never been addressed:
non-zero `dx` on many forms, and the accumulating intra-run drift in bold text documented back
in §10. At 8–12pt, an advance mismatch destroys glyph overlap exactly as effectively as a
vertical offset does, and it compounds along a run rather than staying constant.

**Next action: implement defect 3 before any further baseline work** — treat the FXR width
table as authoritative (`FxrFont.MeasureFap`) and correct per-run advances, most likely by
emitting per-run `letter-spacing` or explicit per-token positioning. Then re-measure; the
baseline correction should be revisited only once horizontal error is removed, because the
two are currently confounded in every score.

### Harness fix

`parity.best_shift` searched ±3px and silently **clamped** — a 6.7px error reported as
"dy=3", understating it by half and sending the first fix in the wrong direction. Default is
now ±10. Any future metric with a bounded search must report saturation.

---

## 13. Defect 3 fixed, and the metric was wrong (2026-08-16)

### Defect 3 (advance widths) — FIXED

`emit-html` reads real glyph advances from the TTF (cmap fmt 4 + hmtx) and distributes the
difference against the FXR width as per-run `letter-spacing`. **First change to improve every
stratum** (mean IoU 0.292 → 0.318; prose 0.356 → 0.429, its unmatched ink 29.8% → 10.8%).

### Ink IoU is the wrong acceptance metric — stop using it as the gate

`A0238C` is **visually indistinguishable** from the legacy render side by side, and scores
**0.247**. Binarized ink IoU at 150 DPI punishes sub-pixel offsets on every glyph edge, so it
cannot separate "slightly offset but perfect" from "wrong".

Replace it with a **text-run placement metric**: extract every text run from both PDFs and
match on (page, text, x, y). It is semantic, human-meaningful ("is every string present, in the
right place"), and far more diagnostic. Measured across the 50-form sweep:

| | |
|---|---|
| Forms placing **88–100%** of runs within 1pt | ~40% — median offset **+0.07, −0.20pt**, i.e. essentially exact |
| Forms placing **0.0%** within 1pt | ~60% |

**The distribution is bimodal, not a gradient.** The second group is not "degraded" — it is
uniformly offset by more than 1pt across the whole form. That is a solvable constant, not
accumulated error, and it lines up with the font ids the calibration had to skip (notably
16010, the most common body font, which measured bimodal at 60% core).

**Next action:** resolve the per-form constant offset for the 0% group — most likely by keying
the correction on something finer than FontId, since 16010's bimodality says FontId alone does
not determine the offset. Expect a large fraction of the library to jump to ~95%+ once it lands.
Adopt text-run match % as the P0 exit gate and set the threshold from a form Products accepts
by eye; keep IoU only as a coarse smoke signal.

---

## 14. Tier 1 content gate — built, and it works (2026-08-16)

`tools/contentdiff.py` compares the text of both renders in reading order (whitespace runs
collapsed to one space, which still exposes a *missing* space). It is the Tier 1 gate: a
dropped space or glyph can change what a filed form states, whereas a 1pt offset cannot.

**Result across the 50-form sweep: 21/48 forms are 100% content-identical, 7 at 99–100%.**

### Harness bug found and fixed first

Ordering words by a rounded baseline reported phantom moves (`insert '2. b.'` +
`delete '2. b.'` — the same text, resequenced), because the two renders differ by fractions
of a point and words on one visual line sorted differently. Words are now clustered into
baseline bands (`LINE_TOL = 2.5pt`) and ordered left-to-right within the band. Several
"worst" forms were this artefact, not real defects. **Any reading-order comparison needs a
band, not a rounded coordinate.**

### Real defect classes, in priority order

1. **Missing inter-token space** — `1983, 1987` → `1983,1987`, `DISCLOSURE --` →
   `DISCLOSURE--`, `following is` → `followingis`. The most common class and the one a
   reviewer noticed by eye.
   **Ruled out as causes** (both tested, neither changed the result): the advance-width
   correction (disabled entirely → identical output) and dropped whitespace-only tokens
   (now emitted → identical output). So it is the *gap between adjacent positioned tokens*
   being narrow enough to merge. Next step is to separate the two sub-cases by measuring the
   actual gap: extraction-only (harmless, PyMuPDF's space heuristic) versus visually merged
   (real, and confirmed present on EB2410A). Do not assume they are the same bug.
2. **Dropped quote glyphs** — `"fungi"` → `fungi`, `"MORTGAGEE"` → `MORTGAGEE`, `"you"` →
   `you`. The quote characters vanish entirely while their space is still consumed. A real
   content defect, cause not yet identified.
3. **Dropped underscore rules** — `M7901A` legacy draws `______` fill-in lines we omit.

### Metric policy (supersedes the IoU gate)

| tier | measure | role |
|---|---|---|
| 1 | content diff (`contentdiff.py`) | **gate — must be 100%** |
| 2 | text-run placement within tolerance | **gate — threshold set from forms Products accepted** |
| 3 | ink IoU + side-by-side/overlay images | smoke signal and human review only, never pass/fail |

Products reviewed EB2410A, A0238C, EB22489Q and P0010G on 2026-08-16 and called them
"pretty much spot on", flagging only missing spaces. A0238C scores IoU **0.247**. That single
data point is why IoU is disqualified as a gate and why Tier 1 leads.

### Missing-space class — measured, and mostly a FALSE POSITIVE

Probing the actual glyph boxes settles it:

| form | legacy | ours | verdict |
|---|---|---|---|
| `P1060A` `1983, 1987` | `1983,` ends x=432.66, `1987` starts x=432.00 — legacy's own runs **overlap by 0.66pt** | single run 403.68→456.39 vs legacy 403.60→457.30 | **positions match within 1pt — extraction artefact, not a visual defect** |
| `IM20754O` `DISCLOSURE --` | `--` at x=251.96 | `--` ≈3pt left of legacy | **real, but a small placement error** |

So the class is two different things wearing one symptom. Documaker emits each token as a
separate text-showing operation, so extraction splits them even where the boxes touch; we emit
one absolutely-positioned span per token, and PyMuPDF merges neighbours whose boxes abut. In
`P1060A` the ink is in the right place and only the *extracted text* differs.

**Consequence for the gate:** Tier 1 must not fail a form for a merge that is positionally
correct. Before flagging a missing space, compare the merged run's start/end against the
legacy pair; if both ends agree within tolerance, it is an extraction artefact and passes.
Without that rule the content gate will reject renders Products has already accepted — which
is precisely the failure mode that disqualified IoU.

The genuinely visual merge the reviewer saw on EB2410A is a *different* instance (bold runs,
Univers→Arial), and is covered by the placement error above rather than by this artefact.

### Tier 1 refined twice — and both refinements were the same lesson

**(a) Positional artefact rule.** A spacing-only difference whose merged run starts and ends
within `POS_TOL = 1.5pt` of the legacy pair is an extraction artefact and passes.
**Word-level match: 21/48 → 31/44 forms at 100%**, 9 more at 99–100%. 41 words reclassified.

**(b) Character-stream equality is the actual gate.** Word-level diffing still mis-attributed
characters across token boundaries: `G2425B` reported `"fungi"` → `fungi` as a dropped glyph,
but the emitted HTML plainly contains `out of a "fungi or bacteria incident".` — the quote was
emitted, just grouped into a neighbouring word by extraction. So the headline test is now the
page's whole character stream with all whitespace removed. It cannot be fooled by tokenisation;
the word-level detail is kept only for locating a defect once the gate fails.

That is twice in one session that a metric reported a defect the render did not have. Both
times the cause was the comparison imposing its own tokenisation on two PDFs that legitimately
structure text differently. **Any future gate must be validated against a form Products has
accepted before it is trusted** — otherwise it manufactures work.

### What genuinely remains

The character gate leaves a small, real residue — differences of 2–60 characters on a handful of
forms (`A0238D` 983 vs 981, `IM20754O` 2254 vs 2250, `EP9901D` 40628 vs 40568). These are actual
dropped characters and worth chasing individually; the count is small enough to diff by hand.
Note `A0238D` fails by 2 characters yet was accepted by eye — so **Tier 1 at 100% is stricter
than Products' bar**, which is the right direction for a filed-forms gate but means the residue
is a quality backlog, not a release blocker.

### Parser bug #3 — enclosing-quote stripping (FIXED)

`ParseMTTLine` stripped a leading+trailing `"` from every M,TT token, treating them as a
quoting delimiter. The FAP stores that text **bare** (`M,TT,(...),(...),10,"Personal `), so a
token that both begins and ends with a quote is a **defined term whose quotes are content** —
`"fungi"`, `"we"`, `"you"`, `"wrongful acts"`. In a filed insurance form those quotes are
legally meaningful.

Confirmed by counting: `G2425B`'s FAP contains 34 quotes, the legacy render shows 34, our HTML
emitted 24 — and our PDF also had exactly 24, proving the loss happened at **parse**, not render.

| gate | before | after |
|---|---|---|
| Nothing dropped (order-insensitive) | 34/44 | **43/44** |
| Character-identical (full Tier 1) | 33/44 | **40/44** |

The 3 non-identical forms that remain (`EB9973A`, `EP9901D`, `P0010G`) have **identical
character counts** — nothing is dropped, the two renders merely linearise a multi-column page
differently. That is the comparison's tokenisation again, not a render defect. The golden `.gd`
suite stayed green (8 OK), so the fix is safe for the existing GhostDraft path.

**This is the third bug found in the shared FAP parser** (CP1252 decoding, reading-order bands,
quote stripping). All three silently corrupted text in the existing `.gd` conversion path too,
not just the new HTML one — worth a dedicated parser-hardening pass with the content gate
pointed at the `.gd` output.

### Sole remaining real defect: `M7901A` drops 452 `_` characters

Legacy draws long `______` fill-in rules; we emit nothing for them. One form in the sample,
one character class, and the only outstanding entry on the Tier 1 backlog.

### `M7901A`'s 452 underscores are NOT dropped content — they are field fill

The FAP contains only two `___` runs in its text. The rest come from Documaker rendering each
**unfilled field as underscore fill**: the form has 17 `F,` records whose declared lengths sum
to 370 characters, plus the `From ____` / `To ____` labels. We deliberately emit fields as
invisible metadata spans, so we draw nothing.

**This exposes a structural flaw in comparing BLANK forms.** A blank legacy render contains
artefacts — underscore fill, and any other placeholder chrome — that a *filled* production
document never shows. Chasing them is chasing the wrong target.

Two consequences for the harness:

1. The Tier 1 content gate should **exclude field regions**, comparing only static content, OR
2. Better: compare **filled** renders once variable-data population is wired in (§3), since a
   populated document is the real deliverable. `populate-from-model` already exists.

Until then, treat field-fill differences as expected and out of scope rather than as defects.
With that exclusion the sample has **zero** outstanding real content defects: 43/44 forms drop
nothing, the 1 exception is field fill, and the 3 non-identical forms are multi-column
reading-order artefacts with identical character counts.

---

## 15. Tier 2 gate calibrated from Products' own acceptances (2026-08-16)

Threshold derived, not guessed — measured against the four forms Products accepted by eye:

| tolerance | worst accepted form |
|---|---|
| 1.0pt | 0.0% |
| 1.5pt | 23.2% |
| **2.0pt** | **90.0%** |
| 3.0pt | 91.0% |

**Gate: ≥ 90% of legacy text runs matched within 2.0pt** (`tools/tier2.py`). A gate must never
fail work already accepted, so the worst accepted form defines the floor.

**Read the cliff honestly.** Three of the four accepted forms jump from ~25% at 1.5pt to ~95%
at 2.0pt. That is not a natural distribution — it is a **systematic ~1.5–2.0pt offset the
tolerance is absorbing**, almost certainly the font-16010 baseline residual that calibration had
to skip as bimodal (§12). So 2.0pt is a *workaround tolerance*, not a quality statement. Fix that
offset and the cliff moves; the gate should then be tightened toward 1.0pt. Record the tolerance
alongside the result so nobody later reads 2.0pt as the standard we aimed for.

`EB2410A` behaves differently — 89% at 1.0pt and flat across tolerances — so its residual 11% is
the bold-run width defect (§10), a separate cause.

### Result

| population | pass |
|---|---|
| All scored forms | 25/44 |
| Forms with ≥25 text runs (a meaningful sample) | **23/31 = 74%** |

Of the 19 failures, **11 score exactly 0.0%** and are overwhelmingly the composable fragments
(`FRMQ-TTL`, `PSCP-LBZ`, `QCPP_*`, `BQ-*`, `QBOP_*`, `QFRM_*`) and image-heavy quote pages — 7 of
them have fewer than 10 text runs, which is too little to judge. A uniform 0.0% means the whole
fragment is offset by more than 2pt: **Documaker composes a fragment onto the page at an origin
we do not reproduce.** That is one bug, not eleven, and it is the same finding as §12 — FAP2PDF
is not a valid oracle for fragments, and packet assembly is where they get their real position.

**Highest-value next fixes, in order:** (1) the fragment composition origin, which unlocks ~11
forms at once; (2) the font-16010 baseline offset, which lets the tolerance tighten from 2.0pt.

### CORRECTION to §15: the fragment offset is NOT the baseline residual

§15 asserted that the Tier 2 fragment failures and the font-16010 baseline residual were "the
same bug". **That was wrong** — asserted from a font-id correlation without measuring. Measuring
it shows two separate causes:

**1. Font 16010 needs no correction at all.** Histogramming its 983 deltas shows the "bimodal
60% core" is nothing but **pixel quantization**: the two modes sit at 0.0pt and 0.5pt, adjacent
bins produced by Chromium snapping baselines to device pixels. The true value is ~0.25pt.
Calibration correctly emits nothing for it, and the earlier −1.6 / −3.85 corrections were noise
being fitted. Splitting the key by element kind (`S,TT` vs `M,TT`) was also tested and made no
difference, so that hypothesis is dead too.

**2. The fragment offset is page composition.** Fragments show a clean vertical offset of
−2.8 to −4.8pt with **dx ≈ 0.00** and tight spread — and it varies per form, which a font
constant cannot produce. These are exactly the forms whose page box we expand from a declared
sliver to a full page (§12 defect 2). We keep content at its FAP origin; Documaker places the
fragment somewhere else on the sheet. Full-page forms, which get no expansion, show ~0 offset.

**So the composition origin is the single highest-value open defect** — it accounts for the
0.0%-scoring Tier 2 group, and nothing about it is font-related. Finding the rule (top margin?
centred? bottom-aligned?) needs the FSISYS/page setup Documaker composes with, not more
font work.

**Process note:** this is the second time a font-id correlation suggested a cause that
measurement then refuted. Correlate to generate a hypothesis; never to close one.

### Fragment composition origin — FIXED

The H record's second group is the section origin: full-page forms declare `(0,0)`, composable
fragments declare `(98,0)`. We were subtracting it; Documaker does not (98 FAP = 2.94pt, which
is essentially the whole measured offset). `Px`/`Py` now use the raw FAP coordinate.

Because full-page forms declare `(0,0)`, the change is a **no-op for them** — zero regression risk,
confirmed: `G2425B` and `A0238C` scores unchanged.

| gate | before | after |
|---|---|---|
| Tier 2 (≥90% of runs within 2.0pt) | 25/44 | **34/44** |
| Tier 1 nothing dropped | 43/44 | 43/44 (unchanged, as expected) |
| Tier 1 character-identical | 40/44 | 40/44 (unchanged, as expected) |

`DEXOTHA` before/after: `Company:` legacy 12.2 vs ours 9.0 → **12.0**; `Policy Number:` 38.2 vs
35.25 → **38.25**. `NR10otHD_B` 0.0% → 81.4%.

**Process failure worth recording:** the first attempt at this fix measured as a no-op and was
nearly written up as a dead end. The re-render loop had silently failed (output redirected to
`/dev/null`), so the measurement read PDFs from the previous build. Only comparing the artefact
timestamp against the binary's caught it. **Never measure a fix without checking that the artefact
is newer than the binary** — and do not redirect stderr away in a verification step.

### Remaining Tier 2 failures (8 forms with ≥25 runs)

`M7901A` 67.6% (field-fill form), `P9905A` 74.2%, `P1060A` 75.0%, `G2412B` 79.3%, `bp7618` 81.2%,
`NR10otHD_B` 81.4%, `G2425B` 86.6%, `IEA4606` 88.6%. None are 0.0% any more — these are partial
mismatches, i.e. a subset of runs on each page rather than a whole-form offset. That is a
different and harder class than anything fixed so far.

### Baseline calibration: coverage improved, but 16010 is not a per-font constant

Splitting the calibration key by element kind (`S,TT` vs `M,TT`) qualified three more
corrections (`14010|s`, `14110|s`, `16110|s`), giving 8 in total. **It produced no measurable
gain**: Tier 2 stayed at 34/44, and Tier 1's order-sensitive metric moved 40→38 while
"nothing dropped" held at 43/44 — i.e. reading-order sensitivity on multi-column pages, not lost
content. The mechanism is kept because it is more principled, but it is not a win; recorded so
nobody re-runs the experiment expecting one.

Widening `CORE_TOL` 0.75→1.25 and `MIN_SHARE` 0.90→0.75 was also tested and **reverted** — it
qualified no additional font and changed no gate.

**The hard residual is font 16010**, the most common body font. Attributing every failing run to
its font id shows it needs ≈1.3pt (`|m`, 258 samples) to 1.6pt (`|s`, 170 samples) — yet its
calibration core is only 60% even at ±1.25pt across 983 samples. **Its offset genuinely varies
by form, so no per-font scalar can correct it.** An earlier 4-form histogram showed ~0–0.5pt and
was simply unrepresentative — a caution against calibrating from a small sample.

Every remaining Tier 2 failure has this shape: `dx ≈ 0.00` (horizontal is exact) and `dy` between
2.05 and 2.7pt — just over tolerance, on a *subset* of runs per page rather than the whole form.
Finding what varies within 16010 is the next real question: candidates are the FXR ascent not
being uniform for that id, or the run's origin record type differing in a way the parser flattens.

### ROOT CAUSE: the baseline offset is per-FORM, not per-font

Measuring font 16010's offset **per form** resolves the puzzle that cost several sweeps:

| form | n | median dy | stdev |
|---|---:|---:|---:|
| P1060A | 20 | **+1.50** | 0.21 |
| M9901C | 103 | +1.55 | 0.22 |
| G2425B | 130 | +1.62 | — |
| P9905A | 158 | +1.60 | — |
| G2412B | 55 | **−1.85** | 0.21 |
| bp7618 | 64 | **−1.62** | 0.24 |
| 372nsN50 | 124 | −0.25 | — |

Within a form the offset is **tight** (stdev ≈ 0.21). Between forms the **sign flips** at a
near-constant magnitude of ~1.6pt. That is why a global per-font median sits near zero with a 60%
core — it is averaging two clusters at +1.6 and −1.6 — and why every attempt to fit one scalar
per font failed while looking statistically reasonable.

**Design consequence — calibrate per form, not per font.** Baseline correction should be computed
**at conversion time, per (form, font id)**, and stored beside the template. This is legitimate
within the determinism contract: conversion is offline, the legacy render is available then, the
computation is a pure function of (FAP, legacy PDF), and the result is recorded in the form's
manifest. It also sidesteps needing to explain the sign flip, which is a genuine open question —
candidates are a per-form coordinate convention (`M,TT` row meaning top vs baseline) or a
reference the parser currently flattens.

Expected impact: every remaining Tier 2 failure has `dx ≈ 0.00` and `dy` of 2.05–2.7pt, so a
correct per-form constant should clear most of them at once.

**Caveat on the numbers above:** `G2425B` (stdev 9.06, min −101.9) and `P9905A` (stdev 44.6, min
−405.8) contain a few grossly wrong pairs where repeated text matched the wrong instance. The
medians are sound; the tails are matcher noise, and a per-form calibrator must use the median
rather than the mean for exactly this reason.

### Per-form calibration implemented — and it did NOT help

`calibrate.py` now also emits `form-calibration.json` (per form, per font key, median), and
`emit-html` prefers it over the per-font table. Given the root cause in the previous section this
should have worked. **It measured worse: Tier 2 34/44 → 32/44.**

Left **disabled by default** (no `form-calibration.json` shipped); the mechanism stays in the
code because the root-cause analysis behind it is sound and the failure is more likely in the
*estimate* than in the idea. Two concrete suspects, both untested:

- The per-form medians are drawn from the same matcher whose tails are wild (`G2425B` stdev 9.06,
  `P9905A` stdev 44.6 from repeated text matching the wrong instance). A per-form median over only
  4+ samples is far more exposed to that than a per-font median over hundreds.
- The calibration form set and the sweep form set are stratified differently, so many swept forms
  had no per-form entry and silently fell back to the per-font value — a mixed model that is
  neither one thing nor the other.

**Bug found while investigating:** `calibrate.py` deleted only `font-calibration.json` before
measuring, not `form-calibration.json`. So every run after the first measured a *partly corrected*
model and produced corrections stacked on corrections. Fixed — both tables are now removed first.
This class of bug is insidious because the output still looks plausible.

### Where the numbers actually stand

| gate | result |
|---|---|
| Tier 1 — nothing dropped | **43/44** |
| Tier 1 — character-identical | 38/44 (the 5 gaps are multi-column reading order, not content) |
| Tier 2 — ≥90% of runs within 2.0pt | **34/44** |

Honest caveat: the Tier 1 character-identical figure has moved 40 → 38 → 39 → 38 across
calibration variants that change no content whatsoever. That spread is **reading-order sensitivity
in the metric**, not quality movement, and it is small enough that none of the last few
calibration experiments can be called an improvement or a regression on that axis. Treat
"nothing dropped" (stable at 43/44 throughout) as the trustworthy Tier 1 signal.

### Sign-flip discriminator: two hypotheses eliminated

Tested and rejected as the cause of the per-form sign flip:

- **The H record.** `bp7618` (−1.62) and `P1060A` (+1.50) both declare `(600,400)`; `M9901C` (+1.55)
  and `372nsN50` (−0.25) both declare `(0,0)`. It does not discriminate.
- **The font mix.** 16010 is the dominant `M,TT` font in every form of both groups.

So the flip is not explained by page setup or font selection. Remaining candidates, untested:
Chromium's device-pixel snapping interacting with each form's particular row coordinates (bounded
at ±0.375pt, so it cannot explain ~1.6pt on its own); a systematic off-by-one-row in the matcher
for forms with many repeated short strings (line height at 10pt is ~11.7pt, so this too fails to
explain 1.6pt); or a `M,TT` row-origin convention the parser flattens.

Given two eliminations and no strong remaining lead, the pragmatic path is the per-form
calibration already built — fix its *estimate* (more samples per form, reject the matcher's wild
tails) rather than keep hunting the mechanism.

### Per-form estimate hardened — still no gain. Calibration is done.

Applying the same core-clustering discipline to the per-form table (min 6 samples, ±0.75pt core,
80% share) to reject the matcher's wild tails made **no difference: Tier 2 still 32/44** versus
34/44 with per-font alone. Per-form calibration is therefore abandoned, not merely disabled —
the root-cause analysis (§ per-FORM sign flip) is well evidenced, but three independent attempts
to exploit it all measured worse.

**Calibration as a whole has reached its limit.** Every variant tried — fixed constant, TTF-derived,
per-font, per-font-per-kind, per-form, per-form-hardened, and two tolerance settings — lands
between 32/44 and 34/44 on Tier 2. The differences are within the noise of which forms happen to
sit in the calibration set. **Stop tuning it.** The shipped configuration is the per-font table
from a 28-form calibration, no per-form table: **Tier 2 34/44, nothing-dropped 43/44.**

The remaining ~2pt residual needs a *mechanism*, not a better estimate, and the two obvious
mechanisms have been eliminated. That is the honest state.

---

## 16. Scale validation — 120 forms (2026-08-16)

Re-ran the whole pipeline at 24 per stratum to test whether the fixes generalise or were fitted
to the original 44-form sample. **They generalise.**

| gate | 44-form sample | **120-form sample** |
|---|---|---|
| Tier 1 — nothing dropped | 43/44 (98%) | **106/109 (97%)** |
| Tier 2 — ≥90% of runs within 2.0pt | 34/44 (77%) | **81/109 (74%)** |
| (diagnostic) stream identical incl. order | 38/44 | 95/109 (87%) |

Median Tier 2 score across all forms: **95.1%**.

**The most important number: zero forms score 0.0% on Tier 2** — down from 11 in the 44-form run.
The whole-form-offset class that the H-record origin fix targeted is **completely eliminated**,
not merely reduced. Every remaining failure is partial.

On forms with ≥25 text runs the pass rate is 53/79 (67%), lower than the headline because small
forms pass more easily — worth quoting both figures rather than the flattering one.

### The 3 remaining Tier 1 failures drop only DIGITS

| form | dropped |
|---|---|
| `EP9907SCHEDB` | `1` ×2 |
| `EB9907C` | `6` ×6 |
| `EP9908B` | `6` ×6 |

Not words, not punctuation — **specific digit glyphs, repeated**. That is a narrow, distinctive
signature (a superscript/footnote marker, a page-number element, or a record type the parser
skips) and should be quick to run down. It is the entire remaining Tier 1 backlog across 109 forms.

### All 3 Tier 1 failures are unpopulated system fields — zero real content defects

The dropped digits are the **total page count**. The FAP static text is literally `Page 1 of`
(declared length 9); Documaker supplies the trailing `6` at render time. `EB9907C` and `EP9908B`
are 6-page forms, hence `6`×6; `EP9907SCHEDB` is a 2-page form, hence `1`×2.

So it is not dropped content — it is a **Documaker system value we do not compute**, the same
category as `M7901A`'s underscore field fill. Across 109 forms, **the Tier 1 content backlog is
therefore zero real defects.**

**Deliberately NOT fixed with a heuristic.** Matching the text "Page N of" and appending
`PageCount` would work today and is tempting, but it is pattern-guessing on document content —
precisely what determinism rule 4 forbids ("ambiguity fails loudly, never guess"). A form whose
body text happens to end "…page 3 of" would be silently corrupted.

The correct fix is a **declarative system-value list**: an explicit, versioned registry of
Documaker-computed values (total pages, current page, form edition, print date) that the emitter
resolves by rule rather than by inference. That belongs with the binding layer (§3), because it
is the same problem — a value that comes from outside the FAP — and it should be resolved through
the same mechanism rather than a second ad-hoc path.

**Until then, the harness should treat unpopulated system fields as expected.** Combined with the
field-fill finding, this is the second instance of the same structural point: **comparing blank
renders conflates "we lost content" with "Documaker computed something we haven't".** Comparing
*filled* renders (§3, `populate-from-model` already exists) removes both at once and is the
higher-value path.

---

## 17. Tier 1 excludes field regions — the gate is now clean at 109/109 (2026-08-22)

The two structural caveats above (field fill, unpopulated system values) are now handled in the
harness rather than carried as prose disclaimers. `contentdiff.py` removes **declared `F,` field
regions from both renders** before comparing, so a blank-render comparison measures only static
content.

**Filled renders are not available from our oracle.** `FAP2PDF.EXE` takes only `/I=fapfile
/X=fxrfile` — there is no way to supply field data, so it can *only* produce a blank reference.
A genuinely filled legacy render means driving `GENDAW32.EXE` through a full Documaker job (INI
config plus extract data), which §5 deliberately avoided. Region exclusion gets the same practical
result now; the filled-render route stays open as separate work.

| gate (120-form sweep, 109 scored) | before | after |
|---|---|---|
| **Tier 1 — nothing dropped** | 106/109 | **109/109** |
| (diagnostic) stream identical incl. order | 95/109 | 95/109 — unchanged, as required |

All three former failures (`EP9907SCHEDB`, `EB9907C`, `EP9908B` — the "Page N of *6*" system value)
now pass. 946 characters are excluded across 13 of 109 forms, and the count is **printed with the
result** so the exclusion can never be silent.

### Three mechanics this needed, each found by measurement

**1. Exclusion must be per CHARACTER, not per word.** PyMuPDF merges a field's underscore fill
together with the static label beside it into one "word", so a word-level test cannot separate
them. Character bboxes can. Field rects are read from the emitted HTML (`span.abs.field`), which is
already a deterministic product of the pipeline, and padded by 1.5pt / 4.0pt — a glyph's bbox is the
font's *line* box, so it is taller than the field's declared height.

**2. Reading order must be preserved.** Taking characters in PDF content-stream order collapsed the
order-sensitive diagnostic 95 → 20, because the two renders emit drawing operations in different
sequences. Re-applying the same `LINE_TOL` baseline banding that `page_text` uses restored it to
exactly 95. **This is the third time the reading-order band has been load-bearing** (§14) — any
comparison that linearises a page needs it.

**3. Exclusion must never be able to MANUFACTURE a drop.** Deciding exclusion from each render's
own glyph positions is asymmetric at a boundary. Measured on `DFP0014F`: its `Type of Farming`
field rect sits directly on top of its own static label, and the `y` lands 0.1pt *inside* our box
and *outside* legacy's — so we filtered a character legacy kept and the gate reported a dropped
`y` on a correct render. Two new false failures appeared this way before it was fixed. The gate now
discounts anything our own filter removed (`dropped = (bag_l - bag_o) - filtered_ours`), so the
exclusion can only ever *miss* a drop, never invent one. **A filed-forms gate must fail safe in
that direction** — inventing work is the mistake that disqualified ink IoU.

Also filtered: control characters. `372nsN50`'s legacy PDF carries a stray U+001B that PyMuPDF's
word extractor silently drops, so keeping it would have failed a form the previous gate passed.

### Validation — against forms Products already accepted

Per the §14 rule that any new gate must be validated against accepted work before it is trusted:
`EB2410A`, `A0238C`, `EB22489Q` and `P0010G` all still pass (the first three at 100% word match),
as does `G2425B`. No accepted form regressed. Two consecutive runs are byte-identical.

### What the exclusion does not reach

`M7901A` goes **452 → 20** dropped underscores rather than to zero. Documaker's fill can extend well
beyond the declared rect — a ~100pt field filling ~250pt — and widening the padding far enough to
catch it would start swallowing real static text. The residue is reported rather than tuned away.
Confirmed against source: the FAP contains exactly 29 underscore characters, all of which we emit,
so nothing is actually lost.

**The Tier 1 content backlog across the sweep is now genuinely zero, and the gate says so without a
footnote.** Tier 2 (81/109, §16) is untouched by this and remains the open fidelity axis.

---

## 18. ROOT CAUSE FOUND: text is anchored to the BOTTOM of its box (2026-08-22)

**Documaker places the text baseline on the bottom edge of the declared box, not the top.** We
anchored to `row1` and let Chromium's line box find the baseline, which is only ever right when the
box height happens to equal the line height we assumed.

Measured over **17,193 legacy text records across all 109 swept forms**:

| anchor | median | stdev |
|---|---|---|
| `baseline − Py(row1)` (what we did) | +9.03 | **2.05** — swings 3.9→16.8 by font size and box height |
| `baseline − Py(row2)` (what Documaker does) | **−0.09** | **0.06** |

**17,191 of 17,193 records (100.0%)** land within 0.5pt of `Py(row2) − 0.09`. It holds for every font
id, for both `T,` static text and `M,TT` tokens, and per-group stdev is 0.04–0.06 throughout.

### Why this hid for so long

The offset *looked* per-form and bimodal (§12–15) because **a form tends to use one box height
throughout, so "which form" was standing in for "how tall is the box"**. `G2425B` is the tell: it uses
both 6.0pt and 9.36pt boxes and was the form whose font-16010 samples looked "bimodal at 60% core".

That fully explains the dead ends. The quantity is neither a font property nor a form property, so
**no per-FontId or per-form scalar could ever have described it** — which is why all eight calibration
variants landed in the same 32–34/44 band. The three-cluster pattern (+1.6 / ~0 / −1.6) was just the
three common box heights (6.00 / 7.92 / 9.36pt) against one assumed line height.

The fix is one line of geometry, and it needs **no calibration and no oracle**: Chromium puts the
baseline at `top + (lineHeight + ascent − descent)/2`, so solve that for `top` given the target
baseline `Py(row2) − 0.09`. `font-calibration.json`, `form-calibration.json` and `tools/calibrate.py`
are **deleted** — they were fitted to the old anchor and would now actively corrupt the geometry.

### Result

| gate | before | after |
|---|---|---|
| **Tier 2 — ≥90% of runs within 2.0pt** | 81/109 (74%) | **95/109 (87%)** |
| **Tier 2 — at the tightened 1.0pt** | — | **85/109 (78%)** |
| Tier 1 — nothing dropped | 109/109 | 109/109 |
| (diagnostic) stream identical incl. order | 95/109 | 96/109 |
| median Tier 2 score | 95.1% | **99.2%** |

**The tolerance is tightened 2.0pt → 1.0pt.** §15 called 2.0pt "a workaround tolerance absorbing a
systematic offset, not a quality statement" and said to tighten it once that offset was fixed. Re-derived
against the four accepted forms: they score 91.0 / 97.5 / 97.7 / 94.8% at 1.0pt, so 90% still clears the
worst. Their scores are **flat from 1.0pt to 2.0pt**, so tightening costs no accepted-work headroom —
what remains on `EB2410A` is the bold advance-width defect (§10), which no tolerance in this range
reaches. `.gd` golden suite green (8 OK); `emit-html` still byte-identical across runs.

### Process note — the staleness trap caught this work too

The first tolerance table measured `EB2410A`, `EB22489Q` and `P0010G` as scoring **0.0% at 1.0pt** and
nearly led to the conclusion that a per-face residual remained. They were simply **not in the current
sweep sample**, so their `_ours.pdf` was stale from the previous build and I was measuring the old
anchor. Re-rendered, they score 91.0 / 97.7 / 94.8%.

§15 already records this exact failure and I built the newer-than-binary check into the re-render
script — then applied it only to the sweep list, not to the extra forms measured alongside. **The check
belongs at the point of measurement, not the point of rendering.** Any form named in a comparison must
be proven fresh, whatever list it came from.

---

## 19. Vertical is finished; the remainder is horizontal (2026-08-22)

### The renderer constant

After §18 a uniform residual remained: **Chromium places the baseline 0.600pt higher than the CSS box
model predicts** from the face's hhea/OS-2 ascent (pooled median over 76,499 runs; p5..p95 =
−0.95..−0.20, entirely one-sided). It is **size-independent** — the same ~0.6pt at 8, 10 and 12pt —
which rules out a metrics error (that would scale with point size) and fits the ascent being rounded to
whole device pixels (1px = 0.75pt at 96dpi).

It is a property of the **renderer**, so it is a named constant pinned with the Chromium build
(determinism rule 5), measured from our own output with no oracle. A per-face table and even a
per-document one were both simulated: **both give 107/109, identical to the single constant**, so there
is nothing to gain from a finer key. There is an irreducible per-document scatter of ±0.35pt.

**Vertical placement is now done:** `|dy| > 1pt` is **0.0% of glyphs** on every accepted form.

### Tier 2 was measuring the wrong thing

The run-level metric matched whole spans on their exact string, and **that made it report placement
defects the render did not have** — the fourth metric in this project to do so. The two engines segment
a line into runs differently, so a correctly-placed string either failed to look up at all or, for a
repeated short word like `the`, matched the wrong instance. Every such miss had its text present on the
page: **220/220 on `BP0564A`, 135/135 on `PRVNOTCB`, 9/9 on `A2303C`**.

Worse, matching runs only checked where each run **started**, so it was structurally blind to drift
*inside* a run. Its flattering 1.0pt scores were never a quality signal.

Tier 2 is now measured at **glyph level** — same lesson, and same fix, as word-level diffing in §14.

### Where that leaves the numbers

| | |
|---|---|
| **Tier 1 — nothing dropped** | **109/109** |
| **Tier 2 gate — ≥90% of glyphs within 3.0pt** | **106/109** |
| Tier 2 at the 1.0pt quality bar (diagnostic) | 66/109 |
| `.gd` golden suite / determinism | 8 OK / byte-identical |

The 3.0pt gate is set, as always, by the worst accepted form (`A0238C`, 90.6%) — **it is not a claim
that 3pt is good.** `A0238C` really does carry ~3pt of intra-run horizontal drift in its 18pt bold
heading, and Products accepted it by eye. The 1.0pt column is the number to drive down.

### Intra-run horizontal drift — the last named defect, and two failed attempts

`dx` accumulates along a run: on `A0238C`, −0.08pt at the first glyph of a run to −2.70pt by the
fortieth. It is worst on **bold and large sizes** (Arial-BoldMT 18pt: median −3.16pt, 75% of glyphs
beyond 1pt), matching the §10 observation that the residue concentrates in bold runs.

**Attempt 1 — drop the FXR advance correction. WORSE, and the reasoning behind it was wrong.**
The legacy PDFs embed *only* base-14 `Helvetica`/`Helvetica-Bold` (verified), and Arial is
metric-compatible with Helvetica, so our natural advances "should" already match and the correction
should be unnecessary. Measured: turning it off **doubled** the error (`A0238C` 25.9% → 52.1% of glyphs
beyond 1pt; `EB2410A` 7.6% → 16.8%). The conclusion is that Documaker emits explicit per-token
positioning so the layout follows the **FXR width table** rather than whatever face is substituted — so
the FXR really is the authority, and metric-compatibility of the substituted face is beside the point.

**Attempt 2 — anchor every word at its own FXR-measured offset. WORSE at the gate.**
It did exactly what it was designed to do: `EB2410A`'s accumulation vanished (−0.01pt at the first
glyph → +0.18pt at the fortieth, versus −0.01 → −0.54). But it traded accumulation for a larger
scatter, and **every accepted form regressed** — `EB2410A` 92.4 → 88.0%, `EB22489Q` 94.3 → 87.5%,
`P0010G` 98.7 → 95.1%, `A0238C` 74.1 → 70.5%. Reverted. Chromium's own inter-word advances plus the
run-level FXR correction track the legacy render better than FXR word offsets do.

### Then the legacy render was measured directly — and the true model still lost

Rather than infer further, the legacy PDFs were interrogated. **Two facts are now established, and both
contradict what the emitter does:**

**1. The correction is MULTIPLICATIVE, not additive.** Take the per-character advances of two legacy
spans in the same face: they are related by a pure ratio with **coefficient of variation 0.0000** —
exact, on every span pair tested — where the additive model's cv is 0.15–0.40. It surfaces as a
fractional point size per span (`10.184`…`10.369` where the FXR declares 10). This matters because
additive `letter-spacing` gets a run's *total* width right while leaving its *interior* wrong — a wide
glyph needs more absolute correction than a narrow one — which is precisely the mechanism that makes
`dx` accumulate along a run.

**2. Documaker fits each token to ITS DECLARED FAP BOX**, not to the FXR width table. Legacy rendered
token width ÷ `(col2 − col1)` has a median of **0.9996–1.0057**, with **89–93% of records inside 2%**.

Also worth recording: legacy's text matrix is **non-uniform** — on one span the advance scale works out
to ~10.61 while the reported vertical size is ~10.36. Horizontal and vertical are scaled independently.

**Attempt 3 — multiplicative correction via point size (FXR target). A WASH.**
`EB2410A` 92.4 → 91.2%, `A0238C` 74.1 → 74.4%, `EB22489Q` 94.3 → 94.5%, `P0010G` 98.7 → 98.8% within
1pt, and the accumulation profile barely moved (`A0238C` −0.06 → −2.38pt across a run, versus
−0.08 → −2.70 before). The ratio we compute is close to but not equal to legacy's: our 18pt bold
heading renders at 17.6pt where legacy uses 17.93.

**Attempt 4 — box-fit as a horizontal `scaleX`. MUCH WORSE.**
`EB2410A` 92.4 → 71.8%, `EB22489Q` 94.3 → 64.4%, `P0010G` 98.7 → 66.9% within 1pt. (`A0238C` did reach
100% at 3.0pt, the only bright spot.) The likely reason is the tail of that 89–93%: **a declared box is
often padding rather than a tight fit** — a record whose declared length exceeds its text — so box-fitting
stretches text that legacy leaves alone, and nothing in the FAP record distinguishes the two cases.

### Where that leaves it

Four mechanisms tried, all worse or a wash, so the run-level additive correction stays — **because it
measures best, not because it is the truest model.** That is an uncomfortable but honest position, and
the code says so.

The two measured facts above are the durable result of this pass; they are exactly what a fifth attempt
should build on, and they rule out a whole family of whole-run scale factors. **A correct fix needs
per-glyph positioning driven by the legacy PDF's actual TJ offsets** — reproducing the layout rather than
re-deriving it — and a way to tell a tight box from a padded one. Everything short of that has now been
tried and measured.

---

## 20. Parser hardening: the `.gd` path now has a content gate too (2026-08-22)

Three bugs found through the HTML pipeline -- CP1252 decoding, enclosing-quote stripping and
reading-order banding -- all lived in the **shared** FAP parser in `core/`, so they silently corrupted
the GhostDraft `.gd` output as well. That path had no content gate to notice. `tools/gdcontent.py` is
now that gate.

It is a **self-consistency** check rather than a parity check, because a `.gd` cannot be rendered
locally: it asserts that every character the FAP declares as static text survives into the `.gd`'s RTF
body. That is exactly the class of defect those three bugs were.

### Result

| corpus | result |
|---|---|
| Golden `.gd` suite | **8/8 exact**, surplus 0 |
| All quote forms, regenerated | **427/427 exact**, surplus 0 -- but 126 are vacuous (no FAP static text), so **real coverage is 301/301 forms with content** |

Every form matches **character for character**, with zero surplus. The three parser fixes did clean the
`.gd` path, and there is now a gate to keep it clean.

### The checked-in `.gd` corpus was three weeks stale

`output/quote-forms-gd/` dated **2026-07-28**, before both parser fixes (2026-08-16). Measuring it
would have scored the *old* parser and proved nothing about today's. Regenerated via `convert-quotes`
first (427/427, brace-bad=0, failures=0). Same staleness discipline as sections 18-19 -- and note these
artefacts are *generated output*, not renders, so the rule is broader than the PDF harness.

### Building the gate reproduced this project's own recurring mistake, twice

Both were caught by a negative control -- deliberately deleting characters and checking the gate fires:

1. **Escapes resolved after control-word stripping.** RTF hex and unicode escapes were being eaten as
   control words, so their characters vanished and the gate reported drops that did not exist. Order
   matters: resolve escapes first. Replaced the regex stripper with a brace-aware single-pass extractor.
2. **Surplus characters MASK drops.** With the RTF header (font table, colour table) included, deleting
   three letters from a body word showed up as a shortfall of only **one**. Multiset comparison sees
   only the net shortfall, so any non-content text in the bag buys silence. Skipping the header
   destinations and the generated binding placeholders takes surplus to **0**, and the same negative
   control then reports exactly 3 of 3.

**A gate must be tested for sensitivity, not merely for passing.** A metric that cannot fail is worth
nothing, and four of this project's metrics have already cried wolf in the other direction. `surplus` is
printed on every row precisely because it is the number that can hide a defect.

---

## 21. The gates were blind to everything that is not a glyph (2026-08-22)

Tier 1 compares characters and Tier 2 compares glyph positions. Both are **text-only**, and
`emit-html` does not emit FAP `G,` image records at all. So a form could be missing its entire logo,
or every rule on the page, and still score ~100%. Every image-bearing form in the sweep was passing
Tier 2, several above 99%.

`tools/nontextink.py` closes that: rasterize both renders, mask out the text on **both** sides (the
union -- masking only the legacy's text would let our own glyphs be scored as artwork), and measure how
much of the legacy's remaining ink we reproduce. It covers logos and rules alike, which is the right
scope: anything on the page that is not a glyph.

**First run: 32/62 scored forms passed**, against Tier 2's 106/109. The blind spot was real.

### It was not mainly about images

Eight forms reproduced **0.0%** of the legacy's non-glyph ink -- thousands of pixels each -- and none of
them declared an image. Dilating the text mask from 1px to 4px moved the numbers not at all, ruling out
an antialiasing artefact, so the gate was not crying wolf. Cropping to the densest band found a filled
rectangle at y=90.0-90.4pt spanning x=71.4-540.2: a horizontal rule.

Its source is `M,PX,(2608,2386,3019,18014),(14,14),1,0` -- row2 3019 = 90.57pt, cols 2386-18014 =
71.6-540.4pt. **`M,PX` is a line record nested inside a text area**, with a payload identical to a
top-level `X,` record; only the prefix differs. The parser recognised `X,` and silently discarded the
rest. **315 of 4210 forms use them, 2,448 records in total.**

| form | before | after |
|---|---:|---:|
| M7056A | 0.0% | **100.0%** |
| M7611A | 0.0% | **92.3%** |
| M7115A | 45.6% | **91.7%** |
| IM70176O | 0.0% | 82.5% |
| M7215A | 0.0% | 58.7% |

Overall 32/62 -> 35/62; Tier 1 and Tier 2 unchanged, as expected for a non-text fix.

### Two things this says about the harness

**1. This is the fourth bug in the shared FAP parser**, after CP1252 decoding, quote stripping and
reading-order banding -- and like the others it silently corrupted the GhostDraft `.gd` path too, which
now gains the rules.

**2. The golden suite passed it vacuously.** None of its eight forms contained an `M,PX` record, so a
real change to the shared parser reported "8 OK, 0 CHANGED, no regressions". A regression suite that
does not contain the construct cannot guard it. `M7215A` (20 `M,PX` records) is now in the suite.

That is the session's recurring lesson landing a third time, in a third place: **ask what the check is
blind to, not just what it reports.** Tier 2 was blind to intra-run drift, both gates were blind to
non-text ink, and the golden suite was blind to a construct none of its forms used.

### Still open on this axis

- `RESCBDY` 21.4%, `STF2471120` 21.6%, `N1STBA` 55.5% -- these DO declare `G,` images, which we still
  do not emit at all. Decoding the Documaker `.LOG` format (86 assets on disk, e.g. `NEWEIG1.LOG`) or
  substituting the GhostDraft branding assets is the remaining image work. Real scope is **191 forms
  with `G,` records**, not the 548 quoted in section 5 -- that figure counted `N,` records too, and `N,`
  is overwhelmingly developer notes (370 forms), not artwork.
- `BOPSECT3` 16.2% and `A2015G` 57.5% declare no image and were unmoved by the `M,PX` fix, so they are
  a third cause, not yet identified.
- 54 of 116 forms are skipped for having under 200 non-text ink pixels. The gate reports that count
  rather than folding them into a pass.

---

## 22. Shaded boxes: the `X,` style field was never read (2026-08-22)

A non-zero style on an `X,` record marks the rectangle as **filled**. We ignored the field and drew
every such record as a hollow outline, so shaded header bands rendered as empty boxes.

Closed **geometrically**, not by correlation: every non-zero-style record matches a filled rectangle in
the legacy PDF at identical coordinates -- `BOPSECT3` 7,830pt2 and `STF2920214` 11,988pt2 exact.
(The correlation was suggestive on its own -- median non-text ink 38.6% for forms with a non-zero style
versus 94.9% without -- but this project's rule is that correlation opens a question and never closes
one.)

### The shade table is measured, and that mattered

| style | measured grey | linear prediction |
|---|---|---|
| 7 | 0.85 | 0.85 |
| 8 | 0.75 | 0.75 |
| 9 | 0.65 | 0.65 |
| 10 | **0.55** (confirmed) | 0.55 |
| 12 | **0.788** | 0.35 ❌ |

Styles 7-10 fit `grey = 1.55 - style/10` perfectly, and rendering a style-10 form on purpose confirmed
it exactly. Style 12 then measured **0.788** where that formula predicts 0.35 -- presumably a hatch
pattern rather than a grey level. **Extrapolating the formula would have shipped a wrong shade on 23
records.** The emitter carries a lookup table of the five styles the library actually uses, and any
unmeasured style falls through to the outline path rather than being guessed at (determinism rule 4).

### Paint order is not cosmetic

The first version emitted shading in the existing line loop, i.e. *after* the text. Because everything
is absolutely positioned, the band painted **over** the text it belongs behind. `STF2920214` regressed
**75.6% -> 25.7%** and that is what caught it; shading now has its own pass before the text loop, and
the form is back to 75.6%.

### Result

| form | before | after |
|---|---:|---:|
| BOPSECT3 | 16.2% | **100.0%** |
| RESCBDY | 21.4% | **100.0%** |
| N1STBA | 55.5% | **99.8%** |
| A2015G | 57.5% | **97.8%** |
| STF2471120 | 21.6% | **95.6%** |

Non-text ink **35/62 -> 40/62**; Tier 1, Tier 2 and the `.gd` gates all unchanged.

**The image estimate in section 21 was wrong.** `RESCBDY`, `STF2471120` and `N1STBA` all declare `G,`
images and were listed there as blocked on image decoding. They were blocked on **shading**. Forms
declaring an image now pass 5/6 rather than 2/6, so the remaining image work is smaller than section 21
implies -- `MCS90D` at 82.2% is the only image-declaring form still failing.

### The golden suite was blind again

For the second time in a row a real change reported "OK, no regressions" because no form in the suite
used the construct. `BOPSECT3` (style-8 shaded box) has joined it, as `M7215A` did for `M,PX`. Both
were added the same day, which is the strongest evidence yet that **suite coverage should be checked
against the construct inventory**, not assumed.

---

## 23. Non-text ink closed to 61/62 (2026-08-22)

Two more defects, both found by chasing the residue from section 22.

### `M,PX` is not a rectangle

Measured over 67 records in 10 forms against the legacy renders:

- **vertical edges are never drawn** -- zero, in every form checked;
- the **bottom** edge is drawn 64/67 (96%);
- the **top** edge splits cleanly on box height: **2/35 below 420 FAP units, 31/32 at or above**.

We were drawing all four edges of a 12-15pt box where Documaker draws one or two thin rules. The
mechanism behind the height split is unknown, so the rule is empirical -- but it is a wide split rather
than a fitted curve, and it covers the library: of 2,448 `M,PX` records, 1,238 sit below the threshold
and 1,207 above, with only **3** in the untested 412-426 gap.

### Boxes were drawn ~1pt too large

`.box` used the CSS default `content-box`, so the border was added **outside** the declared rectangle.
Every box came out ~1pt oversized and its bottom edge landed ~1.4pt below the legacy rule -- past the
match tolerance, so a correctly-placed box scored as a miss. The FAP rectangle is the outer edge, so
`box-sizing: border-box` is what it actually means. This one line affected every rectangle in the
library, not just the forms under investigation.

### Result

| | |
|---|---|
| Non-text ink | **40/62 -> 61/62**, median score **100.0%** |
| Forms declaring a `G,` image | **6/6 pass** |
| Tier 1 / Tier 2 / `.gd` gates | unchanged |

**Nothing in the sample is currently blocked on image decoding.** Sections 5 and 21 both treated images
as the major non-text blocker; every image-bearing form in the sample now passes without a single `.LOG`
being decoded, because their failures were shading, `M,PX` and box sizing. Image work is still needed for
real logo fidelity -- the gate measures ink coverage, and a logo's ink is largely absent -- but it is not
what was holding these scores down.

### The one remaining failure, and a rule NOT adopted

`M7902Ba` (66.6%) renders its two top-level `X,` records as horizontal rules, exactly as `M,PX` does --
and both carry the second parameter group `(14,14)`, which is what every `M,PX` record carries. That
suggests the group, not the record prefix, is the real discriminator.

**It was not adopted, because measuring it refuted the clean story.** Classifying every rectangle record
in the sweep by its second group gives:

| record | group | n | how the legacy draws it |
|---|---|---:|---|
| `M,PX` | (14,14) | 67 | rules only, never a rectangle |
| `X,` | (14,14) | 12 | **mixed** -- 4 rectangles, 5 rules, 3 filled |
| `X,` | (30,30) | 18 | rectangle, 18/18 |
| `X,` | (15,15) | 9 | rectangle 7, filled 2 |

Twelve samples split three ways is not a rule. `M7902Ba` stays failing rather than be fixed by a guess.

---

## 24. Record-type audit, and M,I bullets (2026-08-22)

Three defects in a row -- `M,PX`, `X,` shading, box sizing -- were all found the same way: something
rendered wrong, a gate noticed, and the cause turned out to be a FAP construct the code ignored. That
is a slow way to find them. So instead: **inventory every record type in the library and check it
against what the parser actually dispatches on.**

| record | count | handled? |
|---|---:|---|
| `M,` (all subtypes) | 2,651,520 | `M,TT` `M,H` `M,P` `M,E` `M,PX` `M,X` `M,I` yes; `M,O` `M,P1..3` `M,PE` ignored |
| `A,` | 1,343,088 | only `A,T1` (field references); the rest are audit stamps |
| `T,` | 72,615 | yes |
| `F,` | 58,058 | yes |
| `V,` | 30,537 | version history, correctly ignored |
| `X,` | 23,559 | yes |
| `H,` | 8,078 | yes |
| `N,` | 399 | **no** -- overwhelmingly developer notes, not artwork |
| `G,` | 241 | **no** -- images, the real remaining gap |
| `C,` | 139 | **no** -- `"EDIT BACKGROUND"` colour definitions, design-time only |
| `L,` | 10 | **no** |
| `I,` | 3 | **no** |

The audit immediately found `M,I`.

### `M,I` is a bullet

634 records across 60 forms, and we drew nothing for them. In the legacy render each one is a
**filled+stroked path of four curves -- a solid black disc** about 3.9pt across, at exactly the declared
coordinates. Both trailing parameter values present in the library (55 and 6) render identically, so
the record needs no interpretation beyond its geometry. `A9905A` scores 99.4% on non-text ink.

### What is left unhandled, and why that is fine

`N,` is the interesting one: 399 records and it *sounds* like an image reference, which is how it got
counted toward the "548 forms with images" figure in section 5. Sampling it shows developer commentary
("Combine Mailer Page (BM9DMAIL) and BP1206 ... so that document will duplex"). It is not rendered.
`C,` is a design-time colour palette, `V,` is audit history, and `L,`/`I,` are 13 records between them.

**`G,` -- images -- is the only unhandled record type that represents real missing content.** 241
records, 191 forms, 86 `.LOG` assets on disk.

---

## 25. Attempt 5 at horizontal drift, and why a better width model still lost (2026-08-23)

The 300-form run left the `BAN*` cluster failing placement -- `BAN02INT` 56.0%, `BANAFPCERT` 68.8%,
`BAN_ERR` 78.6%. They are **Times** forms rather than the Arial ones everything else was measured on,
which made them a useful independent test of the section 19 conclusions.

What they showed:

- Vertical is fine: `|dy| > 1pt` is **0.0%** of glyphs.
- Run START positions are essentially exact (dx +/-0.2pt on matched runs).
- `dx` accumulates monotonically **inside** a run: -0.20pt at the first glyph, **-7.43pt by the
  fortieth**. Pure advance drift.
- Our absolute advances are uniformly **2.3-2.7% narrower** than legacy (stdev 0.006 -- very tight).
- Turning the FXR correction off changed almost nothing (56.0 -> 54.9%), so it is not the culprit.

### A measurement that looked conclusive

Section 22 established that Documaker fits a token to its declared box, and section 22's attempt 4
failed only because a box is often padding. So: use the box **only when it is already within 5% of our
natural width**, i.e. when the text evidently fills it. Measured over 1,512 matched runs in 80 forms:

| | |
|---|---|
| runs where the box is tight by that test | 1,147 / 1,512 (76%) |
| current width error on those, median | 0.87% |
| **box-fit width error on those, median** | **0.16%** -- a 5.4x reduction |
| the other 24% | box is 7.7% wider than the text (what sank attempt 4) |

### It still measured worse, and that is the finding

| form | @3.0pt | @1.0pt |
|---|---|---|
| EB2410A | 99.6 -> **96.3** | 92.4 -> **83.2** |
| A0238C | 100.0 -> **93.9** | 74.4 -> **72.4** |
| BAN02INT | 56.0 -> 61.0 | -- |

Reverted: it fails accepted work, which is disqualifying regardless of how good the width numbers look.

**A better width model is not a better placement model.** Matching a run's total width more accurately
says nothing about where the glyphs land *inside* it -- letter-spacing redistributes them uniformly,
and legacy's internal distribution is not uniform. Five attempts have now each improved some measure of
width or scale and failed on placement:

| # | attempt | result |
|---|---|---|
| 1 | drop the FXR advance correction | doubled the error |
| 2 | per-word FXR offsets | removed accumulation, larger scatter, all accepted forms regressed |
| 3 | multiplicative correction via point size | a wash |
| 4 | box-fit `scaleX`, +/-15% guard | much worse |
| 5 | box-fit target, +/-5% tight guard | width error 5.4x better, placement worse |

**Stop optimising run width.** The remaining error is the distribution of glyphs within a run, and the
only thing that reproduces that is per-glyph positioning. Anything that adjusts a single number per run
-- spacing, scale, size, target width -- has now been tried.

---

## 26. CORRECTION to section 23: the M,PX edge rule is 80% accurate, not 97% (2026-08-23)

Section 23 shipped the `M,PX` edge rule on 67 records from 10 forms and reported the top-edge split as
2/35 below the height threshold and 31/32 above. **Re-measured on the 300-form sweep -- 164 records
across 23 forms -- it is materially weaker:**

| | section 23 (n=67) | 300-form sweep (n=164) |
|---|---|---|
| bottom edge drawn | 64/67 (96%) | 145/164 (88%) |
| top edge, height < 420 | 2/35 (6%) | 15/94 (16%) |
| top edge, height >= 420 | 31/32 (97%) | 57/70 (81%) |
| **both edges correct** | -- | **132/164 (80%)** |

The original sample was dominated by one form family (`M7xxx`), which is exactly the bias that makes a
small sample flatter a rule. `EG0421D2` is a clean counter-example: a 360-unit box where legacy draws
the **top** edge, which the rule predicts it will not.

### The rule stays, because the alternatives are worse

Measured on the same 164 records, for the top edge alone:

| policy | top edge correct |
|---|---|
| **height threshold (shipped)** | **136/164 (83%)** |
| always draw both edges | 72/164 (44%) |
| never draw the top | 92/164 (56%) |

And all three beat what was there before, which drew a full rectangle: 0% on the edges plus two
spurious vertical rules per record.

**The honest position is that this is a useful 80% heuristic for a construct with an unknown rule, not a
decoded format.** It is confined to `M,PX` records, its errors are a single thin rule either drawn or
missing, and it is worth revisiting if the mechanism is ever found. Recorded here rather than quietly
left at the flattering figure -- a small sample overstating a rule is the same trap as a small
calibration set (section 15) and a construct-blind regression suite (sections 21-22).

---

## 27. The 0-byte oracle, and a font we were silently substituting (2026-08-23)

### Why 12 forms could not be scored at all

Section 11 recorded, in August, that some forms produce an empty legacy PDF and that the cause was
uninvestigated. It is this: **FAP2PDF resolves embedded fonts relative to its working directory**, and
these forms use `DocuDing.TTF`, the Documaker dingbat face. When it cannot find one it prints
`Failed to read file DocuDing.TTF`, then prints `PDF file created successfully`, writes a **0-byte**
file, and exits **0** -- three separate ways of not telling you it failed.

The font is on disk at `mstrres/Fmres/deflib`. `sweep.py` now copies all 29 faces into the work
directory alongside `FSISYS.INI`. All 12 forms render, and all 12 pass every gate:

| | before | after |
|---|---|---|
| scored by at least one gate | 271/300 | **283/300** |
| green on every gate | 258 | **270** |
| Tier 1 | 259/259 | **271/271** |
| Tier 2 within 3.0pt | 251/260 | **263/272** |
| non-text ink | 154/158 | **159/163** |

### The gates cannot see a wrong glyph SHAPE

Chasing that dependency exposed a defect none of the three gates can detect. `TtfFor()` fell back to
Arial for any face that was not Courier or Times, so **DocuDings symbols were rendering as Latin
letters** -- 48 spans across 16 forms in the sweep.

Every gate passed those forms:

| gate | what it compares | why it missed this |
|---|---|---|
| Tier 1 | character codes | the code is identical; only the glyph differs |
| Tier 2 | glyph positions | a wrong glyph sits in the right place |
| non-text ink | ink outside text boxes | text is masked out by construction |

This is the fourth distinct blind spot found by asking what a check cannot see, after intra-run drift,
non-glyph ink, and the construct-blind regression suite. **Nothing currently measures glyph identity.**
A future gate could compare rendered glyph bitmaps inside the text boxes that the ink gate masks -- the
raster is already there.

### The other substitutions are deliberate and correct

Audited every typeface the sweep uses:

| typeface | spans | maps to | verdict |
|---|---:|---|---|
| Arial | 157,721 | arial.ttf | correct |
| UniversATT | 77,477 | arial.ttf | **correct for this channel** -- FAP2PDF substitutes base-14 Helvetica, and Arial is Helvetica-metric-compatible (section 10) |
| AlbanyAMT | 266 | arial.ttf | correct -- Albany is a metric-compatible Arial clone |
| ArialNarrow | 220 | arial.ttf | correct -- verified those forms embed plain `Helvetica` in the legacy PDF |
| Times | 59 | TIMES.TTF | correct |
| **DocuDings** | **48** | ~~arial.ttf~~ **DocuDing.TTF** | **was wrong, fixed** |
| ArialBlack | 14 | arial.ttf | no TTF on disk; legacy substitutes Helvetica |
| Courier | 2 | COURIE.TTF | correct |

---

## 28. Images: the `.LOG` format is decoded (2026-08-23)

The last unhandled FAP record type, open since section 5. `G,` names a Documaker `.LOG` raster sitting
beside the FAP files, and we drew nothing for them.

### The format

```
header    " rows,cols,bytesPerRow,dpi,bpp,0,...,paletteSize"
palette   paletteSize lines of "r,g,b"   (only when paletteSize is non-zero)
data      hex, a row split over several lines, continued lines ending in a backslash
```

Two details each cost a debugging round:

- **`bytesPerRow` is a padded row STRIDE.** It runs a byte past `ceil(cols*bpp/8)` on many files, so it
  must be used as given. Recomputing it rejects a third of the library.
- **1bpp is ink-set, not luminance.** A set bit is BLACK. Taking it the other way renders a signature
  as white-on-black -- which is exactly how the first decode came out.

### Result

**All 76 assets on disk decode**, and **every image reference in the library resolves**: 205 forms, 255
records, 55 distinct names, zero unresolved. Verified by eye at all three depths -- the MoE logo
(24bpp), the Western States logo (8bpp paletted) and an officer's signature (1bpp).

Ported to C# so the pipeline stays self-contained and deterministic: `DecodeLog` plus a minimal PNG
writer (deflate in a zlib wrapper, hand-rolled CRC32 and Adler32). Images are emitted **before** the
text so artwork sits behind it, as shading does. An unresolved or undecodable image is reported on
stderr rather than skipped silently, because a missing logo is invisible to every gate.
`tools/logdecode.py` is the same decoder standalone, for inspecting assets.

### The harness cannot check any of this

**FAP2PDF embeds no images at all.** 22 swept forms declare `G,` records and their legacy PDFs contain
**zero** embedded images. So the PDF channel is not a valid oracle for artwork -- the same limitation as
fragments (section 12) -- and the gates are unchanged at 270/283 because they cannot see the difference
either way.

Two consequences worth stating plainly:

1. **Image fidelity needs visual sign-off**, not a gate. `MC1690C` now renders its signature above
   "Authorized Representative"; that was confirmed by rasterising our own output and looking at it.
2. **Sections 5 and 21 ranked images as the top fidelity blocker on the strength of scores from a
   harness that could not see them.** They were never affecting those numbers. The real non-text
   blockers turned out to be shading, `M,PX` rules and box sizing -- all found only once a gate existed
   that could see non-glyph ink.

---

## 29. A diagnostic for the wrong-font blind spot (2026-08-23)

Section 27 found that **nothing measures glyph identity**. `tools/glyphshape.py` closes that, with an
important qualification about what it can and cannot be.

### How it works

For each glyph Tier 2 can match by (page, char, position), crop it from both rasters and reduce each to
a 16x16 ink bitmap **normalised to its own ink bounding box**. That normalisation is the whole trick:
comparing the rasters where they sit measures REGISTRATION, not shape, and at 200dpi half a point of
drift destroys it -- measured, the accepted forms scored **6-10%** that way before normalising.

### It is a diagnostic, not a gate

Per-glyph agreement is genuinely noisy at small sizes: on `P0010G` only **72% of CORRECT glyphs** clear
the per-glyph cut. A pass/fail built on that would fail work that is fine, which is the failure mode
this project has disqualified metrics for four times. So it reports:

- **per-form median** shape agreement -- accepted forms sit at **0.79-0.86**;
- **suspect characters**, whose median agreement is below 0.50 over 3+ occurrences. That is the shape a
  wrong *face* takes: every glyph of it disagrees at once, which survives the per-glyph noise.

### Validated for sensitivity, not for passing

Per the rule from section 20, the question for a new check is whether it CAN fail. Comparing each
legacy glyph against a **different character's** crop drops `EB2410A` from **92.2% to 13.8%**, so it is
reacting to shape rather than to whether a match was found. `--selftest` reproduces it.

The first attempt failed this test outright -- 92.2% normal against 93.8% "swapped" -- because the
remap made glyphs unmatchable rather than mismatched, so they were skipped instead of scored. **A
sensitivity test can itself be wrong**, and a self-test that cannot fail is worth as little as a gate
that cannot fail.

### Two limitations, both measured

1. **A suspect character is a lead, not a defect.** `G2425B` flags `'w'` at 0.27 over 12 occurrences and
   it is an artefact: legacy's character bbox is 2.7pt taller than ours, so the crop catches ink from
   neighbouring lines.
2. **It cannot see a face used for only one or two glyphs per form** (`MIN_CHAR = 3`). The DocuDings
   defect that motivated the tool sat right at that edge -- roughly three glyphs per form. Honest
   conclusion: this would probably have caught it, but not comfortably.

---

## 30. The glyph diagnostic nearly reported five wrong-font defects that were not there (2026-08-23)

Section 29's first working version put five forms at a per-form median of **0.31-0.37** -- `CL0164a`,
`M7450B`, `IM74004O`, `IM75506O`, `M7013A` -- against a population median of 0.84. That is exactly the
level a wrong FACE produces, and with thousands of glyphs each it looked conclusive.

**Their fonts were correct.** Checking before writing it up: legacy Helvetica against our Arial, the
deliberate and correct channel substitution, on every one.

The cause was the crop. Glyphs were being cut out at the character bbox PyMuPDF reports, and the two
renders disagree about line-box height -- legacy puts a lot of text in an 11pt box where we use 10pt.
The taller crop reaches into the neighbouring line, so the ink bounding box it normalises to is not the
glyph's. Cropping from the **baseline** instead -- `base - 0.80*size` to `base + 0.25*size` -- fixes it:

| form | bbox crop | baseline crop |
|---|---|---|
| M7450B | 0.31 | **0.83** |
| CL0164a | 0.31 | **0.83** |
| IM74004O | 0.32 | **0.83** |
| EB2410A (accepted) | 91.5% | **98.3%** |
| P0010G (accepted) | 69.9% | **99.5%** |

Sensitivity is unchanged -- comparing against the wrong glyph still scores 15.1% -- so the fix removed
noise, not discrimination. It also explains the `'w'` artefact recorded in section 29.

### Final distribution over 249 forms

| | |
|---|---|
| per-form median shape agreement | **0.85** (p5 0.80, max 1.00) |
| forms below 0.60 | **2**, and both have only ~20 comparable glyphs |

So glyph identity is in good shape across the library, and DocuDings (section 27) was the only real
wrong-face defect.

### Why this is written down

**Six times now a metric in this project has reported a defect the render did not have** -- ink IoU,
word-level diffing, reading-order comparison, run-level Tier 2, the field-region filter, and now this.
The difference is only that this one was caught before it became a work item, by checking the fonts of
the accused forms rather than trusting a striking number. A new measurement's first surprising result
is more likely to be a bug in the measurement than a discovery.

---

## 31. Measuring the ink we INVENT (2026-08-23)

The non-text ink gate scored **recall** only -- how much of the legacy's non-glyph ink we reproduce.
That is half the question. Ink we draw and Documaker does not cannot move a recall score at all, so
spurious box edges were unmeasurable, and any change that draws *less* could only ever look like a
regression.

`nontextink.py` now reports **precision** alongside: how much of OUR non-text ink the legacy also has.
Recall still gates; precision is a diagnostic.

### Image regions are excluded from precision

They had to be. FAP2PDF embeds no images (section 28), so every pixel of correctly-rendered artwork
counted against us -- the first run's lowest-precision forms were almost entirely image-bearing ones
being penalised for drawing the logo. Excluding the `G,` rects moves the median from 92.3% to **96.3%**
and makes the tail meaningful. Same reasoning that makes FAP2PDF an invalid oracle for fragments.

### What it found

| | |
|---|---|
| precision median over 158 scored forms | **96.3%** |
| forms notably over-drawing | ~5 |

`QCPP_CP6_A` is the clearest, at 10%. Its `X,(0,600,458,19800),(25,25)` record is a wide 13.7pt box, and
**legacy draws only the two VERTICAL edges** -- no top or bottom. We draw all four, so two 575pt
horizontal rules are pure invention, and they dominate the page's non-text ink.

### The `X,` second group is only partly decoded

Across the sweep, `X,` rectangle records render as:

| group | n | outcome |
|---|---:|---|
| `(24,24)`, height <= 430 | 79 | rules, 73/79 (92%) |
| `(20,20)` | 24 | rectangle, 23/24 |
| `(15,15)` / `(33,33)` / `(36,36)` | 46 | rectangle |
| `(25,25)` | 83 | **genuinely mixed** -- rectangle, top rule, bottom rule, or verticals-only |

`(24,24)` at 92% would be a defensible rule on the same footing as the `M,PX` one. **It was not adopted**
for a reason worth recording: the recall gate cannot reward it -- removing spurious edges leaves recall
unchanged at best and slightly worse if the rule is wrong -- so before precision existed the change was
literally unverifiable. It is verifiable now, and is the obvious next thing to try on this axis.

`(25,25)` should be left alone until its mechanism is known; four different outcomes over 83 records is
not something to threshold.

---

## 32. A 92% rule was not good enough (2026-08-23)

Section 31 measured `X,(24,24)` records no taller than one text line rendering as horizontal rules
**73 of 79 times (92%)** -- the same accuracy as the shipped `M,PX` rule, on a comparable sample. With
precision now measured, the change was finally verifiable, so it was implemented.

**It measured worse and was reverted:**

| | before | after |
|---|---|---|
| non-text ink RECALL (the gate) | **159/163** | 156/163 |
| green on every gate | **270** | 267 |
| ink precision (diagnostic) | 96.3% | 96.8% |

Three forms fell below the recall threshold to buy half a point of precision.

### Why 92% is fine for `M,PX` and not for `X,(24,24)`

**The failure modes are not symmetric.** The `M,PX` rule replaced a full rectangle -- 4 edges where
legacy draws 1 or 2 -- so being wrong meant drawing a spurious rule, which costs precision but never
recall. This rule replaces a rectangle with rules, so being wrong means **dropping vertical edges the
legacy does draw**, which costs recall directly, and recall is what gates.

The lesson is not "92% is too low". It is that **the accuracy a heuristic needs depends on which way it
fails.** A rule that errs toward drawing too much is cheap; one that errs toward drawing too little is
not, when the gate measures what is missing. Ask what the wrong answer costs before deciding whether
the hit rate is good enough.

`FapLine.Group` is kept, carrying the second group through the parser without interpreting it, so
whoever finds the real mechanism does not have to re-plumb it.

---

## 33. Scale validation at 1,000 forms (2026-08-23)

Everything in sections 18-32 was measured on 44, then 120, then 300 forms. Re-run at **200 per stratum
= 1,000 forms**, a fifth of the parseable library:

| | 300-form run | **1,000-form run** |
|---|---|---|
| rendered without error | 288/300 | **1000/1000** |
| green on every gate | 270/283 (95%) | **896/946 (95%)** |
| Tier 1 — nothing dropped | 271/271 | **913/913** |
| Tier 2 — within 3.0pt | 263/272 | **878/914**, median 99.6% |
| Tier 2 at the 1.0pt quality bar | 167/272 | 552/914, median 92.6% |
| non-text ink recall | 159/163 | **557/571**, median 100.0% |
| non-text ink precision | 95.3% | **97.3%** |
| glyph shape median | 0.85 | **0.85** |

**The results hold at 3.3x the sample.** Green stays at exactly 95%, Tier 1 stays perfect, and every
median is equal or better. Nothing here was fitted to the smaller samples.

**Zero render failures**, down from 12 of 300, because the harness now copies the Documaker fonts
(section 27).

### By stratum

| stratum | n | green | Tier 1 | Tier 2 | ink |
|---|---:|---|---|---|---|
| multipage | 200 | **194** | 200/200 | 199/200 | 118/123 |
| prose | 200 | 186 | 200/200 | 190/200 | 34/38 |
| images | 200 | 183 | 192/192 | 186/192 | 154/157 |
| grid | 200 | 168 | 179/179 | 168/179 | 131/132 |
| fields | 200 | 165 | 142/142 | 135/143 | 120/121 |

`grid` and `fields` are the weakest, which is consistent: grids carry the undecoded `X,` edge semantics
(section 31) and field-heavy forms carry the most unpopulated-field chrome. `multipage` being strongest
is worth noting given that section 5 listed multi-page forms as a parity blocker -- they are not.

### The 50 failures

| cause | n |
|---|---:|
| placement (intra-run drift, section 25) | **36** |
| non-text ink | 14 |
| content | **0** |

So after everything: **no content defects anywhere in 913 scored forms**, and three quarters of the
remaining failures are the one horizontal-drift defect that five separate attempts have failed to fix.
