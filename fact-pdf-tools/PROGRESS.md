# fact-pdf-tools → GhostDraft-like App — Progress Tracker

Three pillars: (1) legacy → PDF render (exists), (2) author new forms (legacy-derived + canvas),
(3) populate with policy data + scenario testing. Spire.PDF in-app rendering; file-based JSON persistence.

## Phase 0 — Backend foundations
- [x] Add `AuthoredFormsDirectory` / `ScenariosDirectory` to `FormFileOptions`
- [x] Add `CommercialApiOptions` (env → base-URL map, default `tst`)
- [x] Add `FormDefinition` model (fields/static-texts/lines in PDF-point coords)
- [x] Add `FormScenario` model (form + edition + flat field-value map)
- [x] Add `FapFormDefinitionConverter` (FAP ↔ FormDefinition)
- [x] Add `FapToPdfGenerator.GeneratePdfBytes(FormDefinition)` overload
- [x] Add `FormDefinitionStore` (file-based JSON under `output/authored-forms/`)
- [x] Add `ScenarioStore` (file-based JSON under `output/scenarios/`)
- [x] Add `FormFieldMapRegistry` in population project
- [x] Add `population` project reference to `server`
- [x] Update `appsettings.json` + `appsettings.Development.json` with new config sections
- [x] Register new services in `server/Program.cs`

## Phase 1 — Render authored/legacy definitions (server)
- [x] `DefinitionsController`: `GET /api/definitions` — list authored forms
- [x] `DefinitionsController`: `GET /api/definitions/{id}` — get one
- [x] `DefinitionsController`: `PUT /api/definitions/{id}` — save/upsert
- [x] `DefinitionsController`: `DELETE /api/definitions/{id}` — delete
- [x] `DefinitionsController`: `POST /api/definitions/{id}/render` — render to PDF
- [x] `DefinitionsController`: `POST /api/definitions/import-legacy` — seed from FAP

## Phase 2 — Authoring canvas UI (client)
- [x] `client/src/api/definitionsApi.ts` — typed API client
- [x] `client/src/pages/DesignPage.tsx` — canvas editor (drag/resize, properties panel, import legacy, live preview)
- [x] `/design` route added to `App.tsx`

## Phase 3 — Policy population (server + client)
- [x] `PolicyController`: `GET /api/policy/{num}?env=` — fetch CDM policy JSON
- [x] `PolicyController`: `POST /api/populate` — form + policy → filled PDF
- [x] `client/src/api/policyApi.ts` — typed API client
- [x] `ConvertPage.tsx`: "Load from policy" card (env picker + policy number)
- [x] `ConvertPage.tsx`: graceful "no field map" message

## Phase 4 — Scenario testing (server + client)
- [x] `ScenariosController`: CRUD + `POST /api/scenarios/{id}/run`
- [x] `client/src/api/scenariosApi.ts` — typed API client
- [x] `client/src/pages/ScenariosPage.tsx` — list/create/edit/run + PDF preview
- [x] "Save as scenario" action on `ConvertPage.tsx`
- [x] `/scenarios` route added to `App.tsx`

## Phase 5 — Navigation & catalog polish
- [x] `AppLayout.tsx` nav: Catalog / Convert / Design / Scenarios
- [x] `CatalogPage.tsx`: authored-forms count / "Open in Design" link (future)

## Verification
- [ ] `dotnet build` Core / Server / Population / Demo
- [ ] moe-ai MCP server still builds (untouched)
- [ ] Pillar 1: `/catalog` → CA2146/1293 → Convert & Preview → manual fill → flatten download
- [ ] Pillar 2a: `/design` import-legacy CA2146 → move field → save → re-render matches
- [ ] Pillar 2b: blank form → add field + text → render
- [ ] Pillar 3: `/convert` load policy by number `env=tst` → fields auto-fill → render
- [ ] Pillar 4: save scenario → reopen → run → preview; JSON persists under `output/`
- [ ] Field-name parity: populated AcroForm names match FAP field names

