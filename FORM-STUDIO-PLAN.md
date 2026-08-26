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

---

## 34. The M,PX edge rule at 1,000 forms, and one more refuted hypothesis (2026-08-23)

Re-measured on **557 records across 87 forms** (was 164 across 23 in section 26):

| | section 26 (n=164) | **1,000-form run (n=557)** |
|---|---|---|
| bottom edge drawn | 145/164 (88%) | **521/557 (94%)** |
| top edge, height < 420 | 15/94 (16%) | 29/289 (**10%**) |
| top edge, height >= 420 | 57/70 (81%) | 221/268 (**82%**) |
| **both edges correct** | 132/164 (80%) | **473/557 (85%)** |
| top edge alone correct | 136/164 (83%) | **481/557 (86%)** |

So the rule is **better than section 26 concluded**, not worse -- 85% rather than 80%. The larger sample
moved it up, which is the opposite of the usual direction and worth recording as such.

### Height is a proxy, not the mechanism

Globally the rule is non-monotonic: heights 427, 499, 827 and 971 draw a top; 360, 411, **507 and 575**
do not. 499 draws one and 507 does not.

But **within a form it is clean.** Of 59 forms with mixed behaviour, height fully explains the split in
10 of the 12 examined -- e.g. `IM70124O` is 0/2 at height 360 and 3/3 at 427, and `IM7213OM` is 0/2 at
411 and 1/1 at 499. The *threshold* moves between forms.

### Hypothesis: the threshold is relative to the form's line height. REFUTED.

If the boundary were "taller than about 1.3 line heights", it would explain a per-form threshold
mechanically and be computable at conversion time with no oracle. Taking the line height from the
enclosing `M,H` record's font tuple and testing height/lineheight:

| rule | correct |
|---|---|
| top iff ratio >= 1.15 … 1.40 | **45%** |
| top iff ratio >= 3.8 (where the data actually splits) | 84% |
| **top iff absolute height >= 420 (shipped)** | **86%** |

The ratio is worse than the crude absolute threshold at every cut. Either the `M,H` line height is not
the right quantity or the last-seen `M,H` is not the enclosing one; either way the hypothesis is dead
and the absolute rule stands.

That is now the **seventh** mechanistic hypothesis on the geometry axes to be raised and then refuted by
measurement -- after the H record, the font mix, per-form calibration, three whole-run width models, and
this. The pattern is consistent enough to be worth planning around: on this codebase, expect a plausible
mechanism to fail, and build the measurement before the fix.

---

## 35. A sub-point rectangle is a rule (2026-08-23)

The renderer treated a record as a horizontal rule only when its height was under **0.01pt**. Anything
thicker became a bordered box -- so a 15-unit (0.45pt) `X,` record was drawn as a box whose 0.5pt
borders collapse into **two hairlines with a gap**, where Documaker draws **one solid bar**.

Measured on `F9950B`: legacy emits `f (53,80)-(307,80.4)`, a single filled 254x0.4pt bar. We emitted a
0.45pt-tall box. The threshold is now **1.0pt**, comfortably below any real box in the library and
above every bar.

### Result, and an explicit trade

| | before | after |
|---|---|---|
| **non-text ink PRECISION** (median, 571 forms) | 97.3% | **100.0%** |
| non-text ink recall (the gate) | 557/571 | 553/571 |
| green on every gate | 896/946 (95%) | 893/946 (94%) |
| Tier 1 / Tier 2 / `.gd` suite | unchanged | unchanged |

**The gate went down and the change was kept anyway.** That deserves justification, because two earlier
changes were reverted on exactly this signal (sections 25 and 32).

The difference is that those were *guesses* about undecoded semantics that happened to hurt the gate.
This one is **demonstrably correct**: the legacy drawing list shows a single filled bar, and we were
drawing two hairlines. And every one of the five regressions is a `PSCP-*` / `PSUM-*` **composable
fragment** -- precisely the class section 12 established FAP2PDF is not a valid oracle for. On those,
total non-text ink is tiny (a 13pt-tall page), so a sub-point edge offset moves the percentage by 18
points without anything being missing: `PSCP-GLT`'s four drawings match legacy's four to within 0.2pt.

So: a broad, real improvement across 571 forms, against five scores from an oracle that cannot validly
judge those forms. Recorded here rather than presented as a clean win, and easy to revert (the constant
is `RuleMaxThickness`) if that trade is judged wrong.

---

## 36. Products reviewed the failures, and it changed the gate (2026-08-23)

A review pack of 21 forms went to Products. Three verdicts came back, and they are now the project's
ground truth -- worth more than any further metric tuning.

| form | verdict |
|---|---|
| `BAN01`, `BANSPECH` | "look identical to my eyes" -- Tier 2 rates them 45.5% and 42.2% |
| `IM74561R` | "missing lines in various places" -- Tier 2 rates it 48.2%, and Tier 1 called it CLEAN |
| `M7902AA` (follow-up) | boxes drawn where legacy underlines column headers |

### 1. Tier 2 measures the wrong quantity

No tolerance reconciles Tier 2 with the human: `BAN01` and `BANSPECH` do not reach 90% even at **8pt**,
more than a line height. The drift accumulates *across* a line -- correct baseline, correct text,
correct breaks, but a word late in a long line sits several points right. A reader does not see it.

`tools/lineplace.py` scores per line instead: same text (so line BREAKS are checked), same start x,
same baseline; blind by construction to drift inside a line. It agrees with **all six** human verdicts
and passes all four August acceptances. 794/807 at scale, median 100%. **Offered as a replacement for
Tier 2 as the placement gate; not swapped in unilaterally, because that changes what we claim.**

Building it took six rounds of threshold tuning that each traded one grouping artefact for another --
an 18pt column gap chopped justified prose, 72pt missed a 30pt column gap, a per-page band measured
worse, a superscript attached to the wrong neighbour. The fix was to stop tuning the grouping and make
the MATCH tolerant of it: if a legacy line's text is contained in our lines sharing that baseline, it
counts. Failures went 33 -> 13.

One real bug found in the grouping: the column-gap test compared item **starts**, not end-to-start, so
a table-of-contents row split because `LOSS CONDITIONS` at x=54 and its adjacent dot leader at x=149
read as 95pt apart when they touch.

### 2. Tier 1 had a gate that could not fail

`IM74561R` drops **808 underscores** and Tier 1 reported it clean. The fill allowance excuses missing
fill once our render carries every one the FAP declares -- and this form declares **zero** underscores
and **zero** field records, so `0 >= 0` was trivially true and excused all of them. Fixed by requiring
a reason for fill to exist: the form must declare at least one field.

An hour later the same class of bug appeared again in a leader-collapse regex that **deleted** leader
runs instead of collapsing them -- which would have re-hidden this exact defect, while the comment
claimed the opposite. Both were found by asking what the check could not fail on.

Mechanism decoded: **`M,P1` is a tab stop whose third field is a leader character** (95 = `_`, 46 =
`.`). Documaker fills to the stop with it; the FAP contains no underscores at all. `A,X1` turned out to
be an audit label ("BOX #N") that follows every `M,PX`, not content. Only 24 of 4210 forms use a
leader, and reproducing it needs the text-flow model this design avoids, so it is scoped and deferred.

### 3. The ink gate rewards over-drawing

`M7902AA` draws a rectangle around each column header where legacy underlines it. **Clearly visible,
and no gate objected** -- four edges are guaranteed to cover legacy's one, so over-drawing scores
perfect recall. Section 32 had reverted a similar rule purely on that signal, which was optimising the
wrong thing.

Re-measured properly: an `X,` record in group `(24,24)` or `(25,25)` sharing its page, group and top
row with **three or more siblings** renders as rules, not a rectangle, in **324 of 333 drawn cases
(97%)**. At exactly two siblings it is only 91%, and a **lone** record in those groups is a rectangle
58-80% of the time -- so the sibling test, not the group, carries the signal. Implemented at >=3.
Ink recall 553 -> 538/571, precision median 100%, and the header underlines now match legacy exactly.

### What this says about the whole exercise

Twenty-plus emitter changes were validated against 1,000 forms by machine before a human looked at any
of them. That review found **one gate that could not fail, one gate biased toward the wrong answer, and
six "failures" that were my own metric's bugs** -- and it cost one person a few minutes of looking.

The lesson is not that the metrics are bad; they found every one of the real defects fixed in sections
18-35. It is that **a measurement system calibrated against four forms cannot be trusted to tell you
when it is wrong about the fifth.** Widen the human-validated set before trusting a threshold, not
after.

## 37. The thirteen failures were three things, and only one of them was a defect (2026-08-23)

Section 36 left a list of thirteen line-placement failures in three classes. Re-derived from
the artefacts, the list was wrong in every class, and the one real defect it was pointing at
was somewhere else entirely -- in the shared FAP parser, affecting 1,340 of 4,478 forms.

### 1. "Missing space at a run boundary" was a misreading of the metric's own output

`lineplace.py` prints the LEGACY line in its `misses` string. `EB 99 09 06 16Includes
copyrighted material` is what LEGACY says, not what we say -- FAP2PDF sets the ISO copyright
notice hard against the edition date. Nothing was missing there.

And line placement **cannot** fail on a lost space: `collapse()` strips all whitespace before
comparing. Deleting one space glyph from our render and closing the gap (scratch:
`spacetest.py`) leaves it at 100% on EB2410A, EB22489Q, P0010G and A0238C. The handoff's claim
that line placement is the only gate that catches a lost space was exactly backwards.

### 2. Five of those six forms fail for a decoded and different reason: the page number

`EB9909SCHEDA` and the four `EP99xxSCHED*` forms declare, verbatim,

```
T,(24950,17235,25262,18915),(14110,392,225,312),12,Page      of
```

and legacy renders that literal **plus two separately positioned digit runs overlaid on it** --
on `EB9909SCHEDA` the current-page `1` at x=545.6, inside the six-space run, and the total at
x=570.0, which is 2.55pt PAST the declared box's right edge. It is the printer driver, not the
form: `FSISYS.INI` declares `PageNumbers = Yes` under `< PRTTYPE:PDF >`, and the FAP contains
no digits at all. 16 of 4,210 forms use the construct.

