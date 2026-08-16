# fact-pdf-tools — Roadmap

The goal: a **deterministic pipeline** that converts legacy Mutual of Enumclaw Documaker (FAP/DDT)
forms into GhostDraft `.gd` documents, lets the business build test scenarios, renders any form from a
catalog against a real policy, and keeps the GhostDraft model + our Common Data Model (CDM) honest about
what data each form needs.

Status: ✅ done · 🟡 in progress · ⬜ not started
(Detailed engineering notes live in [PROGRESS.md](PROGRESS.md); this file tracks the big goals.)

---

## 1. Deterministically convert legacy forms → GhostDraft  🟡 (all quote forms converted)

- [x] **Batch-converted all 419 quote forms** (`demo convert-quotes`, 2026-07-28) → `.gd` in
      `output/quote-forms-gd/` + CONVERSION-REPORT.md. 0 failures; 419/419 structurally valid
      (brace-balanced); 369 with RTF tables. Families: QTE_ 107, QCPP_ 138, QFRM_ 95, QBOP_ 42, others 37.
      Caveat: concept binding is limited; image chrome deferred; true visual check needs GhostDraft Designer.
- [x] **Expanded quote header bindings** (2026-07-28): added cross-quote-family header fields to
      `DefaultFieldMap` (INSURED NAME1/2, AGENT NAME, EFFDATE/EXPDATE, POLICYNBR/POLICYNUM). Quote bindings 16 → 43.
- [x] **Project-owned "Quote" concept model** (2026-07-28): since the CA-only Model Library lacks
      premium/limit/deductible/coverage concepts, created `ProjectConcepts` (a Quote domainModel with 13
      attributes: Premium/Limit/Deductible/Coverage/Exposure/Form fields/etc., deterministic GUIDs). Wired
      into the converter's binding fallback AND emitted as an extended library via `demo make-concept-library`
      → `output/concept-library/Model Library (with Quote).gdm` (validated well-formed, 122 concepts).
      **Quote bindings 43 → 829/1439 (58%)** after expanding the Quote model to 29 attributes (premium/limit/
      deductible/coverage + location/building/cause-of-loss/protection-class/class-code + vehicle year/VIN/
      make-model/state/premium). Across ALL families (QCPP 421/580, QFRM 184/336, QTE_ 111/282, QBOP 55/110).
      Import the extended `.gdm` in GhostDraft so bindings resolve. Remaining ~610 unbound = the low-frequency
      long tail (FIELD/VSDS/CONTMSG/`> BYSLOC` repeating markers + per-form one-offs).


**Objective:** any FAP form → a `.gd` that opens in GhostDraft with faithful layout, fonts, and fields —
no manual cleanup.

**Where we are:** engine built and confirmed against Documaker on `QTE_EA9910E`. FAP geometry + FXR fonts
→ RTF (flowing paragraphs for prose, `\trowd` tables for columnar pages), matching how GhostDraft itself
authors documents (learned from real dec pages in `…\Moe Proprietary\Documents`).

- [x] FAP parser + FXR font resolution (exact pt size / bold / italic / face)
- [x] Flowing-paragraph builder (prose) and RTF-table builder (columnar)
- [x] N-column inference: right-justified value columns (shared `col2`) vs. left columns (clustered `col1`)
- [x] Deterministic right margin from FAP content edge; constant per-row table geometry (`\trleft0` + `\li` indents)
- [x] Field fill points (`%[N]`) + markup concept bindings in GhostDraft's format
- [ ] Dense-grid refinement (auto-symbol page `QTE_COVAUTOSYM`: right-hand definition column can overflow)
- [x] Table gridlines/borders from FAP `X,` rectangles — `DetectGrid`/`EmitGrid` build a bordered RTF
      table from the rectangle geometry (payment-plans grid `QTE_BILLINFO` → 5×4 bordered table).
      ✅ Confirmed in GhostDraft.
- [ ] Images / logos (`G,` references — logo, cover banner). **Investigated 2026-07-28:**
      FAP `G,` names an image (e.g. `QTE_COVHDR` banner, `QTE_COVFTR` footer, logo); source is a Documaker
      `.LOG` file (ASCII header + hex pixels, ~7 MB — decodable but proprietary). Better path: GhostDraft
      already ships MoE branding as reusable assets in `…\Moe Proprietary\Resources\Stationery\`
      (`Logo.gd` = the shield logo, confirmed extractable as a clean PNG; `Headers & Footers\` = TEXT
      templates, not images; `Executive's Signatures\`). Plan when tackled: parse `G,` into the model,
      build a `G,`-name → GhostDraft-asset map, embed the asset's PNG as a positioned `\pict` (reuse the
      WIP background embed code). Caveat: the photographic quote-cover banner has NO GhostDraft asset yet —
      needs `.LOG` decode or a marketing source. NOTE: the coverage "548 images" over-counts — many `N,`
      lines are annotations, not images; real count is lower (`G,` only). Images are branding/chrome, not data.
