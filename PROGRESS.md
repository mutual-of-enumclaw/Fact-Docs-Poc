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

## Open questions / later
- [ ] Commercial API auth for env endpoints (token / managed identity?) — confirm
- [ ] Data-driven DDT → field auto-mapping (replaces hand-written `IFormFieldMap`) — deferred
- [ ] GhostDraft `.gd/.gdm/.scenario` export (copy MCP `GhostDraftProjectGenerator`) — deferred
- [ ] Add more `IFormFieldMap` implementations for additional forms as needed