Two things follow. First, x-ordered extraction reads legacy as `Page      of` + `1` + `1` =
`Pageof11`, so **supplying the value would not fix the gate**: emitting "Page 1 of 1" as one
run keys as `Page1of1`. Matching legacy means reproducing the overlay, not the semantics.
Second, the geometry is only half decoded. The current-page digit sits a constant 28.6pt from
the text start on all seven renders measured; the total-pages digit sits 53.0pt out on the six
forms whose box starts at col 17235 and 55.4pt on `EF0450SCHEDA` at col 16310, and neither
col1, col2 nor the H-record margin accounts for the 2.4pt.

So it stays deferred, with the same shape as the tab leaders: a Documaker feature covering
~0.4% of the library that needs a model this design does not have. What changes is that it is
now deferred for a stated reason instead of mislabelled.

### 3. The grouping fallback was reading its own grouping, twice over

The remaining four failures -- `IM7213OM` and the whole three-form `M7902AA` / `FM7902AB` /
`M7902ABa` "two-line header cell" class -- were artefacts of the fallback added in section 36.
Two bugs, same root: it compared against our already-grouped LINES.

* **The band anchor.** A band is keyed by the y of whichever span opened it. Legacy bands
  `Location` (y 287.2) apart from `Covered` (289.8) because they are 2.6pt apart, over
  `LINE_BAND`; we band them together at 2.25pt, under it. So our band's key y was 287.25 and
  legacy's line was 2.55pt away -- outside `BASE_TOL` -- while the span it actually corresponds
  to sat 0.3pt away. Three forms failed on a 0.3pt difference.
* **Band order is not reading order.** `1. Partial Loss -- If a loss is a partial loss,` puts
  the bold `Partial Loss` 0.75pt off its neighbours' baseline in our render (Chromium rounds
  the baseline to whole device pixels and the two faces round opposite ways). Joining segment
  keys in band order gave `1. -- If a loss...` + `Partial Loss`, so the legacy text was not a
  substring of it and the line read as changed.

Fixed by not grouping at all in the fallback: `lines()` now also returns the raw spans, and the
fallback sorts the spans on that baseline by x, which reconstructs the line as it reads.

### 4. ...and the fallback had quietly thrown away the start-x check

Found by asking what it could not fail on -- the third time that question has found something.
"Is the legacy text contained in this band" verifies content and baseline and nothing else, so
**shifting every glyph on page 1 three points right scored 100%** on EB2410A, IM7213OM and
M7902AA. The primary match checks start x; the fallback was rescuing everything the primary
match rejected for having the wrong x.

Fixed by anchoring: the match must BEGIN at one of our spans sitting within `START_TOL` of the
legacy line's start, and the legacy key must be a prefix of the concatenation from there on.
Only where the line ENDS is given up. The same 3pt shift now scores 13% / 0% / 0%.

Sensitivity, measured rather than assumed (scratch: `sensitivity.py`):

| damage to our render | EB2410A | P0010G | M7902AA | IM7213OM |
|---|---|---|---|---|
| baseline | 100% | 100% | 100% | 100% |
| one glyph removed | 98.1 | 99.9 | **89.5** | 98.2 |
| one space removed | 100 | 100 | 89.5 | 100 |
| 10 spans removed | **81.5** | 99.7 | **52.6** | **84.2** |
| 60 spans removed | **55.6** | 99.2 | **0** | **17.5** |
| page shifted 3pt right | **13.0** | 94.3 | **0** | **0** |
| page shifted 3pt down | **13.0** | 95.1 | **0** | **0** |

P0010G is 1,542 lines, so a percentage gate is inherently insensitive on it -- 60 lost spans
cost 0.8 points. That is a property of the threshold, not of this change, and it argues for a
per-form absolute floor alongside the percentage.

`tools/defectzoom.py` had its own copy of the match and was therefore zooming to regions the
gate had already forgiven. Both now call `lineplace.unmatched`.

### 5. The real defect: the parser was deleting leading whitespace from every text token

`FormFileClient.ParseMTTLine` did `after[(ci + 1)..].Trim()`. A FAP token's leading whitespace
is **layout**: Documaker positions the run at col1 and lets the spaces push the first glyph
right. `EF9975A` declares

```
M,TT,(18085,3317,18389,4285),(14010,376,225,304),6,  From
```

-- box starting at col 3317 = 99.51pt -- and legacy sets `F` at 105.045pt after two space
glyphs. We set it at 99.504pt: the whole word 5.5pt, more than half a character, to the left of
where it belongs, immediately after `Period:`.

**11,599 records across 1,340 of 4,478 forms**, plus 427 `T,` statics across 223. And no gate
could see it. Tier 1 strips whitespace for its headline test; line placement collapses it and
only checks the line's start x, which is the box edge and therefore correct; the token still
carries the right characters; and the ink gates measure recall of rules and shading, not glyphs.

It surfaced from a *diagnostic*, not a gate. Tier 1 reports a same-characters-different-extent
difference as `SPACING+MOVED`, and 181 of 913 forms had one, which is why the class had been
dismissed as drift. Splitting that population by whether our token COUNT dropped separates the
two cleanly:

| SPACING+MOVED opcodes | n | min | p5 | median | p95 |
|---|---:|---:|---:|---:|---:|
| our token count FEWER (a merge) | 483 | -14.72 | -4.30 | **-3.00pt** | -0.73 |
| token count equal | 653 | -1.73 | -0.71 | **-0.20pt** | +1.33 |

A median of exactly -3.00pt with a mode at -3.0 (221 of 483) is one space at 10pt. The
equal-count population, median -0.20pt, is the intra-run drift Products calls invisible. Two
different phenomena had been pooled into one dismissed class.

Trailing whitespace is deliberately NOT restored: the caller trims the whole record before
parsing so it is already gone, and rebuilding it from the declared length would pad the 313,323
of 2,038,456 records that sit one character short. It carries no ink and every token is
absolutely positioned, so it changes only extracted text.

The `.gd` golden suite caught the change on three of ten forms and the deltas account for it
exactly -- `QTE_COVER_A` +1 char against 1 leading space in the FAP, `MCS90A` +24 against 24,
`M7215A` +56 against 56. Goldens recaptured. This is the fifth parser bug Form Studio has found
that was silently corrupting the `.gd` path too.

### 6. Result, against a prediction registered before the re-render

The full 1,000-form sweep was re-run so every `_ours.pdf` post-dates the binary (1000/1000 fresh,
checked at measurement time). The prediction written down first was: the merge population's
-3.0pt mode should collapse, and the equal-token-count population should NOT move -- if the
drift population moved too, the diagnosis was wrong about what it had separated.

| measure (913 forms both sides) | before | after |
|---|---|---|
| **Tier 1 gate -- nothing dropped** | 912/913 | **912/913** |
| forms reporting `SPACING+MOVED` | 181 | **4** |
| forms at 100% word match | 654 | **788** |
| character stream identical incl. order | 768 | **778** |
| **Tier 2 gate (3.0pt)** | 878/914 | **898/914** |
| Tier 2 at the 1.0pt quality bar | 552/914 | **619/914** |
| mean word match | 99.66% | 99.85% |
| lost-whitespace merges, n / median / min | 483 / **-3.00pt** / -14.72 | 34 / **-0.83pt** / -1.40 |
| forms with a merge >= 1.4pt short | 181 | **0** |
| drift population (token counts equal), n / median | 653 / -0.20pt | **647 / -0.22pt** |

The merge population lost its -3.0pt mode entirely -- the worst residual is now -1.40pt, under
half a space -- and the drift population did not move, which is the half of the prediction that
could have refuted the split.

**Tier 2 is the independent confirmation.** It scores absolute glyph positions, so it had no way
to distinguish this defect from the drift it already tolerated -- and it gained 20 forms at the
gate and 67 at the 1.0pt bar from a change it could not have named. That is the difference
between a metric that cannot see a defect and one that cannot ATTRIBUTE it: Tier 2 was measuring
the error all along, pooled into a quantity Products had told us to ignore.

The four forms still reporting `SPACING+MOVED` are all under 2pt:
`BPT0003A` differs by one or two dots of a leader, `MCS90B`/`MCS90D` and `FP00909N` by 1.5-1.7pt
of ordinary drift.

Line placement is **798/807, median 100%**, up from 794 -- entirely from the metric fixes above;
the emitter change cannot move it, because it is blind to whitespace by construction. The nine
remaining failures are two classes, both real, both deferred with a decoded mechanism:

| class | n | forms |
|---|---:|---|
| Missing tab-leader fill (`M,P1` leader char) | 4 | `IM74561R` `IM79014O` `M7208A` `IM74054O` |
| Page-number overlay (`PageNumbers = Yes`) | 5 | `EB9909SCHEDA` `EP990{7,8,9}SCHED*` `EP9910SCHEDC` |

`.gd` content gate 10/10 with no vacuous passes. `.gd` golden suite green on all ten.

One process note worth keeping. The first subset reading of this reported `1/1 forms pass` and
`merged: none`, which looked like a flawless result. Passing 303 form names through Git Bash had
silently delivered only the last one, and neither tool said how many forms it had been ASKED
for -- the same shape of defect as a gate that cannot fail, arriving as a false positive instead
of a false negative. Both tools now accept `@file-of-form-names` and print the requested count
alongside the scored one. And an intermediate subset reading suggested the drift population had
dropped six-fold; the full sweep shows it unchanged. **A subset of a stratified sweep taken in
sweep order is not a sample of it.**

### 7. The validation set was not in the sample it was validating

Checking the accepted forms against the fresh sweep turned up something worse than any of the
above: **`EB2410A` and `EB22489Q` are not in the stratified sample at all.** The strata are drawn
from construct counts and have no reason to include any particular form, so two of the four
forms Products accepted in August had `_ours.pdf` files rendered by the PREVIOUS build -- and
every gate reported on them from that stale artefact, including the sensitivity table in part 4
above.