- [ ] Multi-page columnar forms (table builder is single-page today)
- [x] Batch-convert the full form library + a **coverage report** (`demo coverage` → `output/coverage-report.{md,csv}`).
      Result: **4462 forms, 100% generate a .gd without error** (no crashes). Inventory: 2756 have fields,
      1304 grids, 548 images, 1045 multi-page. QTE quote family (107 forms): **70 (66%) have images** → images
      is the top quote-page fidelity gap. ("OK" = no exception, not yet a render-fidelity guarantee.)
- [ ] Automated visual regression (our FAP→PDF vs. legacy PDF; GhostDraft render still needs a manual pass)

## 2. Business-authored test scenarios  ⬜ (foundation exists)

**Objective:** a form author at MoE can create/save a named scenario (a set of field values or a policy
snapshot) and re-run it against a form they're building — self-service, no engineer.

**Where we are:** we have a file-based `ScenarioStore` + `ScenariosController` (CRUD + run) and a
`ScenariosPage` for our own testing. GhostDraft also has its own `.scenario` format (samples in
`…\Resources\Scenarios`). These aren't yet aligned or business-facing.

- [x] Internal scenario model + store + run endpoint + basic UI
- [ ] Decode the GhostDraft `.scenario` format; decide align-to-GhostDraft vs. keep our own + export
- [ ] Scenario authoring UI aimed at business users (pick form → fill fields / load a policy → save → run)
- [ ] Seed scenarios from a real policy (prefill from CDM), then let the user tweak
- [ ] Scenario library per form/edition; share/duplicate; regression set for a form under development

## 3. Catalog → pick form → enter policy number → render  ✅ (proven live)

**Objective:** browse a catalog, choose a form, type a policy number (+ environment), and get the rendered
form filled with that policy's data.

**Where we are:** `CatalogPage` lists FORM.DAT entries; `PolicyController` fetches a CDM policy by
number+env; `TemplatesController` fills a pre-built PDF template from a policy via `IFormFieldMap`. The
pieces exist but aren't wired into one smooth catalog→policy→render flow, and field maps exist for only a
few forms.

