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

## 3. ~~The blocker for the GhostDraft half~~ — WITHDRAWN, there is no blocker

**This section was wrong, and it steered the plan for two sessions. Corrected 2026-08-27.**

What it said: the 427 converted `.gd` bind to a Quote concept model, root guid
`bffb5190-0d36-e2ce-68e2-79a723e733b8`; that guid is in neither GhostDraft package we hold;
therefore a **GhostDraft Quote package** exists somewhere that we do not have, and obtaining
it (Action 1) gates the entire GhostDraft half.

What is true:

```
MD5("fact-pdf-tools/QuoteLib/Quote")  ->  bffb5190-0d36-e2ce-68e2-79a723e733b8
```

That is `ProjectConcepts.RootGuid` — **a guid this repo derives for its own Quote concept
model**, and the guid its own converter binds to (`core/Infrastructure/ProjectConcepts.cs`).
It is absent from the ISO packages because it was never in them. `make-concept-library`
splices it into the real Model Library; that step had simply never been run against the
project we render in.

Measured, not reasoned — every guid in all 428 converted templates checked against the
library `make-concept-library` emits from Cody's World:

| | guids in library | of 871 guid references in the templates |
|---|---|---|
| base `Model Library.gdm` | 588 | **51 resolve (5.9%)** |
| extended (base + our Quote model) | 619 | **871 resolve (100%)** |

The "3.4% resolve" figure that made this look hopeless was measured against a package that
was never supposed to contain our concepts.

**One real bug came out of the check**, and only a guid-level check could have found it:
`Agent/Full Name` was being emitted as `9b7a6e43-…` where the library says `ed04ed36-…`.
Eight references, all on the cover templates — the page we now render. A name-level
comparison would have passed it; both are called "Full Name". Fixed.

### What is actually left

**617 `<path xsi:nil="true"/>`** — fill points the converter has no mapping for. In the 44
templates the CA packet uses, 127 of them: the covered-auto symbol cells, `TOTAL VERBIAGE`,
the TRIA columns (`CAPRM` / `CATERR`), the conditional `"$"` separators, `ME8802 DESC`.

Every one of those is a FAP field `QuotePacketData` already fills for the HTML render
(§4b). Closing them is extending `ProjectConcepts.FieldToAttr` against a list we can print,
not waiting on anybody.

> **Action 1 is withdrawn.** Do not ask Products for a GhostDraft Quote package. The two
> things that *are* needed are in §4c.

---

## 4. Data: it is a QUOTE, not a bound policy

The document header is `Quote # CPQ 0501406`; the named insured is a test quote
(`FACT-2571 CPP W/O PKG MOD AZ RSX GL LOADED`). So the data source is a **quote**.

What is proven to work today (this session): `CommercialApiPolicyClient` →
`GET {baseUrl}/api/policy/{number}` returns a CDM `Policy`, and real values came back from **tst**
(`https://pointmoeapps-tst1.mutualofenumclaw.net/commercialapi`, policy `BAP000000516`).

> **Action 2 — RESOLVED 2026-08-27. A quote is retrievable, on the same endpoint.**
> `GET {baseUrl}/api/policy/{number}?scope=Pending` returns the in-progress transaction, which is
> the quote; `scope=Verified` (the API default) returns the issued policy.
> `CommercialApiPolicyClient.GetPolicyAsync` takes a `scope` now.
>
> The `CPQ 0501406` in the reference document is a **Point-side quote number**, not a CDM key —
> `CPQ050140600` is not addressable. The CDM key is the ordinary policy number, and
> `policy_search --statusCodes P` lists the pending ones.
>
> **Most pending quotes in tst are UNRATED**: they carry the units and coverages but every
> `totalPremium` and coverage `premium` is zero, and only the line-level `PremiumTotals` are
> populated. Of 20 pending CA quotes sampled, 3 were rated. **`BAP000080307`** (WY, 6 vehicles,
> all rated, `line.Premium` 9,810 reconciling exactly against the units and line coverages) is the
> one the POC uses. Pick a rated quote or the per-vehicle columns render blank.
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

## 4b. The assembler exists — it was in FORM.DAT and the DDTs all along

*Added 2026-08-27. This replaces "there is no assembler yet" in §6 phase 1 step 7.*

DocProd's own packet-assembly instructions are on disk in two places nothing had read:

| where | what it gives |
|---|---|
| **FORM.DAT** | the ordered top-level image list per entry, e.g. `;MOE;CPP;QUOTE CPPSUM.1;;RD;;QTE_HDR\|D3SOX…/QCPPSUM_HDR\|D3S…/…` — and the pagination role in the flags: `OX` = the image that repeats as the page **header**, `OY` = the one pinned as the **footer** |
| **each image's DDT `<Image Rules>`** | `PNTAddImgAfterCurImg` — the data-driven children, with the extract table and column filter that decide how many; `SetOrigin` — `Rel+0,Max+0` flows underneath, `Abs+0,Abs+25200` pins; `SetImageDimensions` — the height |

`tools/ddtpacket.py` reads them and expands the graph. `tools/htmlpacket.py` is the assembler:
it emits each image through the existing `emit-html` (cached; the proven renderer is reused
unchanged), stacks the fragments, paginates against the pinned footer and repeats the header.

For the whole CPP quote packet that is **277 placements over 160 distinct images, and every one
of the 160 has both a FAP and a DDT on disk.** Nothing is missing.

**Two rules that are not obvious, both recovered from the reference document rather than guessed,
and both of which were wrong first:**

* **`PNTAddImgAfterCurImg` runs backwards.** Every rule in a section anchors on the same image, so
  each insertion pushes the previous one down and the *last* rule written renders *first*.
  Two independent confirmations: `QCPP_CAV` lists the "Total Vehicle Premium" row before the
  coverage rows it totals, and `QCPPSUM_HDR` lists GL before CP where the reference prints
  Commercial Property above Commercial General Liability.
* **`SetOrigin Max+0` advances by INK, not by the declared box.** Declared heights are design-time
  allocations and run large — `QTE_BILLINFO_A` declares 10,500 units and draws 6,369. Measured on
  our WY quote's summary page: 25,500 units by declared height against a footer pinned at 25,200,
  so it spilled to a second page; the reference document fits *more* rows than we have on that one
  page, which the declared model cannot do at any row count. By ink the same page measures 19,644
  and fits. `--flow declared` keeps the old model for comparison.

**Also settled by comparing against the reference:** `QUOTE CPPSUM.2` — not `.1` — is the page the
reference document prints. `.2` is the three-column `Premium ($) / *TRIA ($) / Total Premium ($)`
layout built from the `_A` images (`QCPPSUM_HDR_A`, `QCPPSUM_CA_A`, `QCPPSUM_TOTAL_A`,
`QTE_BILLINFO_A`). Both are populated by the builder.

### The data half

`population/QuotePacketData.cs` maps each driving extract table onto the CDM collection that
stands in for it and emits instance counts plus field values:

```
QCOV[INSLINE=CA]      -> the auto Line's Coverages
ASB5CPL1[B5AGTX=CA]   -> Policy.InsuredAssets (the vehicles)
AUTCOV[BYAENB=<unit>] -> that asset's Coverages
ASBECPL1[BEB8NB=…]    -> Policy.Forms   (form code + edition selects the schedule variant)
PMSP0000 / PMSP0200   -> the policy header itself
```

A **zero** count is how the conditional variants get pruned. On `BAP000080307` (CA line only, WY)
that suppresses the other five summary rows, the terrorism row, all five endorsement schedules and
the Montana auto schedule, with no hand-maintained list.

```bash
demo/bin/Release/net9.0/FapPdfTools.Demo.exe quote-data BAP000080307 tst Pending output/quote-poc/BAP000080307.json
python tools/htmlpacket.py --lob CPP --form "QUOTE CPPSUM.2" --form "QUOTE CPPCA.1" \
    --data output/quote-poc/BAP000080307.json --out output/quote-poc/wy-quote.html --pdf
```

4 pages, 216 CDM values spliced and verified by re-reading the output. Artefacts:
`output/quote-poc/wy-quote.{html,pdf}` and `wy_p1..4.png`.

### The oracle that settled all of this

*Added 2026-08-27, second pass.* Products supplied **a legacy DocProd render of the same
quote** — `CPQ 0509752`, which is `BAP000080307` — and it is a far better oracle than the
generic reference document. Compare against it page by page; almost every answer below was
already on disk once the render said where to look.

```bash
demo/bin/Release/net9.0/FapPdfTools.Demo.exe quote-data BAP000080307 tst Pending output/quote-poc/BAP000080307.json
python tools/htmlpacket.py --lob CPP \
    --form "QUOTE COVER.4" --form "QUOTE CPPSUM.2" --form "QUOTE CPPCA.3" \
    --data output/quote-poc/BAP000080307.json --out output/quote-poc/wy-quote.html --pdf
```