This is the rule in the handoff ("never measure a fix without confirming the artefact is newer
than the binary, at the point of MEASUREMENT") firing on the one set of forms where it matters
most. The freshness check itself was being run against `fidelity-sweep.csv`, which lists what
the sweep covered -- so it reported 1000/1000 fresh and was right, and still missed this,
because the question "is everything I measured fresh" is not the question "is everything I must
measure covered".

Fixed structurally rather than noted: `sweep.py` now carries an `ACCEPTED` list -- every form a
human has given a verdict on -- and pins any of them the strata did not pick into the sample.
On the current inventory that adds exactly `EB2410A` and `EB22489Q`.

Re-rendered and re-measured, the accepted set after the leading-space fix:

| form | line placement | Tier 2 @3pt | Tier 2 @1pt (was) | Tier 1 |
|---|---|---|---|---|
| `EB2410A` | 100% | 100.0% | 94.3% (92.4) | clean |
| `EB22489Q` | 100% | 99.2% | 94.3% (94.3) | clean |
| `A0238C` | 100% | 90.6% | 74.1% (74.1) | clean |
| `P0010G` | 100% | 100.0% | 98.8% (98.7) | clean |
| `BAN01` | 100% | 45.5% | 23.0% | clean |
| `BANSPECH` | 100% | 42.2% | 20.5% | clean |

No regressions, and **`BAN01`/`BANSPECH` are unmoved at 45.5% and 42.2%** -- so the open decision
in the handoff still rests on exactly the disagreement it did before. Nothing about the
leading-space fix reconciles Tier 2 with the human verdict on those two.

### What this says about the whole exercise, again

Section 36's lesson was that a metric calibrated on four forms cannot tell you when it is wrong
about the fifth. This is the next one: **a classification of failures is itself a measurement,
and nobody had asked it to prove itself.** Three of the thirteen classes were wrong -- one from
misreading which side of the comparison a string came from, one from a mechanism nobody had
looked up, one from the metric's own grouping. The work queue was wrong for a day.

And the defect that mattered was never in the failure list at all. It was in a diagnostic that
181 forms reported and that had been written off as drift, because nobody had split the
population. **Look at what your dismissed classes are made of.**

The last one is the sharpest. Every gate here is validated against six forms a human has looked
at, and **two of those six were not in the sample being measured** -- so for one build the
validation set was being read off renders from the build before. The freshness rule was followed
and reported 1000/1000. It answered the question it was asked. **A coverage check and a freshness
check are different checks, and this project had only ever written the second one.**

## 38. A vector gate, a dead end closed for good, and 911 missing underlines (2026-08-24)

Section 37 left the ink question open: 57 forms below 90% precision on the non-text ink gate, and
the lone `X,` record still undecoded at "58-80%". Chasing the first led to a new instrument, the
instrument closed the second permanently, and on the way it found a defect nobody had looked for.

### 1. The ink precision tail is mostly the text mask, not the render

`G2032C` scores **4.6%** precision. It is not drawing twenty times the ink. `nontextink.py`
rasterizes both renders and subtracts a dilated text mask, and on that form the mask removes
**98.9% of LEGACY's rule ink and only 72.9% of ours** -- legacy's rules sit within a pixel of the
text boxes and ours land 0.3pt clear of them. The number is measuring which side of a text-bbox
edge each render happened to fall on. Gating on that tail would have been gating on the mask.

That is the fourth measurement artefact in three sections, and the common cause is now clear
enough to name: **every gate so far has compared RASTERS, and a raster forces a threshold and a
mask.** So `tools/vectorrules.py` compares the PDF vectors instead. Both renders are reduced to
canonical axis-aligned edges and coverage is measured as LENGTH in both directions -- recall
catches dropped rules, precision catches invented ones. No raster, no threshold, no mask.

Length rather than edge COUNT, because the two renders legitimately segment the same rule
differently: Documaker draws one filled bar per edge, Chromium emits a whole bordered box as a
single stroked path, and a grid's shared horizontal is one long rect on one side and per-cell
tops on the other. Coverage by length is blind to all of that and still exact about what exists.

Validated against the human-reviewed set before being trusted, as the rules require:

| form | recall | precision |
|---|---|---|
| `EB2410A` | 99.9% | 100.0% |
| `P0010G` | 99.6% | 99.2% |
| `BAN01` / `BANSPECH` | 93.1% / 93.5% | 99.3% / 99.8% |
| `M7902AA` (the form Products objected to, since fixed) | 100.0% | 98.6% |

and shown able to fail, in both directions: removing rules takes recall to 0%, and inventing 10
or 40 rules takes precision to 37.3%, 13.8%, 30.5%, 10.3%, 2.7%, 0.7% on three forms. That second
direction is the one the raster gate never had.

At scale: **614 forms scorable, recall median 100.0% with 54 below 90%, precision median 99.9%
with 71 below 90%.**

### 2. The lone `X,` record is not undecoded -- it is undecidable from the record

`G2032C` declares fourteen `X,` records identical in every field the parser can see: same page,
same group `(25,25)`, same height 400, same columns 1800-18633, same line width, same style,
stacked contiguously. Legacy draws a horizontal edge for four of them and not for the other ten.

Generalised: bucketing lone records by their COMPLETE declared signature within a form, **19.3% of
signature groups with two or more records are drawn differently from each other** (11 of 57,
covering 53 records). No function of a single `FapLine` can ever separate those. Every threshold
fitted to the record's own fields -- height, width, group, line width -- was fitting noise, which
is why five attempts landed between 58% and 80% and none improved on the next.

The disagreements are not random, though. Every one is positional within a contiguous run:

    CU21556Q  TLR x1, LR x3, BLR x1
    G2032C    TBLR x1, TLR x1, LR x10, BLR x1
    FP04089N  TLR x2, LR x6, BLR x1

top on the first, sides on all, bottom on the last. So the unit Documaker draws is the STACK, not
the record. Measured directly, over runs of >=3 lone records sharing page, group and both columns
and abutting vertically:

| | |
|---|---|
| stacks found in the 1,000-form sweep | **13** (sizes 3,3,3,3,3,3,3,4,4,4,4,8,13) |
| left / right verticals drawn | 13/13 (100%) |
| outer top / bottom drawn | 9/13 (69%) |
| **internal** horizontals drawn | **7 of 45 (15.6%)** |

So the hypothesis is directionally right -- Documaker usually does not divide the cells, and we
draw every division. But 84.4% is **below the 92% rule section 32 already reverted**, with the
same failure direction (being wrong DROPS real edges), on 45 observations against that rule's
larger sample. And the outer horizontals cannot even be closed reliably at 69%.

**Not implemented, and it should not be attempted from the record again.** Deciding it needs the
enclosing table -- which records form one grid, and where its header and footer are -- and that is
document structure this design does not build. Recorded here so the next person spends the
afternoon somewhere else.

> **THIS CONCLUSION IS WRONG -- see section 39.** The records are not identical in every field;
> they are identical in every field THIS PARSER WAS READING. The `A,X1` annotation that follows
> each one carries an edge suppression mask, and honouring it takes rule precision from 65.1% to
> 99.8%. The evidence above is sound and the inference from it was backwards: the right reading of
> "identical inputs, different outputs" is that an input is being dropped.

### 3. What the vector gate actually found: 911 underlines we never drew

Sorting the sweep by recall turned up forms drawing **zero** rules where legacy draws hundreds of
points: `MPNIL01A` 604pt, `EB0116T` 530pt, `Certhdr` 303pt, `COM126C` 113pt, `AZPRIVNOT` 417pt.
None of them declares a single `X,` record.

On `Certhdr`, legacy draws four filled bars at y 98.2-98.8 whose x-extents are `65.2-143.8`,
`177.8-302.0`, `594.6-668.0`, `28.8-55.8` -- matching, to a tenth of a point, four text spans on
baseline 96.8: `Policy Number(s)`, `Recipient Name & Address`, `Document Type`, `Seq #`. It is
**underlined text**, and we rendered nothing.

The FAP declares it, on the `A,T1` annotation that follows the text record:

```
T,(2919,5933,3231,10077),(16010,392,352,312),24,Recipient Name & Address
A,T1," ",0,(0,0,0,0),1,0," "                     <- flag 1     underlined
A,T1," ",0,(0,0,0,0),1025,0," ",0                <- flag 1025 = 1024|1, underlined
A,T1," ",0,(0,0,0,0),1024,0," ",0                <- flag 1024, not
```

Bit 0 is the underline. Measured over 320,169 text records in the sweep:

| | n | legacy draws a bar under it |
|---|---:|---|
| bit 0 SET (excluding 1-character field placeholders, which never render) | 233 | **231 (99.1%)** |
| bit 0 CLEAR | 319,885 | 286 (**0.1%**) |

Both directions clear of the bar, and it is a **decoded flag rather than a fitted threshold** --
the first construct in this project settled by reading what the format declares instead of
correlating against the oracle. The 0.1% false positives are text that happens to sit just above
a real table rule; the two misses are one construct on two sibling forms.

**911 runs across 186 of 4,478 forms.**

### 4. Why nothing could see it

An underline sits *directly under the glyphs it belongs to*. So:

* `nontextink.py` dilates a text mask over both renders and subtracts it -- which removes the
  underline from legacy's side along with the text, exactly as it removed 98.9% of `G2032C`'s
  rules in part 1. The one gate that measures non-glyph ink is blind to ink that touches glyphs.
* Tier 1, Tier 2, line placement and glyph shape are all glyph-only. An underline is not a glyph.
* The `.gd` content gate compares characters, not formatting.

It took an instrument that reads vectors rather than pixels, and even then it surfaced as *recall*
-- ink legacy has that we do not -- rather than as anything anyone had gone looking for.

Emitted as `text-decoration:underline` on the run, not as a positioned bar: the underline belongs
to the text, so it must follow the run when the run is edited or rebound. Documaker draws its bar
across the run's own x-extent, which is what a CSS underline covers.

### 5. Result

Rather than a 100-minute sweep, `emit-html` was re-run over all 1,292 cached forms (seconds) and
only the 271 whose HTML actually changed were re-rendered through Chromium. That also PROVES the
blast radius instead of assuming it: **51 of the changed forms are in the 1,000-form sample --
exactly the 51 the FAP-side analysis predicted** -- and the other 220 were stale leftovers from
sweeps predating section 37.

| measure | before | after |
|---|---|---|
| **vector rules -- recall below 90%** | 54 | **22** |
| forms drawing ZERO rules where legacy draws some | 10 | **0** |
| forms at >= 90% both ways | 504/614 | **536/614** |
| recall improved / regressed | -- | **51 / 0** |
| Tier 1 gate -- nothing dropped | 912/913 | 912/913 |
| line placement | 798/807 | 798/807 |
| Tier 2 (3.0pt / 1.0pt) | 898 / 619 | 898 / 619 |
| lost token whitespace | 0 forms | 0 forms |
| **non-text ink recall (the raster gate)** | **538/571** | **538/571** |

`Certhdr` goes 0 -> 302pt against legacy's 303; `EF9933A` 0 -> 413 against 412. Every text gate is
unchanged, which is the correct answer: an underline adds ink and moves no glyphs.

**And the raster ink gate did not move by a single form.** Fifty-one forms gained real ink that
legacy has, and the one gate whose job is non-glyph ink scored exactly the same 538/571, with zero
forms improved and zero regressed. That is not an argument that the text mask eats underlines --
it is a measurement of it.

The accepted set was re-rendered and re-checked in full: `EB2410A` 100% line placement / 100.0%
Tier 2 / 99.9-100.0 vector, `EB22489Q` 100 / 99.2, `A0238C` 100 / 90.6, `P0010G` 100 / 100 /
99.6-99.2, `BAN01` 100 / 45.5 / 93.1-99.3, `BANSPECH` 100 / 42.2 / 93.5-99.8. Nothing dropped on
any of them.

**A sharper form of the freshness rule.** Three accepted forms came up "stale" by mtime after the
change, yet their HTML was byte-identical under the new build, so their PDFs were still correct.
The rule is really *does this artefact match what the current build emits*, and mtime is only a
proxy for that. Re-emitting is cheap and answers the real question directly; Chromium is what
costs the hour. Diff first, then re-render what differs.

### 6. The `.gd` path has the same gap, and it is now specified

`FapToGhostDraftGenerator` writes `\ulnone` on every run unconditionally, so all 911 underlines are
missing from the GhostDraft output too -- the `main` branch's deliverable. The `.gd` content gate
cannot see it, because it compares characters and an underline is not a character. Left alone here
rather than folded into a Form Studio change, but the flag is decoded and `FapStaticText.Underline`
and `FapTextToken.Underline` already carry it, so the fix is switching `\ulnone` to `\ul` on those
runs and recapturing the goldens. **Sixth** parser finding that crosses into the `.gd` path.

### 7. The underline fix was 87% right, and only looking at it showed the other 13%

Building a review pack meant opening the comparisons, and `MPNIL04A` showed the fix half-done:
legacy draws **two continuous bars** under the underlined sentence, we drew **ten, with a 7.4pt
hole at every word boundary**. Documaker underlines a RUN; our tokenisation splits a run into one
absolutely-positioned span per word, so a CSS `text-decoration` on each span underlines the words
and not the spaces between them.

Measured at the edge level:

```
legacy y=362:  141.6-293.6                     301.0-556.0
ours   y=362:  141.6-150.5  157.9-191.7  199.1-208.7  216.1-236.7  244.1-293.8
               301.1-350.6  357.9-377.9  385.3-422.0  429.4-450.5  457.9-480.6
```

Fixed by reassembling the run: consecutive underlined tokens sharing a baseline merge into one
bar when the gap between them is no wider than a word space at that size. The flag stays on the
span as `data-underline` so Layer B keeps the semantics; only the ink moves.

| form | recall before | after |
|---|---|---|
| `MPNIL04A` | 87.2% | **99.9%** |
| `EP9908D` | 92.0% | **99.9%** |
| `MPNIL01A` | 95.1% | **99.9%** |
| `Certhdr` | 99.6% | **100.0%** |

Nine forms improved, one moved 1.2pt the other way on sub-point endpoint noise (and gained
precision), and seven paid 1-3 points of precision. That cost is a **deliberate** trade and worth
naming: legacy leaves one gap on `MPNIL04A` open -- it breaks between "CONFLICT" and "BETWEEN"
across a 7.4pt gap while closing 7.4pt gaps on either side -- so gap width cannot be the whole
rule and merging draws one segment Documaker does not. One spurious segment against eight missing
ones is the right side of that trade, but it is a trade, not a clean win.

**This is the second thing today that no metric would have surfaced.** The vector gate scored
`MPNIL04A` at 87.2% and `EP9908D` at 92.0% -- numbers that read as "close enough" in a table of
614 forms, and both would have shipped. What found it was rendering the comparison and looking at
it. The gates are good at *ranking* and hopeless at telling you when 87% means "nearly right" and
when it means "visibly broken", because they have no model of what a reader notices.

The same pass caught the review pack itself about to ask a bad question: `PSUM_RVPD_DTL` was
selected into the sample on a 2.3% rule precision, but its legacy render has **0 characters and 2
drawings** -- FAP2PDF renders that fragment blank, so the score was entirely the oracle and a
reviewer would have been shown a blank page. The fragment filter matched the prefix `PSUM-` and
the form is `PSUM_RVPD_DTL`. A name-prefix list was the wrong instrument for that question and
always would have been: it encodes a guess about which forms render blank and is one naming
convention away from being wrong. Replaced with a check of the artefact -- if the reference has
almost no text on it, there is nothing to judge, whatever the form is called.

**Look at the output before shipping a fix, and before asking anyone else to look at it.**

### The lesson this time

Section 36 said a metric calibrated on four forms cannot tell you when it is wrong about the
fifth. Section 37 said a classification of failures is itself a measurement. This one is about
instruments: **four of the artefacts found in three sections trace to the same root -- comparing
rasters, which forces a threshold and a mask, and then attributing what the mask ate to the
render.** The underlines had been invisible for the entire project not because anyone reasoned
badly about them, but because every instrument in the kit destroyed the evidence before anyone
looked. A new KIND of measurement found in one afternoon what better tuning of the old kind had
not found in five sections.

And the dead end is worth as much as the find. "58-80%, genuinely undecoded" invited another
fitting attempt. "19.3% of identical records are drawn differently, so no per-record rule exists"
closes it.

The counterweight is part 7. A new instrument is still an instrument, and this one rated a
visibly broken underline at 87% and a worse one at 92%. Every gate here ranks; none of them knows
what a reader notices. **Opening the render caught two things in one afternoon that the whole kit
scored as nearly right** -- which is the same lesson section 36 learned from Products, arriving
this time from the other direction: not "the humans disagreed with the metric" but "the metric
would never have raised it at all".

## 39. CORRECTION to section 38: the edge choice was never undecidable (2026-08-24)

Section 38 concluded that which edges an `X,` record draws is **undecidable from the record**, on
the evidence that fourteen records on `G2032C` are identical in every field the parser reads while
Documaker draws a horizontal for four of them. That evidence was correct. The conclusion was
wrong, and the right inference from it was the opposite one: *then we are not reading every
field.*

Products supplied the correction. Shown the review pack, they reported "there are lines on our
copy where we would expect them to not be there", named five forms, and asked directly: **is
there a bit that determines where the line is visible that we have not identified?**

There is.

### The mechanism

Every `X,` record is followed by an annotation:

```
X,(7025,1800,7425,18633),(25,25),1,0
A,X1,"BOX ",0,(0,0,0,1),2,0
                        ^ 1 = hide TOP, 2 = hide BOTTOM, 4 = hide LEFT, 8 = hide RIGHT
```

It is a **suppression** mask: a bit SET means Documaker does not draw that edge. On `G2032C` the
fourteen masks run `2,2,3,3,3,3,3,3,3,3,3,3,1,1` -- the first two hide their bottom, the middle
ten hide both horizontals, the last two hide their top -- and legacy draws exactly the edges
those masks leave alone. That is why the SCHEDULE box is one open box and ours was thirteen ruled
lines.

Validated by simulating whole forms and comparing to the legacy vectors:

| | mask honoured | all four edges (the old behaviour) |
|---|---|---|
| rule precision, median | **99.8%** | 65.1% |
| forms below 90% precision | **1** | 65 |
| rule recall, median | 100.0% | 100.0% |

At the gates, after re-rendering the 150 forms it changed:

| measure | before | after |
|---|---|---|
| **vector rules -- forms below 90% PRECISION** | **71** | **0** |
| vector rules -- forms below 90% recall | 22 | **2** |
| vector rules -- passing both ways | 536/614 | **585/587** |
| non-text ink recall (the raster gate) | 538/571 | **562/571** |
| non-text ink precision below 90% | 57 | **13** |
| Tier 1 / line placement / Tier 2 | unchanged | unchanged |

Zero regressions over 1pt in either direction. Every form Products named went from 15-52%
precision to 99.5-100%, and the two whose *recall* was also broken -- `CR 35 21` at 79.3% and
`CA 21 34` at 81.6% -- came back at 99.9%. `CA 21 34`'s "strikethrough" was the same cause: we
drew the bottom edge where the mask says hide it and draw the top, 12pt higher.

### Three heuristics retired

The mask supersedes, and explains, all of these:

* **Section 23's height threshold** (`M,PX`: bottom always, top when >=420 units). It was fitting
  the average of the masks that happen to occur at each height.
* **Section 32's group rule**, reverted at 92% for dropping real edges.
* **Section 36's sibling-row rule** (>=3 side-by-side records are underlines, 97%). This one was
  *nearly* right for a reason: a row of side-by-side records **is** a row whose masks each hide
  three edges. It was reading the shadow of the mask in the layout.

Five attempts across four sections landed between 58% and 80% because they were fitting noise
around a field nobody had read.

### Two ways I got this wrong, both instructive

**Dismissing the line.** Section 36 looked at `A,X1`, saw `"BOX #NNN"`, and recorded it as an
audit label. The label *is* an audit label. The number beside it is not, and the record was
written off on the strength of the part that was.

**Testing the right hypothesis with the wrong polarity.** Asked whether a bit governed
visibility, I tested `bit set <-> edge drawn`, got 13-48% agreement across 901 records, and
reported it refuted. The same measurement read as `bit set <-> edge HIDDEN` gives 87%, 73%, 82%,
84% -- and every single mismatch was one-directional (legacy drew an edge the mask called hidden,
never the reverse), which is the signature of a shared edge drawn by the neighbouring box. A
one-sided error distribution is evidence about the model, not noise to average over. Whole-form
simulation then gave 99.8%.

The second mistake is the worse one. An inverted-sign refutation looks exactly like a real
refutation, and it closed a line of enquiry that a domain expert had opened correctly.

### What this says about the whole exercise

Sections 36-38 each landed on a version of "the metric was wrong". This one is different and
sharper: **the FILE was more informative than the model of the file, and no amount of measuring
the render would have revealed that.** Every instrument built here compares our output to
Documaker's output. None of them can tell you that the input contains a field you never parsed.
Nine sections of geometry work -- and a stated conclusion of "undecidable" -- rested on a parser
that silently dropped a per-record field, and the thing that broke it open was a person who knows
the forms asking whether a visibility bit existed.

Two concrete practices follow:

1. **Audit the parser against the format, not against the output.** Section 24 did a record-type
   audit and found `M,I` bullets. Nobody ever audited the FIELDS of the record types we do parse.
   `A,X1`'s number was in every one of 7,055 annotations in the library.
2. **When a heuristic stalls in the 60-80% band across several attempts, stop tuning and go
   looking for an unread input.** That band is what fitting an average of a hidden discriminator
   looks like.

### Still open

* `MC1690a` / `MC1690C` -- one missing 444pt horizontal, the only two vector-rule failures left.
* The `A,X1` COLOUR (the `(0,0,0,1)` group) is decoded as far as it needs to be: every non-black
  value sits on a shaded record, and the declared colour is NOT what Documaker paints. Section
  22's measured per-style table matches legacy exactly on all 25 records where both can be
  compared (0.75, 0.55, 0.85) and the declared colour would have regressed 21 of them, including
  painting a box white that Documaker paints at 0.85 grey. Investigated because it looked like a
  defect; it is not one. What the colour means is unknown -- plausibly an authoring-tool value the
  print driver ignores.
* The raster ink gate lost a little recall on four forms (`R2014B` -8.3, `A0431A` -4.3,
  `EP04453R` -4.2, `G3115A` -3.2). Not a regression: `R2014B` now draws exactly 13 edges against
  legacy's 13, median offset 0.38pt and max 1.12pt, and the raster gate's tolerance is ~0.96pt at
  150dpi. The over-drawing had been padding its recall, exactly as section 38 part 1 said it
  would. Worth remembering that removing an over-draw can look like a regression on a
  recall-biased instrument.

## 40. The binding format, read from 491 production templates (2026-08-26)

Products supplied the **ISO Commercial Auto Project (2607.0)** package: 491 production `.gd`
templates, the two concept libraries, `model.xml`, and the generated `GDXSD.xsd`. That is the
matched-pair input the section-39 handoff asked for, and it settles P3's first question --
*how does a field become a binding* -- from evidence rather than from a design proposal.

Two plans had previously been written for this and both were wrong in the same direction: they
invented a namespace. There was no need. The answer was in the package.

### 40.1 What a production template actually is

Three parts, stitched by integer IDs:

| part | content |
|---|---|
| `<content><rtf>` | the LAYOUT, carrying `%[ID]` markers |
| `<markup>...<instructions>` | the LOGIC, a tree of typed instructions keyed by those IDs |
| `model.xml` | the projection of concept GUIDs onto **Server XML element names** |

The whole instruction vocabulary, across all 491 templates, is five types and five part types:

```
fillPointType              14107   emit a value            <path>
conditionalInstructionType 10362   if / else               parts carry <path>
subscriptionType             885   include another .gd     document NAME
listInstructionType         691    iterate                 <pathToList> + <iterator>
annotationType               10    reviewer note
```

**A path is never a string.** It is `(rootNode + rootguid, pathNodes[name + guid])` against a
concept library. `model.xml` is what turns it into Server XML: every attribute carries the `id`
that becomes the element name, and every list carries the `elementId` that becomes the repeated
child under `Items`. So the resolution rule is

```
CA Auto Level Coverages > Autos with UIM Coverage - Washington    (list, guid path)
  Auto > Vehicle Description                                      (fill point, guid path)
      -> CAAutoLevelCoverages/Items/Auto/VehicleDescription
```

`tools/gdmodel.py` implements it; `tools/gdbindings.py` walks the templates.

### 40.2 It resolves, and an independent authority says so

**26,141 of 26,145 instructions resolve. 14,107 of 14,107 fill points resolve.** The four
holdouts are two list-position built-ins and two templates applying a list-level test from item
scope; they are reported, not swallowed.

Resolving is not the same as being right, and this is exactly the trap section 37 documents -- a
gate that cannot fail. So every resolved path is walked through `GDXSD.xsd`, which the package
generates from the same model by a **different code path** that `gdmodel.py` never reads:

| | |
|---|---|
| paths checked | 18,972 |
| present in the schema | **18,972 (100.00%)** |
| sensitivity selftest | 200 baselines accepted, **1,000 mutations all rejected** |

The mutations are drop-a-middle-step, rename-leaf, drop-the-`Items`-hop, reverse-path, and
extra-step-past-leaf. `tools/gdxsdcheck.py --selftest` runs them.

**The XSD found two real bugs before it agreed.** The first pass scored 96.32%, and both classes
of failure were mine:

1. **`elementId`, not `elementName`.** A list declares both, and they differ on **117 of 296**
   lists (`elementName="Additional Insured"` / `elementId="AdditionalInsured"`). Reading the
   display name produced 698 paths the schema rejects. On `Auto` the two coincide, which is why
   the matched pair passed while the library did not -- a single-form check would have shipped it.
2. **A mutex-group member test is an enum VALUE, not an element.** 75 of 1,905 tests belong to a
   `mutexTestGroup`. The GROUP is the element -- an enumerated string -- so `Policy > is Renewal`
   is `Policy/TransactionType == "isRenewal"`, and there is no element named `isRenewal`. The XSD
   states it as a `simpleType` restriction with seven enumerations.

Neither was findable from the templates or from `model.xml` alone. Both came from reading a
second authority, which is section 39's lesson applied on purpose rather than in hindsight.

Two shapes also needed the concept library, because `model.xml` does not project them:

* **`is provided`** -- 5,742 uses, ONE stable guid, declared in no file. A built-in predicate over
  the path resolved so far. Same family as `is First` and `has N or more elements`.
* **`contains the X`** -- a `<test>` in the `.gdm` carrying `selector="<guid>"`. Not a fourth
  shape: it is "any item matching selector X", and it resolves to that selector's boolean.

### 40.3 The matched pair: 13 legacy fields, 9 modern bindings

`A2134FN.DDT` against `CA 21 34 10 13` (which is **two** templates plus three subscriptions):

| legacy DDT field | rule and DB2 source | modern Server XML |
|---|---|---|
| `POLNUM` | `concat` PMSP0200 SYMBOL(3)+POLICY0NUM(7)+MODULE(2) | `Policy/PolicyNumber` |
| `INSNAME1` | `DAL CALL("Insured_Address_LongName")` | `Insured/PrimaryNamedInsured` |
| `EFFDATE` | `movedate` PMSP0000 TYPE0ACT=EN | `Policy/TransactionEffectiveDate` |
| `EFFDATE #002` | `movedate` PMSP0200 / PMSP0000 (NB,RB) | `Policy/EffectiveDate` |
| `AUTOSDSC` | `concat` ASB5CPL1 B5ANCD(6)+B5DCNB(30)+B5DDNB(30) | `CAAutoLevelCoverages/Items/Auto/VehicleDescription` |
| `BIINJ` `BIINJA` `BIINJB` | `noopfunc` / `movenum` / value->text table | `.../BodilyInjuryLimit` + `.../BodilyInjuryLimitWording` |
| `BIINJ2` `BIINJ2A` `BIINJ2B` | same, split limits | `.../BodilyInjuryandPropertyDamageLimit` + `...Wording` |
| `>XUnit1` | `move_it @GETRECSUSED` (unit iterator) | the `listInstructionType` |
| `BODINJ` | `hardexst` BYCZST=Y (presence test) | the selector + `is provided` |
| DDT filter `BYAGTX in (CA,FA), BYAOTX=UN, BYBCCD=WA` | the WHERE chain | the selector `AutoswithUnderinsuredMotoristsCoverage-Washington` |

Five things follow, and only the first is about syntax.

**(a) A value and its rendering are two separate elements, and this is a rule of the model, not a
quirk.** `X` and `XWording`. Measured across `model.xml`: **3,191** `*Wording` attributes have a
bare twin and only **6** do not, out of 10,229 attribute declarations -- and **36.2% of all
14,107 fill points bind the `Wording` half**. Legacy encoded the same distinction as *separate
FAP fields with different rules*: `movenum` with a `9.0,9.0,C` picture for the number, `noopfunc`
with a twelve-way `CSL 100 =100,000:...` table for the words. Three legacy fields collapse to two
modern elements because the *third* was a state variant, not a rendering.

**(b) Selection is a selector, not a field.** A DDT's DB2 filter chain becomes one named boolean
per list item. 422 selectors exist; each is an `xs:boolean` on the item class.

**(c) Concatenation and DAL functions move upstream.** Three DB2 columns become one attribute.
`CALL("Insured_Address_LongName")` becomes `Insured/PrimaryNamedInsured`. The transform does not
disappear -- it moves out of the form and into the section builder, which is precisely what
"never store a CDM path in a form" is for.

**(d) The map is many-to-one, and the DDT RULE is the unit of provenance, not the field.** 13
fields to 9 bindings. Any design that maps FAP field -> binding one-for-one is wrong before it
starts.

**(e) A form is not one template.** `CA 21 34` is `...Washington Underinsured Motorists
Coverage` + `...Schedule`, plus subscriptions to a header/footer and a schedule-overflow wording.
Across the package, **346 of 491** templates subscribe to another, 885 edges over 172 distinct
targets, and **all 172 resolve** -- 168 exactly and 4 only case-insensitively
(`wording for schedule overflow` / `WOrding for schedule overflow`), so GhostDraft's subscription
lookup is case-insensitive. This is the packet-assembly model the plan lists as open, already
specified: composition is by document NAME, not by path.

### 40.4 What the demand ranking says, and one cross-check that landed

Fill-point demand over 491 templates, by how many templates need the element:

```
305  Policy/PolicyNumber                              4006  PolicyDeclarations
297  Insured/PrimaryNamedInsured                      3031  CAAutoLevelCoverages
241  Policy/TransactionEffectiveDate                  2611  CALocationLevelCoverages
153  Policy/TransactionEffectiveDateWording           1189  CAPolicyLevelCoverages
 73  CAAutoLevelCoverages/Items/Auto/VehicleDescription  1159  Policy
 64  InsuranceCompany/Name                             472  Insured
```

`tools/bindgap.py` ranked the LEGACY demand independently, off the DDT, and put
`PMSP0200.SYMBOL` and `PMSP0200.POLICY0NUM` at the top, each blocking 1,276 forms. The modern
ranking puts `Policy/PolicyNumber` first at 305 of 491 templates. **Two rankings from disjoint
sources agree at the top.** That is the first external evidence that bindgap's backlog ordering
is sound -- its docstring warns the coverage *percentage* is a floor, and this does not change
that, but the ORDERING was the part it claimed was trustworthy and it now has a witness.

### 40.5 What this does and does not settle

Settled: the binding format, the resolution rule, the composition model, the value/rendering
convention, and a 2,139-path vocabulary of real Server XML paths with usage counts
(`output/gdbindings-ca2607.csv`).

Not settled, and not touched: **populating** one. The handoff is precise that "converts" and
"populates" are different claims and only the first is true. Nothing here renders a real policy.
The honest next test is still the one named in the handoff -- fetch a policy through the API,
build the Server XML, render a converted form -- and it is now much better specified, because the
target XML shape is no longer a guess.

Also unchanged: `FapToGhostDraftGenerator.LookupBinding` still matches on FIELD NAME, which the
DDT analysis showed is the unreliable key. Finding (d) says why that can never reach 100%: the
relation is many-to-one and the field is the wrong unit. The DDT rule is the right one.

### 40.6 Tooling

```bash
python tools/gdbindings.py <pkg> --form "CA 21 34 10 13 Schedule"   # one template, resolved
python tools/gdbindings.py <pkg> --out output/gdbindings-ca2607.csv # all 491
python tools/gdxsdcheck.py output/gdbindings-ca2607.csv <pkg>/GDXSD.xsd
python tools/gdxsdcheck.py output/gdbindings-ca2607.csv <pkg>/GDXSD.xsd --selftest
```

A `.gdsp` is a zip. Extract it under `output/iso-packages/<name>/` (gitignored) and point the
tools at that directory; they expect `model.xml`, `Templates/`, `Concept Libraries/` and -- for
the check -- `GDXSD.xsd` copied in beside them.

**Do not trust a single-form validation of a path resolver.** The `elementId` bug passed the
matched pair and failed 698 paths in the library.

## 41. Demand meets supply: what fact-docgen actually emits (2026-08-26)

Section 40 answered *how* a field becomes a binding. This is the second question of the
matched-pair route: *how the Server XML for that form is assembled*, and how much of the
demand the existing builders already meet.

### 41.1 Do not scrape the builders -- read what they produced

The section builders are 35 files of `new XElement("VehicleDescription", ...)`, which is exactly
the proxy this project has been burned by five times in one day (section 39's list). The
authority is the XML that was really generated: the fact-docgen integration tests write
`Builder.xml` and `Service.xml` per policy under `BuilderRenderOutput/BulkPdfComparison/`, and
there are **65 of each** from real policies.

`tools/xmlsupply.py` joins them to the demand CSV. It reports three states, because "present" is
not one thing:

| state | meaning |
|---|---|
| `supplied` | the element appears somewhere with a non-empty value |
| `empty-only` | the element appears, always empty |
| `missing` | never appears in the sample |

**Supply is a FLOOR, and for a sharper reason than bindgap's.** A policy's XML contains only the
sections its own coverages trigger; 65 policies cannot exercise 2,139 paths. So `missing` is
partly a statement about the sample. What is sound: the `supplied` set is exact (if it appears
with a value, the builder can produce it), and the *ranking* of unmet paths, because demand comes
from the package and does not depend on the sample.

The artefacts also confirmed section 40 independently, which was not the point of looking:
`<TransactionType>isRenewal</TransactionType>` is in every Builder.xml. The mutex-group-as-enum
decode was made from the XSD and is now witnessed in real output.

### 41.2 The measurement

```
DEMAND: 2139 distinct fill-point paths over 474 templates

state            paths            template-weighted
supplied           238   11.1%      2538   30.3%
empty-only         142    6.6%       594    7.1%
missing           1759   82.2%      5254   62.7%
```

The weighted column matters more than the path column: the paths that many templates need are
much likelier to be supplied than an average path, which is what you would hope -- 30% of demand
by template-usage against 11% of distinct paths.

### 41.3 Five root sections have no builder at all -- and that part is not about the sample

Separating the sample-independent claim from the sample-dependent one is the whole value of this
join. A root element absent from **all 65** artefacts cannot be explained by policy mix:

| demanded paths | root section | builder registered? |
|---:|---|---|
| 284 | `CommonState-SpecificPolicyLevelCoverage` | no |
| 245 | `CALocationLevelCoverages` | no |
| 54 | `CommonPolicyLevelCoverages` | no |
| 38 | `SingleInterestAutoPhysicalDamageInsurance` | no |
| 2 | `RetrospectivePremiumPlan` | no |
| **623** | **29.1% of all demanded paths** | |

Two independent sources agree exactly on this list. The artefacts say these five never appear at
top level; the `[SectionBuilder]` attribute registry -- a declarative registration, not scraped
source -- registers 13 root sections and these five are not among them. The other 13 demanded
roots map one-to-one onto the 13 registered ones (`CAStateSpecific` ->
`CAState-specificPolicyLevelCoverages`, `PolicySection` -> `Policy`).

`CALocationLevelCoverages` is the one to note: 38 templates each need all five of
`Location/Address/{AddressLine1,AddressLine2,City,State,ZIP}`, and location-level coverage is a
whole axis of the model with no builder.

### 41.4 One precise defect inside a section that DOES work

`CAAutoLevelCoverages` supplies 124 paths, so it is not a stub. Yet the single most-demanded
unmet path in the entire library sits inside it:

```
43 templates bind  CAAutoLevelCoverages/Items/Auto/VehicleNumber
```

`CAAutoLevelCoveragesSection.cs:340` emits `VehicleNumberWording` and never `VehicleNumber`.
Counted exactly over the 65 artefacts: `<VehicleNumberWording>` appears **201** times,
`<VehicleNumber>` **0**.

This is section 40's finding (a) turning into a bug class. The model pairs every value with its
rendering -- 3,191 `*Wording` twins against 6 exceptions -- so a builder that emits only the
`Wording` half satisfies half the demand and leaves the other half blank. Searching for that
shape across all demand finds a short, complete list:

| templates | value element supplied only as `...Wording` |
|---:|---|
| 43 | `CAAutoLevelCoverages/Items/Auto/VehicleNumber` |
| 4 | `CAState-specificPolicyLevelCoverages/BusinessInterruptionCoverage-Washington/ScheduledPropertywithSameLimit/Limit` |
| 2 | `CAPolicyLevelCoverages/BusinessInterruptionCoverage/ScheduledPropertywithSameLimit/Limit` |
| 2 | `CAAutoLevelCoverages/Items/Auto/Snowmobiles2/OtherAutoCoverages/Items/Coverage/CoveredVehicleNumbers` |
| 2 | `CAAutoLevelCoverages/Items/Auto/Snowmobiles2/AdditionalPremiums/Exclusion4Premium` |
| 2 | `CAAutoLevelCoverages/Items/Auto/Snowmobiles2/AdditionalPremiums/Exclusion3Premium` |
| 1 | `CAState-specificPolicyLevelCoverages/PersonalInjuryProtection-Oregon/MedicalExpenseDeductible` |

Seven, and only seven -- so the convention is otherwise respected. Be careful what this claims:
43 production templates *bind an element the builder never emits*. It does not follow that 43
forms render wrong today, because which templates are selected for which policy is a separate
question this join does not answer. It is a lead with a file and a line number, not a verdict.

### 41.5 What this is good for

A new measure of the same kind as the fidelity gates, but on the binding axis: it ranks work by
how many production templates a fix unblocks, from a demand side that is exact. `PolicyNumber`
and `PrimaryNamedInsured` lead demand and are already supplied; `CALocationLevelCoverages` leads
the gap and has no builder.

What it cannot see -- state it before someone quotes the number:

* **It cannot fail on a wrong value.** It checks that an element is emitted with *something*, not
  that the something is right. A builder emitting the wrong policy number scores as supplied.
* **`missing` conflates two things** everywhere except the five root sections: a real gap and a
  path this policy mix never reaches. Only the NEVER column is sample-independent.
* **It says nothing about templates it was not given.** This is one package of eight.
* **`empty-only` is ambiguous by construction** -- correctly empty for these policies, or wired
  to nothing at all. 142 paths sit there and the join cannot tell which.

The fix for the first three is the same and is already the named next step: render one converted
form against one real policy end to end.

```bash
python tools/xmlsupply.py output/gdbindings-ca2607.csv <BuilderRenderOutput-dir> \
       --kind Builder --out output/xmlsupply-builder.csv
```

## 42. A third authority, and two format fields the extractor never read (2026-08-26)

The `.gdsp` was not the only thing Products supplied. The same folder holds an **Integration
Specification** zip: one `.txt` per template listing its "Used Domain Paths" -- GhostDraft
stating, in its own words, which domain nodes each template touches. 1,110 of them.

That is a third authority, and a categorically better one than the XSD. `gdxsdcheck.py` can only
prove a path we produced EXISTS. It is structurally blind to a path we never produced. The spec
lists the expected SET per template, so it can fail on an omission -- which is the failure mode
the XSD check cannot see, and the one that mattered.

### 42.1 It failed, twice, and both were real

First run, restricted to the 491 templates the package actually ships: **89.62% recall**. The
misses were concentrated, not scattered, which is what a systematic omission looks like:

* **`CAAutoLevelCoverages.VehicleNumber`, on 63 templates we produced nothing for.** Tracing one
  (`CA 04 41 11 20 Replacement Cost Coverage`) found the path in an `<orderByList><orderBy>` on a
  list instruction. **A list can declare a SORT, and the sort key is a data path.** 140 of them,
  never parsed.
* **`...containstheComprehensiveCoverage` and friends.** Not a defect: the spec keeps
  `contains the X` as a path segment where `gdmodel` follows the concept-library indirection to
  the selector it stands for. Both are right; the comparison had to treat both as predicates.

So the parser audit was done again, this time exhaustively and by PARENT rather than by
instruction type:

```
instruction:fillPointType          path            14107
instruction:fillPointType          adornmentPath    1393   (+ 12714 empty)
part:conditionalPartType           path            10428
instruction:listInstructionType    pathToList         691
orderBy                            path               140
part:compositeConditionalPartType  path                24
```

**This is section 39 repeating, in a session that had section 39 in front of it.** Section 39's
lesson was written as "audit the parser against the FORMAT, not against the output -- section 24
audited record TYPES; nobody had audited the FIELDS of the types we already parse." Section 40
enumerated the five instruction types and treated that as the audit. It was not: `orderByList`
and `adornmentPath` are fields of a type already parsed. The rule was correct and got applied one
level too shallow.

Worth noting *why* it was caught: not by re-reading the format, but because a third authority
disagreed. Two authorities had both said 100%.

### 42.2 The second field is formatting, and the resolver was right to refuse it

`adornmentPath` looked like 1,393 more bindings. Resolving it as a path produced 1,393
`unresolved root` rows -- with roots named `with comma grouping`, `as MM/dd/yyyy`,
`without cents`, `as yyyy`. An **adornment is the FORMATTING applied to the value**, declared in
the concept library (91 per library), not a second data path.

| uses | adornment |
|---:|---|
| 1320 | `with comma grouping` |
| 65 | `as MM/dd/yyyy` |
| 2 | `without cents` |
| 2 | `as MM/dd/yy` |
| 2 | `as yyyy` |
| 1 | `as Dollars with comma grouping` |
| 1 | `with 2 decimal places and comma grouping` |

That is the direct modern counterpart of a legacy DDT picture clause -- `movenum;9.0,9.0,C` on
`BIINJA` in section 40's matched pair is `with comma grouping` here. It completes the
value/rendering story: the model offers *three* mechanisms for the same job, a raw value, a
pre-formatted `*Wording` twin, and an adornment on the fill point.

The resolver refusing to invent a path is the reason this was legible in ten seconds rather than
becoming 1,393 plausible-looking wrong bindings. A resolver that guesses is worse than one that
reports.

### 42.3 After both fixes

```
templates compared                          491   (of the spec's 1110)
spec paths (canonical)                     8995
our paths  (canonical)                     9004
in both                                    8995
  spec recall                            100.00%
  our precision                           99.90%
templates where we found EVERY spec path  491/491
templates matching the spec set EXACTLY   482/491
```

The 9 excess rows are a bare `Policy` on 9 templates that the spec does not list. Everything else
agrees exactly. The XSD check still passes at **100.00%** over 19,112 paths, now including the 140
sort keys.

Section 40's instruction totals are superseded, and this is the current count — §40's numbers stand
as what §40 measured, per the append-only rule, so do not read them as live:

```
bindings extracted   27678   (was 26145 in §40)
  fillpoint          14107   unresolved 0
  condition          10300   unresolved 0
  subscription         885
  adornment           1393   NEW -- formatting, no path
  list                 691
  orderby              140   NEW -- real demand
  condition-not        128
  condition-composite   24
  annotation            10
unresolved               2   (one list-level test reached from item scope, on 2 templates)
```

Note the sample fact this exposed: **the `.gdsp` ships 491 templates and the spec documents 1,110,
with the 491 a strict subset.** Comparing against all 1,110 would have scored 619 templates we
have no `.gd` for as total misses -- a measurement of the package export, not the extraction. The
first run did exactly that and reported 47%.

### 42.4 The corrected demand, and what it does to section 41's lead

Sort keys are demand: a list cannot be ordered without its key. Folding them in:

| | |
|---|---|
| distinct data paths (fill points + sort keys) | **2,140** |
| demand rows | 14,247 |

And the element this moves is the one section 41 had already singled out:

```
        counted straight from the templates, three independent ways:
  43 templates use CAAutoLevelCoverages/Items/Auto/VehicleNumber as a FILL POINT
  81 templates use it as a list SORT KEY
  41 templates use it in a CONDITION
 106 templates in union   <- and the Integration Specification independently says 106
```

`Auto/VehicleNumber` is the **fifth most-demanded element in the library**, ahead of
`VehicleDescription`, and `CAAutoLevelCoveragesSection.cs:340` emits only `VehicleNumberWording`
(201 occurrences against 0 across the 65 artefacts). Section 41 filed this at 43 templates. The
number is 106, and the consequence is worse than a blank field: on 81 templates it is what the
auto list is SORTED BY.

The ranked gap list is otherwise unchanged -- `CALocationLevelCoverages` and the other four
builder-less root sections still account for 623 paths, 29.1% of demand.

### 42.5 Tooling

```bash
python tools/gdspeccheck.py output/gdbindings-ca2607.csv <pkg> "<IntegrationSpec.zip>"
python tools/gdspeccheck.py ... --form "CA 21 34 10 13 Schedule"
```

The spec zip is read in place -- do not extract it. Several template names exceed the Windows
260-character path limit and extraction fails partway through.

**Three authorities, and each caught something the others could not.** `model.xml` gave the
projection; the XSD caught `elementId`-vs-`elementName` and the mutex-group enum; the
Integration Specification caught two unread fields. Any two of them agreeing was not enough.

## 43. The WRITE side: what it takes to author a `.gd`, and what is still unproven (2026-08-26)

Sections 40–42 decoded the format by READING it. Authoring MoE's proprietary forms as GhostDraft
templates is the write direction, and it needs three things the read side did not: the grammar that
relates markup to RTF, the rule that names an XML element, and a target model. Two are now
established mechanically. The third is missing and is the real blocker.

**Nothing here has been round-tripped.** No `.gd` with logic has been written by this repo and
rendered by GhostDraft. The invariants below are verified against 491 production templates, which is
strong evidence about the format and no evidence at all about our output.

### 43.1 The logic grammar — verified, zero violations over 491 templates

`%[ID]` markers in the RTF are what place a markup node. The correspondence is exact and total:

| node kind | placed in RTF | not placed |
|---|---:|---:|
| `instruction:fillPointType` | 14,107 | **0** |
| `instruction:subscriptionType` | 885 | **0** |
| `instruction:annotationType` | 10 | **0** |
| `part:conditionalPartType` | 10,428 | **0** |
| `part:elsePartType` | 5,813 | **0** |
| `part:endPartType` | 11,053 | **0** |
| `part:listPartType` | 697 | **0** |
| `part:compositeConditionalPartType` | 12 | **0** |
| `instruction:conditionalInstructionType` | **0** | 10,362 |
| `instruction:listInstructionType` | **0** | 691 |

Five rules, all of which held universally:

1. **A container is never placed.** `conditionalInstructionType` and `listInstructionType` carry no
   marker — 0 of 11,053. Their PARTS carry the markers, and that is how the logic gets its extent.
2. **Everything else is placed exactly once.** 43,005 markers = 54,058 declared ids − 11,053
   containers. Exact, no remainder.
3. **No marker exists without a declaration.** 0 dangling markers.
4. **A container's part markers appear in declaration order, and its `endPartType` marker is last.**
   11,053 / 11,053 both ways.
5. **Every child's marker span nests strictly inside its parent's.** 9,689 nested containers and
   14,144 leaf instructions, no exceptions.

That is a complete, checkable grammar: to emit a conditional, declare the instruction with its
parts, then place `%[part]`…content…`%[elsePart]`…content…`%[endPart]` in that order inside the
parent's span. A generator can assert all five rules on its own output before writing the file.

**One trap, and it bit this analysis first.** `%[N]` is a marker in the RENDERED character stream,
not necessarily contiguous in the RTF source. Production splits it across runs —
`{\cf0\f3\fs20\ulnone\ulc0 %}{\cf0\f0\fs20\ulnone\ulc0 [70]}` — on **854 markers across 119
templates**. A naive scan of the raw RTF reports those nodes as unplaced, which is exactly the
"orphaned markup" conclusion this section nearly recorded. Destyle before scanning. (Our own
`FapToGhostDraftGenerator` already emits the split form, so whoever wrote it had seen this.)

### 43.2 The element-naming rule — 100.00%

To add an attribute to a concept library you must be able to predict the Server XML element name
fact-docgen will have to emit. The rule is "make the name a valid XML NCName":

```
id = drop every character except [0-9 A-Za-z _ . -]
     prefix '_' if the result starts with a digit
     append an integer if that id is already taken in scope
```

| | |
|---|---|
| exact | 12,169 / 12,486 (97.46%) |
| exact + collision suffix | 317 (2.54%) |
| **rule accounts for** | **12,486 / 12,486 = 100.00%** |

Worked examples: `Vehicle Number (Wording)` → `VehicleNumberWording`;
`CA State-specific Policy Level Coverages` → `CAState-specificPolicyLevelCoverages` (hyphen KEPT);
`is Organization (incl. Corporation)` → `isOrganizationincl.Corporation` (dot KEPT);
`51 - 200 Miles Premium` → `_51-200MilesPremium` (leading digit); `Snowmobiles` → `Snowmobiles1`
(collision).

### 43.3 `model.xml` is DERIVED — so authoring a model means authoring the `.gdm`

All **12,486** model.xml members appear in a concept library `.gdm`; **none** is absent. The `.gdm`
files carry 4,723 further guids that model.xml does not project (the concepts themselves,
adornments, domainModels, hidden nodes).

So `model.xml` and `GDXSD.xsd` are projections the GhostDraft Packager regenerates (5.3.1971.0 per
the spec's `Information.txt`). **Do not hand-author them.** A new concept is authored in the `.gdm`;
the ids, the XSD and the server model follow from §43.2's rule.

### 43.4 What our generator emits today, and the gap

`FapToGhostDraftGenerator` writes a valid `.gd` envelope and **fill points only**:

| element | ours | production |
|---|---|---|
| `properties`, `content/rtf`, `library` nil | yes | yes |
| `markup/instructions` with `fillPointType` | yes | yes |
| `listInstructionType` / `conditionalInstructionType` / `subscriptionType` | **none** | 11,938 |
| `parts` / `endPartType` structure | **none** | 28,003 |
| `orderByList` sort keys | **none** | 140 |
| `adornmentPath` (formatting) | emitted empty | 1,393 populated |
| `styleMap` + `stylelibrary` | **none** | every template |
| `annotationStyleMap`, `domainmodels`, `scenarios` | **none** | every template |
| `trimlastparagraphmarker`, `documenttype` | **none** | every template |

So it produces flat, logic-free forms. It also binds via `LookupBinding`, a hand-written catalogue
of ~7 name maps with hardcoded GUIDs against a different model than the ISO packages (its `Policy`
root guid is `ee97488b-…`; ISO Commercial Auto's is `48653e4d-…`), which is §40 finding (d) in
practice: field-NAME matching cannot get there.

### 43.5 The translation table is the real content, not the syntax

The syntax above is mechanical. What makes generated logic CORRECT is §40's matched-pair mapping,
which says what legacy machinery becomes:

| legacy DDT / FAP | GhostDraft |
|---|---|
| `>XUnit1` `move_it @GETRECSUSED` (unit iterator) | `listInstructionType` over the list, iterator bound to the item |
| DDT filter chain (`BYAGTX in (CA,FA), BYAOTX=UN, BYBCCD=WA`) | a named **selector** — one `xs:boolean` per list item |
| `hardexst` presence test | the `is provided` built-in on the resolved path |
| `printif` | `conditionalInstructionType` + parts |
| `concat` of N DB2 columns / `CALL("…")` DAL | ONE attribute; the transform moves upstream into the section builder |
| `movenum;9.0,9.0,C` picture | an **adornment** (`with comma grouping`) |
| a value→text table (`noopfunc` with `CSL 100 =100,000:…`) | the `*Wording` twin element |
| sort order implied by the extract | `orderByList` |

Six legacy fields collapsing to two elements is the normal case, not an exception.

### 43.6 The blocker: we have no proprietary package

`PackageNames.cs` names **ten** packages. We have the `.gdsp` for **one**, and it is an ISO package,
not a proprietary one.

This matters because **a fill point path is `(rootguid, pathNodes[guid])` and GUIDs are per-model.**
Without the proprietary model there is no way to emit a resolvable proprietary binding — and no way
to check one, since every instrument built in §40–42 keys off `model.xml`, the package XSD, and the
Integration Specification.

The good news is that the proprietary models **already exist and are already fed**:

* `MoE Proprietary Commercial Auto` has **25 registered section builders** at version 2601.0, over
  roots `MOECAAutoLevelCoverages`, `MOECAPolicyLevelCoverages`, `MOEPolicyDecInfo`, plus shared
  `Agent`/`Insured`.
* 10 of the 65 integration-test `Builder.xml` artefacts carry those roots, so real policies already
  produce proprietary Server XML.

So this is **adding templates to an existing proprietary model**, not building a model from scratch
— much the easier job, and the one the §40–42 decode maps onto directly.

What is needed, in order:

1. **The `MoE Proprietary Commercial Auto` package export** (`.gdsp` + its GDXSD, ideally the
   Integration Specification too). Every tool in §40–42 then works on it unchanged — point them at
   the extracted directory.
2. **A round trip on ONE form.** Author a `.gd` with a list, a conditional and a bound fill point;
   open it in GhostDraft; render it. Until that has happened, §43.1's grammar is a well-evidenced
   hypothesis about our output, not a demonstrated capability.
3. Only then generate at scale.

The MCP is not a route to (1): `form_list_templates` / `form_parse_ghostdraft_template` read the
Integration Specification `.txt` files, not `.gd` templates or concept models, and the template
directory is unconfigured in this environment.

### 43.7 Unknowns that a round trip would settle

Named so they are not mistaken for solved:

* **Will GhostDraft open a machine-authored `.gd`?** `styleMap` carries a `libraryid` and per-style
  `link` GUIDs into a style library; `annotationStyleMap`, `domainmodels` and `scenarios` are absent
  from our output entirely. Which are required and which are optional is untested.
* **Who mints GUIDs for new concepts,** and whether the Packager accepts hand-edited `.gdm` files
  or insists on Designer.
* **ID allocation.** Production ids are unique per template but this analysis never checked whether
  they must be contiguous or ordered; our generator's sequential allocation may or may not matter.
* **The `<explanation>` blob** on `<markup ID="0">` is a second RTF document (the reviewer-facing
  narrative). Production always has one. Whether it is required is untested.

## 44. The proprietary package, and a `.gd` with logic authored against it (2026-08-26)

Products supplied **MoE Proprietary Commercial Auto 2607.0** — the blocker §43.6 named. It is a
smaller package than the ISO one and carries something the ISO package did not: **32 Test Cases,
which are real Server XML instances.** They substitute for the GDXSD this export does not include.

```
79 Templates   1 Concept Library   model.xml   32 Test Cases   3 Style Libraries
CompositionServerVersion 5.3.1771   PackageVersion 2607.0
6 root elements: Policy, Insured, Agent,
                 MOECAAutoLevelCoverages, MOECAPolicyLevelCoverages, MOEPolicyDecInfo
93 types, 353 members  (against the ISO package's 2,678 and 12,486)
```

### 44.1 The toolchain transferred with no changes

`gdbindings.py`, pointed at the new package: **1,761 bindings, 0 unresolved.** 863 fill points, 590
conditions, 241 adornments, 36 subscriptions, 31 lists, over 273 distinct Server XML paths.

The §43 authoring grammar **replicates exactly on an independent package** — 79 templates, zero
violations: containers never placed (0 of 621), every other instruction and part placed exactly
once, part markers in declaration order, endPart last, all spans nested. Two packages, 570
templates, one grammar.

`is provided` carries **the same guid in both packages** (`50b7af4a-…`, 5,742 uses in ISO and 431
here), confirming §42's reading that the built-ins are GhostDraft constants rather than model values.

### 44.2 The Test Cases are an authority, and they discriminate

All **221** demanded data paths appear, with values, in the 32 instances — 100.0%. A gate that
cannot fail would report the same thing, so it was crossed against the wrong model: ISO demand
against these test cases scores **1 of 2,140** (the single hit is `Policy/PolicyNumber`, which both
models happen to name identically). The instrument discriminates.

### 44.3 The live proprietary gap in fact-docgen

Proprietary demand against the 65 real `Builder.xml` artefacts:

| | paths | template-weighted |
|---|---:|---:|
| supplied | 140 (63.3%) | 599 (71.2%) |
| empty-only | 64 (29.0%) | 214 (25.4%) |
| missing | 17 (7.7%) | 28 (3.3%) |

**63.3%, against 30.1% for ISO** — as expected, since MoE built this model for its own forms. All
five demanded roots are emitted, so there is no builder-less section here; the 17 misses cluster in
`AutosWithFarmAutoSeasonalLayUp`, `MCS-90` dates and `ItemSixScheduleOfDriversForPersonalUseAutos`,
and only 10 of the 65 policies are proprietary, so most of that is sample limitation rather than a
gap. Ranked list in `output/xmlsupply-propca.csv`.

### 44.4 Our existing `.gd` generator is bound to three different models at once

With the real model in hand, `FapToGhostDraftGenerator`'s hand-written catalogue can be CHECKED
rather than trusted. Its `Policy` root guid `ee97488b-…` and `Policy Number` guid `16ed3972-…` are
byte-identical to this package's, so it was always aiming here. Resolving all 428 generated `.gd`
files against the authority:

| | |
|---|---|
| files carrying fill points | 284 of 428 |
| fill points emitted | 1,480 |
| **resolve in MoE Proprietary CA** | **51 (3.4%), on 13 paths, across 11 forms** |
| root `Quote` (guid `bffb5190-…`) | 812 — **in neither package we hold** |
| unbound (`<path xsi:nil="true"/>`) | 617 |

So the catalogue is a three-way mixture: correct proprietary GUIDs for `Policy`, `Insured`, `Agent`
and — the standout — eight genuinely correct `MOECAPolicyLevelCoverages/MCS-90/*` paths, plus a
`Quote` model that does not exist in either export, plus 42% simply unbound. A `.gd` whose fill
points cite GUIDs from two models cannot resolve in either package. This is §40 finding (d) —
field-NAME matching — showing up as a measurable number.

### 44.5 A `.gd` WITH LOGIC, authored and checked

`tools/gdauthor.py` emits a template from a declarative spec (`Repeat` / `Cond` / `Fill` /
`Static`), building markup and RTF in one recursive pass so rules 4 and 5 hold by construction. It
resolves every binding through `gdmodel` first, so a path that would not resolve in the real package
fails at author time rather than in GhostDraft.

The demo reproduces a Loss Payable Clause vehicle schedule: iterate the autos, print each field only
when provided, with an else branch on VIN. Output: **26 instructions, 13 bindings, all resolved.**

Four checks, three of them by tools that know nothing about the emitter:

1. **`verify()` re-derives all five grammar rules from the written bytes** — not from the emitter's
   intent. Passed.
2. **`--selftest` mutates the file five ways and requires every rule to fire.** All 5 caught: place
   a container (rule 1), delete a leaf marker and duplicate a marker (rule 2), add an undeclared
   marker (rule 3), move an endPart before its siblings (rule 4).
3. **`gdbindings.py` reads it back** and reconstructs the intended tree exactly, resolving all 13
   bindings — a genuine round trip through a separately written reader.
4. **`xmlsupply.py` validates the 7 distinct data paths against the package's own 32 test-case
   instances**: 7/7 present with values.

And the structure it produced is **the same shape as the production
`Loss Payable Clause- Vehicle Schedule Overflow` template** — list over `AutosWithLossPayableClause`,
`is provided` conditionals on VehicleNumber / Description / VIN / ComprehensiveDeductible /
CollisionDeductible, then `LossPayee/FullName`. That was not copied; it fell out of writing the
schedule the model's shape implies.

### 44.6 What is STILL not proven — read this before claiming the capability

**GhostDraft has not opened the file.** Everything above is checked by tools in this repo plus the
package's own data. That is a materially stronger claim than §43 could make, and it is not the same
claim as "GhostDraft renders it".

Specifically untested, and all of it in the envelope rather than the logic:

* `styleMap` / `stylelibrary` — production carries a `libraryid` and per-style `link` GUIDs into a
  style library. The emitter writes none, so it has no named styles: every run is inline
  `\f1\fs20`. Whether GhostDraft requires the map is unknown.
* `annotationStyleMap`, `domainmodels`, `scenarios`, `trimlastparagraphmarker`, `documenttype` —
  present on all 570 production templates, absent from ours.
* The `<explanation>` blob on `<markup ID="0">` — a second RTF document carrying the
  reviewer-facing narrative. Always present in production.
* **ID allocation.** Ours are sequential from 1 and unique. Production ids are unique but this
  analysis never established whether contiguity or ordering matters.
* Layout fidelity is not addressed at all here. The demo's RTF is deliberately plain text; the
  geometry work of §10–39 is a separate axis and none of it is wired into `gdauthor.py`.

The next step is small and decisive: **open `output/authored-lpc.gd` in GhostDraft Designer and
render it against one of the 32 shipped test cases.** It either opens — in which case the envelope
gaps above are cosmetic and generation at scale is an engineering exercise — or it names exactly
which envelope element is required, which is a day's work to add.

### 44.7 Tooling

```bash
python tools/gdauthor.py <pkg> --demo output/authored-lpc.gd     # author + verify
python tools/gdauthor.py <pkg> --selftest output/authored-lpc.gd # prove verify() fails
python tools/gdbindings.py <pkg> --templates output/authored --out out.csv   # read ours back
python tools/xmlsupply.py out.csv "<pkg>/Test Cases" --kind "*"  # paths vs real Server XML
```

`--templates` points `gdbindings.py` at any directory of `.gd` files, so OUR output can be resolved
against a REAL model. That is the check that turned "the generator has a concept catalogue" into
"3.4% of its fill points resolve".