## Phase 6 — GhostDraft export (legacy FAP → .gd)
- [x] `FapToGhostDraftGenerator.Generate()` — FAP → GhostDraft 5.x `.gd` (RTF body + XML markup)
- [x] Flowing + WIP (shape-based / EMF background) RTF builders
- [x] Model Library concept bindings for Policy / Insured / Agent / MCS-90 fields
- [x] `POST /api/convert/export-gd` endpoint + `exportGhostDraft()` client call
- [x] **Global concept model + per-form field maps** (2026-07-28): replaced the single
      name-keyed `FieldConceptMap` with a canonical `ConceptModel` catalog (keyed by
      `Policy.EffectiveDate`, `Insured.FullName`, `Mcs90.*`, …) that every form maps into via
      `FormFieldMaps` (form-number-prefix → field-name → concept key) + a `DefaultFieldMap`
      fallback. `Generate()` now takes `formNumber`. Fixes the `EFFDATE` cross-form collision
      (policy eff date on quotes vs. endorsement eff date on MCS-90).
- [x] **First quote page converted — QTE_COVER** (2026-07-28): 9 fill points; bound INSURED
      NAME1→Insured.FullName, AGENT NAME→Agent.FullName, EFFDATE→Policy.EffectiveDate,
      EXPDATE→Policy.ExpirationDate. Unbound (no known concept yet): TITLE, INSURED NAME2,
      AGENT PHONE, PROPOSAL PERIOD, MT_DISCL. MCS-90 bindings verified unregressed.
- [x] **Farm Quote Packet build set** (2026-07-28): mapped all 13 pages of `Farm_Quote_Packet.pdf`
      to their FAP sources and converted 24 building-block forms → `output/farm-packet-gd/` (+ MANIFEST.md).
      Packet = two FAP families: `QTE_*` (cover/disclaimer/boilerplate/auto-symbols/forms-list) and
      `QFRM_*`/`QFRMSUM_*` (Farm-quote summary fragments: header + repeating detail + total, per coverage).
      Switched the server form library to DocProd-Development (see config note below).
- [x] **Deterministic table-based engine** (2026-07-28): earlier flowing (tab-stop) and shape attempts
      both drifted / failed in GhostDraft. Root approach corrected by inspecting REAL GhostDraft docs in
      `Downloads\Moe Proprietary\Documents\Dec Pages\*.gd`: simple forms = flowing paragraphs; columnar
      docs = **RTF tables** (`\trowd`), never shapes. New engine in `FapToGhostDraftGenerator`:
      (1) fonts resolved deterministically from the FXR via `FxrFontLibrary` (exact pt size → `\fs`,
      bold, italic, `\f0`Times/`\f1`Arial) — replaces the guessed `FontMap`; (2) columnar rows emitted as
      RTF table rows matching the DA0093 grammar — left clusters = left cells at FAP columns, right-zone
      clusters = one `\qr` value cell to the right margin, boundary sized from FXR char widths so the
      widest value fits; (3) prose = plain paragraphs. `Generate()` takes an `FxrFontLibrary`;
      `ConvertController` injects it. Verified structurally: EA9910E → 25 table rows, braces balanced,
      FXR fonts (cover shows fs12/20/24). NOT yet visually confirmed in GhostDraft.
