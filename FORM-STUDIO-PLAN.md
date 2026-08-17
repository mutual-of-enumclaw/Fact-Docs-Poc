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
