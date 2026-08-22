# Handoff — fact-pdf-tools (FAP → GhostDraft)

Written 2026-08-22 for the next agent. Read this first, then `ROADMAP.md` (goal tracker),
`PROGRESS.md` (engineering detail), and the 6 memory files (auto-loaded; listed at the end).

## What this project is

Convert legacy Mutual of Enumclaw Documaker **FAP/DDT** insurance forms into **GhostDraft `.gd`**
documents, deterministically, and make them testable with real policy data. Five goals (see ROADMAP):
1. Deterministic FAP→GhostDraft conversion — engine solid; regression-guarded.
2. Business scenario authoring — foundation only.
3. Catalog → policy# → render — ✅ proven live.
4. Model-gap tooling (GhostDraft model + CDM) — tooling + skill built.
5. Extras — regression harness ✅.

**Most recent focus (Goal 1 for quotes):** all 419 quote forms convert; 829/1439 fields (58%) bound to a
project-owned "Quote" concept model we created. See memory `project-quote-concepts`.

## Repo / environment state

- Repo: `C:\src\fact-pdf-tools`, branch **`main`** — **~30 paths of UNCOMMITTED work from this session**
  (all of the above). Nothing committed yet. If continuing substantially, consider committing to a
  feature branch first (main is default). The user has NOT yet asked to commit — confirm before doing it.
- Projects: `core` (engine), `population` (CDM field maps), `server` (ASP.NET API :5035), `demo` (CLI
  tools), `client` (React/Vite :5173). **All 4 .NET projects build clean.** Client has node_modules.
- Sibling repos used: `C:\src\fact-commercial-api` (CDM `MoE.CommonDataModel`; CDM hydration SQL under
  `MoE.Commercial.Api/Components/Data/GetPolicy/`), `C:\src\FaCT-DocProd-Development\mstrres\MOEC0`
  (FAP/DDT/DAL/FORM.DAT — the form library the server is configured to read).
- GhostDraft assets (user's machine): `C:\Users\cmorehouse\Downloads\Moe Proprietary\` — `Resources\Model
  Libraries\Model Library.gdm` (concept catalog), `Resources\Scenarios\` (test data), `Documents\` (real
  authored `.gd` — the format ground truth), `Resources\Stationery\Logo.gd` (MoE logo PNG).

## Key gotchas (don't relearn these)

- **Config**: `server/appsettings.Development.json` points FormsDirectory/DdtDirectory/FormDatPath at the
  `FaCT-DocProd-Development` tree. `server/appsettings.json` CommercialApi URLs were DEAD Azure hosts —
  fixed to `https://pointmoeapps-{env}.mutualofenumclaw.net/commercialapi` (needs MoE network). Server
  uses IOptions → restart after config edits.
- **Can't render `.gd` locally** (no GhostDraft). The true visual check is the user opening a `.gd` in
  **GhostDraft Designer**. You CAN rasterize a *PDF* to PNG via `demo render-preview <pdf>` (Spire) and
  view the PNG. The Read tool can't render PDFs (no poppler).
- **Bash heredocs mangle backslashes** — write Python/scripts with the Write tool, not `<<'EOF'`.
- **Stale `dotnet run` processes** lock DLLs → "file locked" build errors. Kill the listener on :5035
  (PowerShell `Get-NetTCPConnection -LocalPort 5035`) before rebuilding server.

## Tooling (the `demo` CLI — your workhorse)

Run from `cd C:\src\fact-pdf-tools\demo`:
- `dotnet run -- regress` — **golden-file regression for the `.gd` generator (8 curated forms). ALWAYS run
  after changing the generator.** `regress capture` re-baselines (do this intentionally after a
  verified-good change; new bindings change `.gd` markup and WILL show CHANGED).
- `dotnet run -- convert-quotes [PREFIX]` — batch-convert quote forms (default `Q*`) → `output/quote-forms-gd/`
  + CONVERSION-REPORT.md.
- `dotnet run -- make-concept-library` — emit the extended `.gdm` (real Model Library + our Quote concept)
  → `output/concept-library/Model Library (with Quote).gdm`.
- `dotnet run -- gap <FORM>` — per-field DDT provenance + GhostDraft-bound? + on-CDM? (see skill
  `form-gap-analysis`).
- `dotnet run -- coverage` — scan all ~4400 forms → `output/coverage-report.*`.
- `dotnet run -- render-preview <pdf>` — rasterize a PDF to PNGs for visual inspection.

## The engine (core/Infrastructure/FapToGhostDraftGenerator.cs)

FAP geometry + FXR fonts → RTF. Non-WIP forms use `BuildRtf`: prose→paragraphs, columnar rows→RTF tables
(`\trowd`), grids (FAP `X,` rectangles)→bordered tables. Right-justified value columns detected via shared
`col2`. `\trleft0` + `\li` for indentation (never `\trleft`, which shifts the whole table). Fonts from
`FxrFontLibrary`. Bindings via `LookupBinding` (per-form maps → DefaultFieldMap → `ProjectConcepts` Quote
fallback). `DescribeBinding()` exposes bindings for the gap tool. The WIP shape builder's no-background
path is BROKEN in GhostDraft — don't route plain forms to it.

## Live threads / next steps (pick up here)

1. **User must import** `output/concept-library/Model Library (with Quote).gdm` into GhostDraft and confirm
   Quote bindings resolve + spot-check a batch of `.gd` in Designer. (Nothing here is GhostDraft-render-verified.)
2. **Open design question for the user:** the Quote model is FLAT (all premiums → one `Premium` concept,
   etc.). Decide whether to keep flat or restructure into repeating coverage/location/vehicle sections (a
   more faithful data model — bigger design). Don't expand further without this decision.
3. **Long-tail quote bindings** (~610 unbound): low-frequency/ambiguous fields (FIELD, VSDS, CONTMSG,
   `> BYSLOC` repeating markers, per-form one-offs). Diminishing returns; some need per-form context.
4. **Goal 4 precision:** the on-CDM check flags DAL-sourced fields as "via DAL (resolve)". Automating
   DAL-script column extraction (DAL scripts in `...MOEC0\DAL\*.DAL`) would make it column-precise.
5. **Goal 1 fidelity gaps:** images/logos (chrome; `G,`→`.LOG`, reuse GhostDraft Stationery assets — see
   ROADMAP), dense multi-col grid (`QTE_COVAUTOSYM`). Regression harness makes these safe to attempt.
6. **Goal 2** (scenarios) and **Goal 3** polish (more `IFormFieldMap`s) are open but lower priority.

## Memory files (durable facts — auto-loaded each session)

`fap-ghostdraft-export-pipeline`, `project-quote-concepts`, `ghostdraft-model-library`,
`commercial-api-endpoints`, `form-data-gap-analysis`, `population-commondatamodel-dependency`.
Update these (not this handoff) as facts change.