- [x] Table engine visually confirmed on QTE_EA9910E (matches Documaker: labels left, values right-aligned).
- [x] **Right-margin fix** (2026-07-28): values were aligning to a fixed col ~18600 (~0.5" short). The
      section right margin is now derived from the FAP content: right edge = max col2 of right-zone
      items (Documaker right-justifies values to their box col2, all = col 19600 on EA9910E).
      margrsxn + usable width computed from that, so values land exactly at the legacy right margin.
- [x] **Value right-edge alignment fix** (2026-07-28): short vs long values landed on slightly
      different right edges. Two causes: (a) `\trpaddr108` right cell padding — short values sat inside
      it, long ones overflowed past it → zeroed cell padding so `\qr` hits the exact cell edge; (b) value
      cell was sized from FXR (Documaker font) widths but GhostDraft renders Arial wider → widened the
      value cell (maxRightWidthTw × 1.3). Now every value cell is identical (right edge = col 19600, `\qr`).
- [x] **Constant table geometry** (2026-07-28): indented rows' values were shifting right because label
      indentation used `\trleft` (offsets the WHOLE table incl. the value column). Rewrote `EmitTableRow`
      to a fixed two-zone geometry: `\trleft0` + identical `\cellx` (label [0→valueCellLeftTw], value
      [valueCellLeftTw→col19600]) on every row; label indent now via the label paragraph's `\li`. Value
      column right edge is now the same on top-level and indented rows alike.
- [ ] Re-render QTE_EA9910E.gd to confirm indented-row values no longer overshoot the margin
- [x] **N-column inference** (2026-07-28): replaced the fixed label|value 2-zone with `InferColumns` —
      detects a RIGHT-aligned value column (items sharing the page right edge col2 with varying col1)
      vs. LEFT-aligned columns (clustered by col1, gap > 3500 FAP = real column break; smaller = indent
      levels via \li). `EmitTableRow` emits one cell per inferred column with fixed page-global
      boundaries + \trleft0. EA9910E preserved (2 col, values \qr); QFRM_FGL → 3 left cols; auto symbols
      → 2 left cols (no more wrongly right-aligned definitions). All brace-balanced.
- [ ] Render a multi-column page (QFRM_FGL) in GhostDraft to confirm N-column layout
- [ ] Auto-symbols edge case: a left column whose content sits far right of its cell start (col 10433
      in a cell ending ~9429) can overflow — refine column boundaries for dense grids if needed
- [x] **Grid borders from X, rectangles** (2026-07-28): `DetectGrid` finds a page's rectangular `X,`
      lines (>4 rects, >=2x2 boundary lattice) and builds row/col boundary lists; `EmitGrid` emits a
      bordered RTF table (`BorderedCellProps` = `\clbrdr*\brdrw10\brdrs`) from that geometry, placing each
      text item into the cell whose rectangle contains its (row,col). Grid items are excluded from column
      inference / normal flow. `QTE_BILLINFO` → 5x4 bordered table; grid-less forms unaffected
      (EA9910E still borderless, 25 `\qr`). Brace-balanced. ✅ Confirmed in GhostDraft.
- [ ] **Farm concept model**: `QFRM_*` fields + auto-symbol/vehicle-schedule/forms-list fields are all
      UNBOUND — need Farm GhostDraft Model Library concept GUIDs (Farm Property / GL / Auto data model)
      added to `FapToGhostDraftGenerator.ConceptModel`.
- [ ] **Packet assembly spec**: authoritative fragment order + repeating rules (per location/vehicle/
      coverage) lives in the quote print-job / form-set definition — needed to reproduce packets end-to-end.
- [ ] Find concept GUIDs for AGENT PHONE / PROPOSAL PERIOD / TITLE, then bind

## 2026-07-28 — Server form library repointed to DocProd-Development
`appsettings.Development.json` FormDatPath/FormsDirectory/DdtDirectory now point at
`C:\src\FaCT-DocProd-Development\mstrres\MOEC0` (was `C:\EDrive\moec0\Mstrres\MOEC0`). The Dev tree
carries the newer form editions the current quote packets use (e.g. EA9910E 12/24, EF9923/24 03/26);
EDrive was missing some. FxrPath + output dirs unchanged. Server uses IOptions — restart after edits.

## 2026-07-28 — Build restored after CommonDataModel migration
The `population` project referenced the now-removed `MoE.Commercial.GhostDraftDataModel`
project in the sibling `fact-commercial-api` repo. `CDMPolicyView` was folded into the base
`Policy` type in `MoE.Commercial.CommonDataModel` (namespace `MoE.CommonDataModel`); the old
convenience helpers (`AllForms`, `AllCoverages`, `*InContext`) were dropped but our field maps
never used them. Fix applied: repointed the ProjectReference to `MoE.CommonDataModel.csproj`,
swapped `using MoE.GhostDraftDataModel.SDK` → `using MoE.CommonDataModel`, and renamed the
`CDMPolicyView` type → `Policy` across population/server/demo. All 5 projects build clean;
server boots; `/api/forms` and `/api/convert/export-gd` return 200.

## Open questions / later
- [ ] Commercial API auth for env endpoints (token / managed identity?) — confirm
- [ ] Data-driven DDT → field auto-mapping (replaces hand-written `IFormFieldMap`) — deferred
- [ ] GhostDraft `.gdm/.scenario` export (copy MCP `GhostDraftProjectGenerator`) — deferred
- [ ] Add more `IFormFieldMap` implementations for additional forms as needed
