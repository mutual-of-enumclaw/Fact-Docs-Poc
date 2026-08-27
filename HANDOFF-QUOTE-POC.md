# Handoff — Quote Proposal POC (Commercial Auto / CPP)

**Written 2026-08-26.** Continues `HANDOFF.md` + `FORM-STUDIO-PLAN.md` §40–52 and `CONVERSION-PLAN.md`.
Branch `features/form-studio-p0` in `C:\src\fact-pdf-tools`.

**The goal, from Products:** convert a **Quote Dec Page / quote proposal** for Commercial Auto (and
Commercial Farm if time allows), render it in **HTML *and* GhostDraft**, populated with real CDM
data. MVP = *it renders and it is populated*.

Reference document: `C:\Users\cmorehouse\Downloads\Example QUote Dec.pdf` — read it before anything
else. What follows is its anatomy.

---

## 1. What the target document actually is

**Not a single legacy form.** It is a 14-page assembled packet, mostly data-driven repeating tables.
Header on every content page: `<quote name>` … `Quote # CPQ 0501406`.

| pages | content | shape |
|---|---|---|
| 1 | Branded cover: banner image, marketing copy, quote name, agency + phone, proposed policy period | static + 4 fields |
| 2 | Disclaimer text + banner image | static |
| 3 | **Premium summary by Insurance Line** — Commercial Property / Commercial General Liability, each with a nested endorsement list, `Premium ($) / *TRIA ($) / Total Premium ($)`, then **Total Premium**; plus an **Available Payment Plans** table | repeating: line → endorsements |
| 4 | "Additional Information" (blank in this example) | static |
| 5–7 | **General Liability detail** — limits header (Occurrence / Aggregate / Product Agg / P&AI / Fire / MedPay), a coverage table, an endorsement-benefit schedule (EG9901), then **per-Location class-code tables** with Exposure / Deduct / Premium / TRIA / Tot Prem and a per-location total | repeating: location → class code → coverage |
| 8–9 | **Commercial Property detail** — coverage table, then endorsement benefit schedules (EP9901, EP9923, EP9924) | repeating + long static schedules |
| 10 | **Location / Building detail** — construction, protection class, coverage rows | repeating: location → building |
| 11–12 | **FORMS AND ENDORSEMENT SCHEDULE** — coverage line / form number / edition date / description | repeating: form list |
| 13 | COM 126 01 21 Terrorism disclosure notice | static legacy form |
| 14 | COM 127 08 15 Rejection of Terrorism Coverage — policy number, named insured, effective date | legacy form + 3 fields |

**Consequences for the plan**
* It is a **packet-assembly** problem as much as a form problem — `CONVERSION-PLAN.md` W5.5.
* Almost every page is a **repeating table**. The GhostDraft constructs for that are decoded and
  proven (§40, §44): `listInstructionType` + selector + `orderByList`, and `gdauthor.py` emits them.
* The example is **CPP** (Property + GL). Commercial Auto is the same skeleton with the CA line.

---

## 2. The good news: the fragments exist, and 427 are already converted

`C:\src\FaCT-DocProd-Development\mstrres\MOEC0\FORMS` holds **667** quote-packet FAP fragments, and
their names map onto the packet almost one-to-one:

```
BQ-HDR, BQ-HDR1/2        page header
BQ-QINS1/2/3/3A/QINSGE   Insurance Line summary          (page 3)
BQ-QPREMDET, -DET2, -DT2A  premium detail
BQ-QLOC1, BQ-QLOCTOT     per-location table + location total (pages 5-7)
BQ-TERM, BQ-TERO         terrorism / TRIA
BQ-TOT                   totals
BQ-BP1..4, BQ-BPH/BPZ    payment plans / boilerplate
PSCP-CA, -CAA, -CAB,     proposal schedule, Commercial Auto (and -CAH/-CAM variants)
QCPPSUM_{CA,CP,CR,GL,IM,PL}       per-line summary       (page 3 rows)
QCPPSUMDTL_{CA,CP,CR,GL,IM,PL}    per-line detail        (pages 5-9)
COM126, COM126B/C/D, COM127B/C/NQ terrorism notices      (pages 13-14)
```