- [x] Form catalog listing; policy fetch by number+env; template fill from CDM policy
- [x] One UI flow **already wired**: `CatalogPage` → `/convert?form=&edition=` → "Load from Policy" card
      (env + policy#) → `POST /api/policy/populate` (fetch CDM → render FAP → fill via `IFormFieldMap`).
      Registry has a `GenericHeaderFieldMap` fallback so EVERY form renders (≥ common header fields).
- [x] Fill+render pipeline proven offline via `demo` (sample CDM policy → filled PDF).
- [x] **Live end-to-end PROVEN (2026-07-28):** rendered MCS90A filled from real tst policy `BAP000000515`
      → "Issued to: RENEWAL OVERLAY TEST", policy # BAP000000515, eff 01/25/2025 (`output/policy_live_test.pdf`).
      Required two fixes: (1) the `CommercialApi.BaseUrls` in `appsettings.json` were dead Azure hosts
      (NXDOMAIN) — corrected to the real internal pattern `https://pointmoeapps-{env}.mutualofenumclaw.net/commercialapi`;
      (2) `/api/policy/populate` only resolved via FORM.DAT — added the direct-FAP-filename fallback (like `/convert`)
      so MCS90A/QTE_* render.
- [ ] Extend `IFormFieldMap` coverage — only 4 specific maps today (CA2146, BOPDEC, MCS90A, EB2410) + the
      generic header fallback; more forms want specific maps for their data fields.
- [ ] Optional: same flow producing a **GhostDraft** render (where the Model Library supports the form)

## 4. Model-gap tooling: augment the GhostDraft model & CDM deterministically  🟡

**Objective:** given a form's fields, deterministically answer — is this concept already in the GhostDraft
**Model Library**? Is the data already in our **CDM**? If not, add the concept to the Model Library and/or
flag the CDM addition — as a repeatable skill, not hand-work.

**Where we are:** we can parse `Model Library.gdm` (121 concepts, GUIDs match our bindings) and the CDM
exists (`MoE.CommonDataModel`). The commercial MCP already has building blocks (`form_cdm_gap_analysis`,
`form_generate_mapping_report`, `form_read_model_file`). Nothing yet ties form fields → both models with a
gap report + safe model edits. **Known constraint:** the current Model Library is Commercial-Auto-centric —
it has no Farm concepts (see [memory: ghostdraft-model-library]).

- [x] `ModelLibrary` loader/parser (`core/Infrastructure/ModelLibrary.cs`) → flattened concept catalog
      (concept/attribute names + GUIDs + types). 301 bindable attributes from `Model Library.gdm`.
- [x] **DDT-driven gap report** (`demo gap <form>`): per field, the authoritative DATA PROVENANCE from the
      DDT — `system` (DAL/table computed), `manual` (powtype/WIP entry), or `constant` (mk_hard) — plus
      whether our converter already binds it to a GhostDraft concept. This replaces unreliable field-name
      guessing with the DDT's real "where the data comes from" (per user insight, 2026-07-28).
- [x] CDM check demonstrated via commercial MCP: `form_resolve_dal` (DAL → DB tables) + `form_cdm_gap_analysis`
      (columns → found/missing in `MoE.CommonDataModel`). **Nuance:** literal column-name search over-reports
      "missing" because legacy iSeries column names (POLICY0NUM/EFFDATE/LONGNAME) ≠ CDM semantic names
      (Policy.Number/EffectiveDate, Insured.FullName). The data IS on the model; the CDM check needs a
      semantic column→concept→CDM-property layer, not literal matching.
- [x] **Automated on-model (CDM) check** in `demo gap <FORM>` (2026-07-28): loads the CDM hydration SQL
      (`fact-commercial-api/.../GetPolicy/*.cs` + `Data.Provider/Mapping`), extracts each system field's DB2
      columns from the DDT `Source`, and reports ON CDM? = `yes` (columns hydrated) / `NOT hydrated` /
      `via DAL (resolve)`. Fully local (no MCP at runtime). Verified: POLICYNUM yes (real columns),
      ISSUEDTO flagged DAL. Heuristic caveat: a table-name hit counts; DAL fields need `form_resolve_dal`
      for column-precise confirmation.
- [x] Deterministic Model Library augmentation — `demo make-concept-library` inserts our `ProjectConcepts`
      Quote concept + domainModel into the `.gdm` (deterministic GUIDs, matching the converter bindings).
      First use: the Quote model (40% of quote fields now bind). Extend `ProjectConcepts.Attributes` to add more.
- [x] Packaged as a skill: `.claude/skills/form-gap-analysis/SKILL.md` (provenance → GhostDraft Model
      Library check → CDM hydration-trace → what-to-add). Invoke for "what data does form X need / is it on
      our model".

## 5. Recommended additional functionality

- [x] **Regression harness** — `demo regress [capture]` golden-files the `.gd` generator output for a curated
      8-form set (2-col right-align, prose+fields, bordered grid, N-column, multi-col defs, multi-page). Check
      reports OK/CHANGED/NEW (normalizes the volatile Created timestamp) and exits 1 on change, for CI. Golden
      files in `demo/regression/golden/`. Verified it catches changes. Catches `.gd`-generator regressions
      without a manual GhostDraft render. (A pixel-level GhostDraft render diff still needs GhostDraft.)
- [ ] **Packet assembly** — the Farm quote packet is ~24 FAP fragments (`QTE_*` + `QFRM_*`) assembled in an
      order driven by coverages. Capture that form-set/order so a whole packet can be produced, not just pages.
- [ ] **Auto-binding suggestions** — use the Model Library catalog to propose field→concept matches for review.
- [ ] **Value formatting / adornments** — money ("without cents"), dates, percent — carry FAP/DDT format
      hints into GhostDraft adornments so filled values render correctly.
- [ ] **Conversion coverage dashboard** — per-form status (clean / warnings / unsupported construct) across
      the whole library, to prioritize work and prove progress.
- [ ] **Border/grid fidelity** — render FAP `X,` rectangles as real table borders where the legacy shows a grid.
- [ ] **Golden-file tests in CI** — lock in known-good `.gd` output for a set of representative forms.

---

## Suggested sequencing

1. Finish **Goal 1** fidelity gaps that block real packets (borders/grid, images, dense grids) + batch coverage report.
2. Wire **Goal 3** end-to-end UI (catalog → policy → render) on the CDM PDF path — highest visible value.
3. Build **Goal 4** model-gap tooling — it unblocks real GhostDraft binding and tells us what the CDM is missing.
4. Layer **Goal 2** business scenario authoring on top once binding + catalog are solid.
5. Pull in **Goal 5** items as they unblock the above (regression harness early; it pays for itself).