**The packet the reference actually prints is `QUOTE COVER.4` + `QUOTE CPPSUM.2` +
`QUOTE CPPCA.3`** — the `_A`/`_B` image variants, not the plain ones.

Three sources that were not being read, and each fixed a visible defect:

| source | what it settles |
|---|---|
| `TABLE/MOE_ASAH.TBL`, keyed `"CPPCA CA    " + code` | the coverage WORDING. Products asked where our descriptions came from — they were `Coverage.Description` off the CDM; legacy's are this table, which `QUOTE.DAL` looks up |
| `DAL/QUOTE.DAL`, `CPPQ_CAA_*` / `CPPQ_CAV1_*` | the three computed columns: RENTAL names the physical-damage coverage it reimburses and prints `days/perDay` ("30/75"); UM/UN name their limit basis ("Single Limit (BI)"); LOANCM/LEASCM take the unit's comprehensive deductible and LOANCO/LEASCO its collision deductible |
| the DDT's `<Image Field Rules Override>` | `MODE=R` — **right justify**. It marks exactly the Limit / Deductible / Premium columns, so the alignment is now read from the DDT for every image rather than styled by hand |

Two of our own rendering bugs the comparison exposed:

* **Every page went bold** once the EA9911 schedule was in the packet. `@font-face` rules
  were deduplicated by family name alone, so `QCPP_CAV_B` — which declares only the bold cut
  of `F_UniversATT` — claimed the name document-wide. Keyed on family+weight+style now.
* **An over-wide value only ever shrank.** Right when something sits to its right, wrong when
  nothing does: `VEH1 STATE` reserves one glyph for a 15-character value, so "WYOMING"
  rendered at 5pt in a header line with nothing after it. It grows into free space now.

### What the oracle still shows as different

| | |
|---|---|
| **vehicle 2 is missing a coverage** — legacy prints "Amusement Devices 500,000 434", we print six rows summing 495 against a stated `totalPremium` of 929 | a **CDM gap, not ours**: `InsuredAssets[1].Coverages` has six entries and none is AMUSE. Legacy reads it from the AUTCOV extract. Worth raising — a quote proposal that silently drops a $434 coverage is a correctness problem for any consumer of this CDM, not just us |
| the header's second line, `WYAUTO03`, is blank for us | no CDM property carries it. `InsuredParty` has only `FullName`; `Policy.Code` is `MONO` |
| we print `BAP000080307` where legacy prints `Quote # CPQ 0509752` | the CPQ number is Point-side and is not in the CDM (see Q6/Q7) |
| three limit cells differ in the line table — NonOwned Donors and NonOwned Volunteers show 500,000 for us and blank in legacy, POLLPP the reverse | `CPPQ_CAA_LIMIT` has per-coverage branches we have not ported; the pattern is known, the work is mechanical |
| DOC Comprehensive / DOC Collision show no deductible where legacy shows 500 | same DAL, same shape as the LOANCM/LOANCO inheritance already ported |
| coverage ORDER is empirical | legacy orders by the extract's `BYC0NB`, which the CDM does not carry. Two hand-written orders in `LegacyCoverageText` reproduce the reference's line table and both vehicle tables exactly — but they are fitted to one document |
| picking between two editions of one form code is inferred | EA9911 is on the line at both 2018-03 (seq 32) and 2024-12 (seq 38), both `actionCode` "A". Legacy prints 03 18, so lowest sequence wins. If a future quote prints the later edition, this rule is wrong |
| the payment-plan table draws horizontal rules where legacy draws full cell borders | `emit-html`'s sibling-row heuristic on `QTE_BILLINFO` (`FORM-STUDIO-PLAN` §32); a fidelity item, not an assembly one |
| the terrorism notices (COM126/COM127, reference pages 13–14) are unmapped | the FORMS AND ENDORSEMENT SCHEDULE and the Auto Summary are done — see below |
| `quote-data` and `fill-html`'s `IFormFieldMap` registry are two population paths | the packet builder covers repeating images, the maps cover single forms; they should converge |

---

## 4c. GhostDraft — ONE template, built and verified

*Rewritten 2026-08-27 (third pass). Products settled the scope and both open decisions:*

