---
name: form-gap-analysis
description: For a legacy FAP form, determine where each field's data comes from (DDT provenance) and whether that data is captured on our models — the GhostDraft Model Library (for binding) and the Commercial API CDM (MoE.CommonDataModel) — or needs to be added. Use when asked "what data does this form need", "is this form's data on our model", "what's missing for form X", or when preparing a form for GhostDraft binding / real-data population.
---

# Form data-gap analysis (Goal 4)

Answers, per form field: **where does the data come from**, and **is it on our models or does it need adding.**
Do NOT guess from field names — they're cryptic (OF, ISSUEDTO, POLICYNUM). The DDT is authoritative about
provenance; the CDM *hydration* code is authoritative about what DB2 data reaches our model.

## 1. Provenance + current binding (deterministic, local)

Run the gap tool from the `demo` project:

```bash
cd demo && dotnet run -- gap <FORM>
```

For each field it prints the DDT **provenance**, whether our converter already binds it to a GhostDraft
concept, and (for `system` fields) an automated **ON CDM?** check:
- `system`  — computed from policy data (DDT method `DAL`, or a table method like `concat`/`movedate`). **These are the fields that need CDM data + a GhostDraft concept.**
- `manual`  — DDT method `powtype` (WIP user entry). No data source; nothing to map.
- `constant`— DDT method `mk_hard` (hard-coded literal, e.g. a phone number).

The ON CDM? column is computed locally: it extracts the field's DB2 columns from the DDT `Source` and
greps the CDM hydration SQL (`fact-commercial-api/.../GetPolicy/*.cs` + `Data.Provider/Mapping`). Result:
`yes` (columns hydrated → on our model) / `NOT hydrated` (real gap) / `via DAL (resolve)` (DAL-sourced —
its real columns live in the DAL script; do step 3 for column-precise confirmation). Only `system` fields
matter; `manual`/`constant` need nothing.

## 2. Is the concept in the GhostDraft Model Library?

`ModelLibrary.Load(<Model Library.gdm>)` (`core/Infrastructure/ModelLibrary.cs`) exposes the catalog
(~300 bindable attributes). The tool flags fields our converter already binds; for the rest, check whether a
suitable concept exists in the library. **Note:** the current library is Commercial-Auto-centric — it has no
Farm concepts (see memory `ghostdraft-model-library`). If no concept exists, it must be added (step 4).

## 3. Is the data on the CDM (our model)?  — trace the hydration, don't literal-match

Legacy DB2 column names (POLICY0NUM, EFFDATE, LONGNAME) do NOT appear verbatim in the CDM C# (which uses
semantic names like `Policy.Number`), so `form_cdm_gap_analysis` alone FALSE-reports "missing". The reliable
chain, using the commercial MCP + the hydration source:

1. `form_resolve_file_name` → `form_parse_ddt <fileName>` — real DDT field rules + `dalFunctions`.
2. `form_resolve_dal <dalFunctions>` — the actual DB2 tables/columns each DAL function reads.
3. Check the **CDM hydration SQL** for those columns — they appear as raw column names there:
   `fact-commercial-api/MoE.Commercial.Api/Components/Data/GetPolicy/*.Queries.cs`
   (`SELECT <DB2COLUMN> AS <SemanticName> FROM {0}.PMSP0200 ...`) and
   `MoE.Commercial.Data.Provider/Mapping/MappingPipeline.cs`.
   If the column is SELECTed there, the data is hydrated into the CDM (**on our model**); if not, it's a real gap.
   (Cross-check with `db2_get_table_schema` / `db2_list_tables` for the table's columns.)

Beware the naming layers: DDT form-field name → DAL intermediate → raw DB2 column → SQL alias → CDM property.
Resolve the DAL first to get the REAL columns before searching the hydration.

## 4. Add what's missing (deterministic augmentation)

- **CDM gap** (data not hydrated): add the column to the relevant `GetPolicy/*.Queries.cs` SELECT + its
  target property in `MoE.CommonDataModel` — this is a fact-commercial-api change; hand off or open a ticket.
- **Model Library gap** (no GhostDraft concept): add a `<concept>`/`<attribute>` (name, new GUID, type
  Text/Date/Currency, optional adornment) to `Model Library.gdm`, then reference it from
  `FapToGhostDraftGenerator`'s per-form field map so the `.gd` binds it.

## Domain context

Commercial MCP: `db2_get_table_schema`, `db2_list_tables`, `form_field_summary`, `get_instructions`.
Skills: `policy-builder` (DB2 tables/joins), `rate-keys` (S3B3TX layout).
Related memory: `form-data-gap-analysis`, `ghostdraft-model-library`, `commercial-api-endpoints`.