**`output/quote-forms-gd/` already contains 427 of these converted to `.gd`** (from the `main`
branch's work, regenerable with `dotnet run --project demo/FapPdfTools.Demo.csproj -- convert-quotes`),
including `QCPPSUM_CA`, `QCPPSUMDTL_CA`, `QCPPSUM_CP`, `QCPPSUMDTL_GL` — the exact fragments this
packet is built from.

---

## 3. The blocker for the GhostDraft half — read this before promising a date

Those 427 converted `.gd` files bind to a **Quote concept model**, root guid
`bffb5190-0d36-e2ce-68e2-79a723e733b8`. Measured this session (§44.4):

| | |
|---|---|
| fill points emitted across the 427 | 1,480 |
| resolve against **MoE Proprietary Commercial Auto 2607.0** | **51 (3.4%)** |
| cite root `Quote` (`bffb5190-…`) | **812** |
| unbound (`<path xsi:nil="true"/>`) | 617 |

`bffb5190-…` is **in neither GhostDraft package we hold**. So there is a **GhostDraft Quote
package** that we do not have, and without it a quote fragment's bindings cannot be resolved or
verified — concept GUIDs are per-package (§43.6).

> **Action 1, and it gates the GhostDraft half: obtain the GhostDraft Quote package export**
> (`.gdsp`, plus its GDXSD and Integration Specification if available). Everything in §40–52 then
> works on it unchanged — extract under `output/iso-packages/<name>/` and point the tools at it.

If it does not exist yet, the GhostDraft half becomes "author the quote fragments against a model
that must first be built", which is a different and much larger project — say so rather than
absorb it.

---

## 4. Data: it is a QUOTE, not a bound policy

The document header is `Quote # CPQ 0501406`; the named insured is a test quote
(`FACT-2571 CPP W/O PKG MOD AZ RSX GL LOADED`). So the data source is a **quote**.

What is proven to work today (this session): `CommercialApiPolicyClient` →
`GET {baseUrl}/api/policy/{number}` returns a CDM `Policy`, and real values came back from **tst**
(`https://pointmoeapps-tst1.mutualofenumclaw.net/commercialapi`, policy `BAP000000516`).

> **Action 2: confirm a quote (`CPQ…`) is retrievable through the same endpoint**, or find the quote
> endpoint. The `moe-commercial` MCP has `policy_search` / `policy_get` and the policy-builder skill
> notes `policyType="Q"` for quotes, so quotes are addressable — just confirm the shape the CDM
> returns for one.
>
> **Careful:** the base URLs in `CommercialApiPolicyClient`'s docstring and in stale `bin/` copies
> are `*.azurewebsites.net` hosts that no longer resolve. The live ones are in
> `server/appsettings.json` under `CommercialApi.BaseUrls`. `fill-html` already resolves an env name
> from there.

The premium data the packet needs (line premium, TRIA, per-location, per-class-code) is aggregation,
not attribute reads — same shape as the dec page's `GetRecSum` fields. On the CDM side the
equivalents exist: `AutoLinePremiums` / `PremiumTotals` with `LiabilityPremium`, `MedPayPremium`,
`PersonaInjuryProtectionPremium`, `CollisionPremium`, `ComprehensivePremium`,
`SpecifiedPerilsPremium`, `TowingPremium`, `UninsuredMotoristPremium`,
`UnderinsuredMotoristPremium`, `EstimatedAdditionalPremiumForEndorsements`, and
`CommonAutoLine.Premium`.

---

## 5. What is already built and working (use it, do not rebuild it)

### HTML: convert + populate from real data — **proven end to end**
```bash
dotnet run --project demo/FapPdfTools.Demo.csproj -- emit-html <FORM> out.html
dotnet run --project demo/FapPdfTools.Demo.csproj -- fill-html <FORM> <EDITION> <POLICY> in.html out.html tst
"C:\Program Files\Google\Chrome\Application\chrome.exe" --headless --disable-gpu \
   --no-pdf-header-footer --print-to-pdf=out.pdf "file:///…/out.html"
```
`emit-html` emits `<span class="abs field" data-field="<FAP field name>" data-maxlen data-fid style="…">`
for every `F,` field, with the form's own font, positioned from `InlineFieldPositions` (the A,T1
anchor — the visual position; the `F,` record's is not). `fill-html` fetches a real policy, runs the
form's `IFormFieldMap`, splices values into those spans, **verifies by re-reading the output**, and
auto-sizes a value that exceeds its box.

Demonstrated: **MCS90A** (3 fields) and **BADECpg1 = DA 00 93 01 08**, the Business Auto dec page —
13 fields from real CDM including `LIABPREM 7,707`, `COLPREM 868`, `COMPPREM 459`, `PIPPREM 127`,
`UNDERPREM 834`, `ESTTOTALPREM 9,995`. See `output/badec-populated.html` / `.pdf`.

### CDM → field values
`population/` + `IFormFieldMap`, one class per form, registered in `fill-html`'s registry in
`demo/Program.cs`. Existing maps: `GenericHeaderFieldMap` (`*` fallback), `Mcs90aFieldMap`,
`Eb2410FieldMap`, `Ca2009FieldMap`, `Ca2146FieldMap`, `BopDecPageFieldMap`, and **new this session**
`BaDecPageFieldMap` — which is deliberately a **mirror of fact-docgen's `ItemTwoSection`**, so the
HTML and the GhostDraft render read the same CDM and a divergence is a bug in one map, not a data
question. Copy that pattern.

### GhostDraft: authoring, validation, packaging
| tool | does |
|---|---|
| `tools/gdmodel.py` | resolve a template's GUID path → Server XML path, against a package's `model.xml` |
| `tools/gdbindings.py` | extract every binding from a package's templates (`--templates` points it at OUR `.gd` output) |
| `tools/gdxsdcheck.py` | verify resolved paths against the package XSD (`--selftest` proves it can fail) |
| `tools/gdspeccheck.py` | diff the extraction against GhostDraft's own Integration Specification |
| `tools/xmlsupply.py` | join template demand to the Server XML fact-docgen really emits |
| **`tools/gdauthor.py`** | **emit a `.gd` with logic** from a `Repeat`/`Cond`/`Fill`/`Static`/`Break` spec; verifies the grammar from the written bytes; `--selftest` mutates 5 ways |
| `tools/gdvalidate.py` | run GhostDraft's own compiler (`CreateSnapshot.exe`) over a Studio project |
| `tools/gdproject.py` | register a `.gd` in a `.gdproj` (**text splice — never re-serialise the manifest**) |
| `tools/gdpackage.py` | rebuild a `.gdsp` with added templates |

**Proven (§51):** a machine-authored `.gd` with a list, five conditionals and eight bindings
**rendered populated PDF from GhostDraft**. That is the whole GhostDraft capability, demonstrated.

Sandbox Studio project Products provided:
`…\Documents\GhostDraft Studio\Cody's World\Cody's World.gdproj` — its
`Resources\Model Libraries\Model Library.gdm` is the same library the proprietary package ships (587
guids in both), and the authored template is registered in it.

---

## 6. The POC plan

### Phase 0 — unblock (do first, in parallel)
1. **Get the GhostDraft Quote package export.** Gates all GhostDraft work (§3).
2. **Confirm a `CPQ…` quote is retrievable** as CDM through the commercial API (§4).
3. **Pick the exact target.** Recommendation: the **CPP quote proposal for Commercial Auto**, scoped
   to pages 1, 3 and the CA line detail — a cover, a line summary with one repeating list, and one
   per-location table. That exercises every construct (static, field, list, conditional, total)
   without the 14-page surface.

### Phase 1 — HTML (no external dependency; start here)
4. `emit-html` each chosen fragment; confirm it renders. **Look at the PDF**, do not trust the log.
   Note: FAP2PDF renders these fragments nearly blank so the legacy oracle is invalid for them
   (`HANDOFF.md` → environment gotchas) — our renderer is the reference here.
5. Write one `IFormFieldMap` per fragment, mirroring whatever fact-docgen builder covers the same
   data. Register in `fill-html`'s registry.
6. `fill-html` against a real quote, render, inspect.
7. **Assembly:** concatenate the fragment HTMLs into one document in packet order. There is no
   assembler yet — the simplest honest version is an ordered list of fragments per line of business,
   which is also the input the GhostDraft `subscriptionType` graph will need.

### Phase 2 — GhostDraft (after Phase 0.1)
8. Point `gdbindings.py` at the Quote package; re-run `gdxsdcheck` / `gdspeccheck` to establish the
   baseline. Then `gdbindings.py --templates output/quote-forms-gd` to measure how many of the 427
   already-converted fragments resolve **against the right model** (the 3.4% figure was against the
   wrong one).
9. For fragments that resolve, register into a Studio project (`gdproject.py`) and validate with
   `gdvalidate.py` — GhostDraft's own compiler, which found a pre-existing error in `EA9940 0194`
   and passed everything else.
10. For fragments that do not, author them with `gdauthor.py` using the §43.5 translation table.
11. Populate from CDM: whatever the Quote package's Server XML root is, fact-docgen needs section
    builders for it. Measure the gap with `xmlsupply.py` before estimating.

### Phase 3 — the honest comparison
12. Render both, side by side, same quote. Any value that differs is a bug in one of the two maps.

---

## 7. Traps that have each already cost time

* **Bash heredocs eat backslashes.** Writing C#/Python containing `\r\n`, `\\` or `\"` through a
  heredoc silently corrupts it. Use the Write/Edit tools.
* **Windows 260-char path limit** — broke extracting the Integration Spec zip (read it in place) and
  broke cloning a Studio project (do not clone; use `--dry-run`).
* **Never re-serialise a `.gdproj` through an XML parser.** It declares its default `xmlns` per
  element; a parser rewrites every element and the added entry lands in no namespace, which Studio
  silently ignores. A real `<document>` entry also needs `<artifactStatusGuid>` and
  `<lockable>false</lockable>`. §49.
* **A `.gdsp` is not a Studio file.** Studio opens `.gdproj`. §47.
* **A `.gd` never carries its own model** — 570/570 production templates have
  `<library xsi:nil="true"/>`. The library comes from the project. §46.
* **`.NET Regex.Replace` with `"$1"` + a value starting with a digit** is parsed as capture group
  101. It silently ate a span and the success counter still said OK. Splice by index and **verify by
  comparing the output, not by counting replacements.** §49, §51.
* **Look at the render.** Six gates and GhostDraft's own compiler passed a template that rendered as
  a single wall of text, and this session twice reported a fix from stdout without opening the PDF.
  Rendering is not inspecting.
* **Do not chase parity with existing GhostDraft templates.** Products: they are hand conversions
  with human tweaks and are **not an oracle**. §51.

---

## 8. Open questions for Products

| # | question |
|---|---|
| Q1 | Does a **GhostDraft Quote package** exist? If not, the GhostDraft half of the quote POC is a model-building project, not a conversion. |
| Q2 | Commercial Auto **or** Farm first? The fragments are per-line (`QCPPSUM_CA` vs the Farm set), so it is a scoping choice, not extra architecture. |
| Q3 | The **payment-plan table** (page 3) and the marketing copy (pages 1–2) — static boilerplate, or data-driven? Changes whether they need bindings at all. |
| Q4 | Two renderers currently format a policy number differently: fact-docgen's `FormatPolicyNumber` gives `BAP 0123456 00` (14 chars) while `Mcs90aFieldMap` uses the raw 12. Which is correct? It is visible on any dec/quote page. |
| Q5 | `CONVERSION-PLAN.md` D7 — does the HTML field name mirror CDM exactly, or is there a curated form-facing vocabulary? |

---

## 9. Session state

Committed on `features/form-studio-p0`:
`FORM-STUDIO-PLAN.md` §40–52, `CONVERSION-PLAN.md`, and `tools/{gdmodel,gdbindings,gdxsdcheck,`
`gdspeccheck,xmlsupply,gdauthor,gdvalidate,gdproject,gdpackage,gdgrammar,gdorder,gdidrule}.py`,
plus `population/Maps/BaDecPageFieldMap.cs` and the `fill-html` command in `demo/Program.cs`.

Artefacts: `output/badec-populated.{html,pdf}` (dec page, real CDM data),
`output/mcs90a-populated.{html,pdf}`, `output/authored-lpc-v2.gd` (authored, renders in GhostDraft),
`output/gdbindings-{ca2607,propca2607,ours}.csv`, `output/xmlsupply-{builder,propca}.csv`.

Extracted packages (gitignored): `output/iso-packages/commercial-auto-2607/`,
`output/iso-packages/proprietary-ca-2607/`.

**Not done, and known:** the FAP+DDT → `gdauthor` spec join (`CONVERSION-PLAN.md` W3.1) — nothing
yet derives a binding spec from a form, so every `.gd` authored so far was hand-specified.
`gdauthor.py` also emits plain-text layout and uses **none** of the FAP geometry (W3.2). Both are on
the path to mass conversion, neither is needed for a single-form POC.