* **Scope:** *"for this POC we make one form that has all these elements on it; in the
  future we can figure out how to split them out more intelligently."*
* **Render target:** `…\Documents\GhostDraft Studio\Cody's World\Cody's World.gdproj`.
* **Server XML:** the DocGen project carries the builder.

A single template needs a single bindable root, so everything hangs off `Quote`.

### The model — declared once, three artefacts

`tools/quotemodel.py` is the single declaration. Three things must agree exactly or
nothing renders, and all three are generated from it:

| artefact | who consumes it |
|---|---|
| `.gdm` concept library | GhostDraft Studio edits against it; `CreateSnapshot` validates markup against it |
| `model.xml` | what a template's guid paths resolve through — `tools/gdmodel.py`, `tools/gdauthor.py` |
| the Server XML element names | what the DocGen builder must emit |

GUIDs derive exactly as `ProjectConcepts.cs` derives them (MD5 of
`fact-pdf-tools/QuoteLib/<key>`), so the C# and Python sides agree without a shared file.

The `.gdm` grammar for a repeating table was **read off the real Model Library**, not
guessed — there is no `isList` attribute, and the concept for a list is not where you
would look for it:

```xml
<concept xsi:type="ListConcept" name="Model Library_Quote_Vehicles"
         elementName="Vehicle" hiddenConceptName="Model Library_Quote_Vehicles_Elements">
  <selectors>… First / Last, singleton …</selectors>
  <elementKinds><conceptName>…_Elements</conceptName></elementKinds>
</concept>
```

and the parent points at it with an `attributeState="AttributeWithKinds"` attribute.
Six lists, thirteen concepts, one `domainModel`.

```bash
python tools/quotemodel.py --print          # the model
python tools/quotemodel.py --gdm --model    # the library and the compiled model
```

### The template — authored, bound, grammar-checked

```bash
python tools/quotetemplate.py
  247 instructions, 118 bindings, ALL resolved against the model
  7 listInstructionType, 47 conditionalInstructionType, 64 fillPointType
  0 <path xsi:nil="true"/>
  GRAMMAR CHECK PASSED -- all five rules re-derived from the written file
```

Set that against the 428 mechanically converted quote templates: **0 lists, 0
conditionals, 617 nil paths between them.** Those are layout; this is a form. Sections
follow the packet order §4b recovers, and the wording is the wording the HTML render
uses, so the two can be diffed value for value.

### The data — one source, two renderers

`tools/quotexml.py` turns the **same** `quote-data` document that feeds the HTML render
into Server XML:

```
1 InsuranceLine · 14 Endorsement · 6 Vehicle · 33 Coverage · 14 LineCoverage · 37 Form
```

so a value that differs between the HTML and the GhostDraft render is a bug in a
renderer, not a data question. It is also the **worked example for the DocGen builder** —
whatever emits this in production has to emit this shape.

One rule in it is load-bearing: an empty value is **omitted**, never written as an empty
element. Every conditional in the template turns on `is provided`, which is false for an
absent element and true for an empty one, so `<Limit/>` would print a label with nothing
after it.

### The one step no tool here can take

**Rendering.** Every GhostDraft UI is a GUI (Studio, Server.TestClient, Data Workbench),
so a PDF comes from `GhostDraftServer/RestAPI/AssembleDocument` against a **published**
template, with credentials. `gdvalidate.py` gets us GhostDraft's own compiler and
`CreateSnapshot`'s markup check without a GUI, and that is as far as a shell reaches.

To finish the POC someone needs to: point the project's Model Library resource at
`output/concept-library/Model Library (with Quote).gdm`, add
`output/quote-poc/Quote Proposal.gd` to the project, publish, and assemble it against
`output/quote-poc/BAP000080307.serverxml`.

**The oracle for values is the HTML render; the oracle for layout is the Products render
of the same quote.** Neither is the existing GhostDraft template library — Products:
those are hand conversions with human tweaks and are **not** an oracle (§51).

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
1. ~~Get the GhostDraft Quote package export.~~ **WITHDRAWN** — the concept root is ours, not a
   missing package (§3). Nothing gates the GhostDraft work; the plan is §4c.
2. ~~Confirm a quote is retrievable as CDM through the commercial API.~~ **DONE** — `?scope=Pending`,
   see §4.
3. ~~Pick the exact target.~~ **DONE** — the CPP quote proposal for Commercial Auto, `QUOTE CPPSUM.2`
   plus `QUOTE CPPCA.1`, against the rated WY quote `BAP000080307`.

### Phase 1 — HTML (no external dependency) — **the vertical slice is closed**
4. ~~`emit-html` each chosen fragment.~~ **DONE**, and cached by `htmlpacket.py`. Still true: FAP2PDF
   renders these fragments nearly blank so the legacy oracle is invalid for them
   (`HANDOFF.md` → environment gotchas). The oracle that *is* valid is the Products reference PDF,
   and it settled two assembly rules this session (§4b) — compare against it, page by page.
5. ~~One field map per fragment.~~ **DONE differently, and better**: a per-form `IFormFieldMap`
   cannot express "one instance per vehicle". `population/QuotePacketData.cs` emits instance counts
   *and* values for the whole packet in one pass. Extending it to a new line means adding that
   line's CDM→image mapping there, not a new class per fragment.
6. ~~`fill-html` against a real quote, render, inspect.~~ **DONE** — `htmlpacket.py --data`.
7. ~~Assembly.~~ **DONE** — and it is not a hand-written ordered list; it is read from FORM.DAT and
   the DDTs (§4b), so it will also feed the GhostDraft `subscriptionType` graph without a second
   source of truth.

**What Phase 1 still owes:** the gaps table at the end of §4b — page numbers in the footer, the
cover and disclaimer pages, the forms schedule and terrorism notices, and the `QTE_BILLINFO` cell
borders. None of them are structural; all are one mapping or one fidelity rule each.

### Phase 2 — GhostDraft — **superseded by §4c**, which is measured rather than assumed
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
| ~~Q1~~ | ~~Does a **GhostDraft Quote package** exist?~~ **Withdrawn — no such package is needed, see §3.** The concept root is ours; `make-concept-library` supplies it. |
| Q2 | Commercial Auto **or** Farm first? The fragments are per-line (`QCPPSUM_CA` vs the Farm set), so it is a scoping choice, not extra architecture. |
| Q3 | The **payment-plan table** (page 3) and the marketing copy (pages 1–2) — static boilerplate, or data-driven? Changes whether they need bindings at all. |
| Q4 | Two renderers currently format a policy number differently: fact-docgen's `FormatPolicyNumber` gives `BAP 0123456 00` (14 chars) while `Mcs90aFieldMap` uses the raw 12. Which is correct? It is visible on any dec/quote page. |
| Q5 | `CONVERSION-PLAN.md` D7 — does the HTML field name mirror CDM exactly, or is there a curated form-facing vocabulary? |
| Q6 | *(new)* Is `BAP000080307` an acceptable demo quote, or is a specific one wanted? It is a WY Commercial Auto test quote with 6 rated vehicles. Most pending quotes in tst are unrated and render blank premium columns. |
| Q7 | *(new)* The reference document prints **`QUOTE CPPSUM.2`**, the TRIA three-column summary, and **`QUOTE CPPCA.3`** for the auto detail. Is `.1` ever the right page, and if so what selects between the variants? Right now the choice is ours, not the data's. |
| Q8 | *(new)* **The CDM drops a coverage.** On `BAP000080307` vehicle 2 the legacy render prints "Amusement Devices 500,000 434"; the CDM's `InsuredAssets[1].Coverages` does not contain it, so the rows sum to 495 against a `totalPremium` of 929. Is this a known gap in the auto coverage projection? It affects anything reading the CDM, not just the quote proposal. |
| Q9 | *(new)* The legacy header's second line is `WYAUTO03` and nothing in the CDM carries it. What is it — a DBA, an account/unit reference, an agency's own label? |

---

## 9. Session state

**2026-08-27** added, on `features/form-studio-p0`:
`tools/ddtpacket.py` (the packet graph, from FORM.DAT + DDT `<Image Rules>`),
`tools/htmlpacket.py` (the assembler), `population/QuotePacketData.cs` (the CDM data half),
the `quote-data` command in `demo/Program.cs`, and a `scope` parameter on
`CommercialApiPolicyClient.GetPolicyAsync`.

Artefacts: `output/quote-poc/wy-quote.{html,pdf}` — a populated 4-page CA quote proposal from
`BAP000080307` — plus `wy_p1..4.png`, `packet-cpp-quote.csv` (277 placements),
`cpp-quote.{html,pdf}` (the whole 14-page packet unpopulated), and `frag/` (the per-image
`emit-html` cache).

Earlier, committed on the same branch:
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
