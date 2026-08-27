# Mass conversion plan — FAP → HTML + GhostDraft, populated with real data

**Status:** plan, 2026-08-26. Written to be handed out in pieces.
**Companion docs:** `FORM-STUDIO-PLAN.md` §40–51 (the evidence behind every claim here),
`HANDOFF.md` (branch/state), `tools/bindgap.py` docstring (why coverage numbers are floors).

Every number in this document is measured and traceable to a section of `FORM-STUDIO-PLAN.md`.
Where something is unknown it says so. **If you are picking up one task, read §0 and your own task;
you do not need the rest.**

---

## 0. The one architectural decision everything else follows from

`FORM-STUDIO-PLAN.md` §40 established this from 491 production GhostDraft templates, verified three
ways (§42: model.xml, the package XSD, and GhostDraft's own Integration Specification — 100% on all
three):

> **The GhostDraft Server XML element paths ARE the logical binding namespace.**
> A form field stores a Server XML path. It never stores a CDM path.

That is already how production works, and it collapses requirement 1 into one pipeline rather than
two:

```
                        ┌──────────────► GhostDraft template  ──► PDF   (GhostDraft server)
DB2 ──► CDM ──► Server XML
        (148 mappers)   └──────────────► Form Studio HTML     ──► PDF   (Chromium)
        (ISectionBuilders)
```

**Both renderers consume the same Server XML.** So "map CDM to HTML" and "map CDM to the GhostDraft
model" are the *same* mapping — the section builders in `fact-docgen` — plus a small HTML-side
runtime that reads Server XML. Do not build a second CDM→HTML mapping; §40's two earlier design
attempts both did that and were wrong in the same direction.

**Consequence for task ordering:** W1 (section builders) unblocks both renderers. W2 (HTML runtime)
and W3 (GhostDraft emission) can then proceed in parallel.

### What is already proven, so nobody re-does it

| | evidence |
|---|---|
| Binding format fully decoded, 14,107/14,107 fill points resolve | §40, §42 |
| Machine-authored `.gd` with a list + conditionals + bindings **renders populated PDF** | §51 |
| GhostDraft's own compiler validates an authored template (`CreateSnapshot.exe`) | §50 |
| Authored template registers into a Studio project | §47–49 |
| A `.gd` never carries a model; the library comes from the project/workspace | §46 |
| `<explanation>`, the 10 system properties and instruction IDs are GhostDraft's, not ours | §50 |

### The bar, per Products (§51)

> Existing GhostDraft forms are **hand conversions with human tweaks**. They are **not an oracle.**
> Parity with them is not required. The goal is **mass convert, then tweak.**

So the metric is **binding coverage**, because it is exactly the volume of hand-tweaking left — not
pixel fidelity against existing templates.

---

## 1. Where the numbers stand today

| measure | value | source |
|---|---|---|
| FAP library | ~4,478 forms; **~31% are fragments**, not standalone documents | HANDOFF |
| Forms with data-bound fields | 2,489 | `bindgap.py` |
| Distinct attributes demanded | 678 over 62 entities | `bindgap.py` |
| DDT records declaring a source | 99.7% of 61,096 | `bindgap.py` |
| **`powtype` = manual WIP entry, no data source can ever exist** | **11.7%** | `bindgap.py` |
| Already converted by Products (the two packages we hold) | ~306 ISO CA + ~55 proprietary CA **form numbers** (570 templates) | measured, approximate |
| Server XML supply, ISO CA, template-weighted | **30.1%** | §41 |
| Server XML supply, proprietary CA, template-weighted | **63.3%** | §44.3 |
| ISO paths in root sections with **no section builder at all** | **623 (29.1%)** | §41.3 |
| Our existing C# `.gd` generator: fill points that resolve | **51 of 1,480 (3.4%)** | §44.4 |

---

## W1 — CDM → Server XML (the section builders)

**Repo:** `fact-docgen` · `src/MoE.Commercial.Documents.Generation.GhostDraft/Components/Serialization/`
**Unblocks:** everything. Do this first.

### W1.1 Build the five missing ISO root sections
`§41.3`. Five root elements are demanded by ISO templates and **no `[SectionBuilder]` is registered
for them** — confirmed two independent ways (absent from all 65 real `Builder.xml` artefacts, and
absent from the attribute registry):

| paths demanded | root section |
|---:|---|
| 284 | `CommonState-SpecificPolicyLevelCoverage` |
| 245 | `CALocationLevelCoverages` |
| 54 | `CommonPolicyLevelCoverages` |
| 38 | `SingleInterestAutoPhysicalDamageInsurance` |
| 2 | `RetrospectivePremiumPlan` |

Start with `CALocationLevelCoverages`: 38 templates each need all five of
`Location/Address/{AddressLine1,AddressLine2,City,State,ZIP}`, and location-level coverage is a whole
axis of the model with nothing behind it.

**Acceptance:** `python tools/xmlsupply.py output/gdbindings-ca2607.csv <BuilderRenderOutput> --kind Builder`
shows the root as emitted rather than `NEVER`, and its supplied count rises.

### W1.2 Fix the value-half-missing pairs
`§41.4`. The model pairs almost every value with a pre-formatted `*Wording` twin (3,191 pairs against
6 exceptions). Seven pairs emit only the `Wording` half, so the value half renders blank. Top one:

**`CAAutoLevelCoverages/Items/Auto/VehicleNumber` — needed by 106 templates** (43 fill point, 81 list
**sort key**, 41 condition; GhostDraft's own spec independently says 106). `CAAutoLevelCoveragesSection.cs:340`
emits only `VehicleNumberWording` — 201 occurrences against 0 across the artefacts. The 81 sort-key
uses matter most: a missing sort key means **wrong ordering**, which renders as plausible-but-wrong.

Full list of seven in §41.4. **Confirm first** which of those templates DocGen rules actually select —
the counts come from the package, not from rule evaluation.

### W1.3 Work the ranked gap list
`output/xmlsupply-builder.csv` (ISO) and `output/xmlsupply-propca.csv` (proprietary) rank every
demanded path by how many templates need it, with its supply state. Demand is exact; supply is a
floor. **Read `tools/xmlsupply.py`'s docstring before quoting a coverage number.**

### W1.4 Stand up binding coverage as a tracked metric
Per §51 this is the number that predicts hand-tweak volume. Wire `xmlsupply.py` into the dashboard so
it is reported per package on every build, not measured ad hoc.

**Blind spots to state whenever it is quoted:** it cannot fail on a *wrong value*, only a missing
element; outside the `NEVER` rows `missing` conflates a real gap with a path the policy sample does
not reach; `empty-only` (142 ISO paths) is ambiguous by construction.

---

## W2 — Populate the HTML form from Server XML

**Repo:** `fact-pdf-tools` · `core/`, `server/`, `client/`
**Depends on:** §0 decision; W1 for real data (a hand-written Server XML instance unblocks dev)

### W2.1 Put a Server XML path in every HTML field
`emit-html` currently emits geometry and text. Fields need `data-bind="<server-xml-path>"`.
**Never a CDM path** — FORM-STUDIO-PLAN section 3, and §40's rationale.

Where the path comes from is W3.1's output: the same FAP+DDT → binding resolution. Do not build a
second resolver.

### W2.2 A Server XML → HTML population runtime
Given a Server XML document and an emitted HTML form, fill the bound fields. Must handle what the
model actually uses (§40, measured):

* **lists** — `.../Items/<Element>` repeats a row group
* **selectors** — a boolean on the item; filters which items render
* **conditionals** — `is provided` and friends; a suppressed field must leave **no hole** (§51)
* **the value/rendering pair** — bind `X` or `XWording`; 36% of real demand is the `Wording` half
* **adornments** — the formatting counterpart of a legacy DDT picture clause (`with comma grouping`,
  `as MM/dd/yyyy`); 1,320 of 1,393 uses are comma grouping

### W2.3 Test data without waiting on W1
The proprietary package ships **32 Server XML instances** in `Test Cases/` (`1 Auto.xml`,
`3 Autos.xml`, `100 Autos.xml`, …) and they validate against real demand (§44.2: 221/221 paths
present with values). Use them as fixtures. `xmlsupply.py --kind "*"` reads that folder directly.

**Acceptance:** an emitted HTML form, fed `3 Autos.xml`, renders three auto rows with populated
values and no empty rows.

---

## W3 — FAP + DDT → GhostDraft `.gd` (the mass-conversion engine)

**Repo:** `fact-pdf-tools`
**This is the piece that does not exist** (§51). Everything either side of it does.

### W3.1 Derive a `gdauthor` spec from a parsed FAP + its DDT
`tools/gdauthor.py` takes a hand-written `Repeat`/`Cond`/`Fill`/`Static`/`Break` tree and is proven
to emit renderable logic (§44.5, §51). Nothing yet derives that tree from a form.

§43.5 is the translation table, from the `A2134FN` ↔ `CA 21 34` matched pair (§40.3):

| legacy | GhostDraft |
|---|---|
| `>XUnit1` / `move_it @GETRECSUSED` (unit iterator) | `Repeat` over the list, iterator bound to the item |
| DDT filter chain (`BYAGTX in (CA,FA), BYAOTX=UN, …`) | the **selector** on the list |
| `hardexst` presence test | `Cond(path.then(*IS_PROVIDED))` |
| `printif` | `Cond` with parts |
| `concat` of N DB2 columns, `DAL CALL("…")` | **ONE** attribute; the transform moves upstream into the section builder |
| `movenum` picture (`9.0,9.0,C`) | an **adornment** |
| value→text table (`noopfunc` with `CSL 100 =100,000:…`) | the `*Wording` twin element |
| sort order implied by the extract | `orderByList` |

**Two rules that will save you a wrong design:**
1. The relation is **many-to-one**: `A2134FN`'s 13 DDT fields become 9 bindings. Any design mapping
   FAP field → binding one-for-one is wrong before it starts.
2. The **DDT rule**, not the FAP field, is the unit that carries provenance. This is why the existing
   `FapToGhostDraftGenerator.LookupBinding` (matches on field NAME) resolves 3.4%.

Resolve every binding through `gdmodel.Model` against the target package **at author time**, so a
bad path fails in the converter rather than in GhostDraft (already how `gdauthor.py` works).

### W3.2 Emit layout from the FAP, not plain text
`gdauthor.py` currently emits plain text runs and uses **none** of the FAP geometry. §10–39 built a
high-fidelity FAP→layout pipeline; that work has to reach the RTF. This is what makes W4 possible.

### W3.3 Batch-convert and gate with GhostDraft's own compiler
`tools/gdvalidate.py` wraps `CreateSnapshot.exe`, which compiles a whole Studio project and validates
**every** template's markup against the concept library (§50). It is the right batch gate: convert N
forms into a project, run it, get a per-template error list. It already exits non-zero on any error.

Baseline on the sandbox project today: **82 templates compiled, 1 complaint** — and that one
(`EA9940 0194`, `'… Has 8 or more' is not in the model`) is **pre-existing**, not ours.

### W3.4 Register converted forms into a project
`tools/gdproject.py`. **Read §49 before touching a `.gdproj`:** it must be edited as TEXT, never
round-tripped through an XML parser (the manifest declares its default `xmlns` per element; a parser
rewrites every element and the added entry lands in no namespace, which Studio silently ignores). A
real entry needs `<artifactStatusGuid>` and `<lockable>false</lockable>`. The tool asserts a
byte-level pure-insertion invariant before writing, and backs the manifest up.

---

## W4 — Visual match to legacy (requirement 2)

**Depends on:** W3.2

### W4.1 Establish the exclusion set
Products' framing (§51): forms **already converted by Products are exempt** from the visual-match
requirement. So the scope must be known before it can be measured. Approximately **306 ISO CA + 55
proprietary CA form numbers** are already done across the two packages we hold (570 templates,
including Schedule/overflow companions) — but that count is heuristic, from filename parsing.

**Task:** produce an authoritative mapping of FAP form → GhostDraft template for each package, so
the conversion queue is `FAP library − already-converted − fragments`. Use the template catalogs and
the Integration Specification rather than filename regex.

### W4.2 A fidelity gate for the GhostDraft render
The problem: rendering a `.gd` needs Studio (GUI) or the server API (§50), so the geometry gates from
§10–39 cannot be pointed at it locally.

Two candidate routes — **this needs a decision, see D3:**
* compare the **GhostDraft render** to the legacy FAP2PDF render, reusing §38's vector-comparison
  instrument (`vectorrules.py`) rather than raster (§37's lesson: "when a metric keeps producing
  artefacts, suspect the KIND of measurement");
* or treat the **Chromium HTML render as the reference** — it is already gated against legacy at
  known numbers — and compare GhostDraft to it. Cheaper to automate, one indirection from truth.

**Validate any new gate against the forms Products accepted** (`EB2410A`, `A0238C`, `EB22489Q`,
`P0010G`, `BAN01`, `BANSPECH`) before trusting it. Seven metrics in this project have reported
defects the render did not have.

### W4.3 Remember what no gate can see
§51: six gates and GhostDraft's own compiler passed a template that rendered as a **single wall of
text**, because none of them can see layout. **Look at the render before shipping a fix and before
asking anyone else to look at it.**

---

## W5 — Things not on the original list that are needed

### W5.1 Make `FormDefinition` the shared document model — do this before W2/W3 diverge
`emit-html` and `FapToGhostDraftGenerator` both read `FapParseResult` directly and are **siblings,
not a chain**. Consequences already observed: an edited form cannot reach GhostDraft (the `.gd`
regenerates from the original FAP and discards the edit), and the underline fix had to be made twice
(§38 and §39). `FapToPdfGenerator` already accepts either.

`FormDefinition` needs extending to carry images, shading, underline and edge masks first. Both W2
and W3 want to read one model; if they are built against `FapParseResult` separately, every future
fix is made twice again.

### W5.2 Get the remaining package exports
`PackageNames.cs` names **ten** packages. We hold **two**. Concept model GUIDs are **per package**
(§43.6), so a binding cannot be emitted or verified for a package we do not have. Everything in
§40–51 is Commercial Auto only.

Needed per package: the `.gdsp`, its GDXSD if available, and ideally the Integration Specification.
Every tool then works unchanged — point them at the extracted directory.

### W5.3 A process for adding concepts to the model
When a FAP field has no corresponding concept, someone must add it to the `.gdm`. §43.2–43.3
established the mechanics: the concept library is the source of truth, the Packager regenerates
`model.xml` and the GDXSD, and the XML element id is derived from the name by
**"make it a valid NCName"** — strip to `[0-9A-Za-z_.-]`, prefix `_` on a leading digit,
integer-suffix on collision (**100.00%** over 12,486 members).

**Open:** who owns concept additions, and does the Packager accept hand-edited `.gdm` files or must
it go through Studio? Not tested.

### W5.4 Decide what happens to the 11.7% that can never bind
`powtype` fields are manual WIP entry — **no data source exists**, by design. They are a hard ceiling
on binding coverage, not a gap to close. They should become fillable/interactive fields in both
renderers. Naming this now stops someone trying to bind them and stops the coverage metric being
read as if 100% were reachable.

### W5.5 Packet assembly
~31% of the FAP library are **fragments**, so a converted form is not always a deliverable document,
and FAP2PDF is not a valid oracle for one. GhostDraft's model for this is already decoded (§40,
finding e): composition is by document **name** through `subscriptionType`, 346 of 491 ISO templates
subscribe to another, 885 edges over 172 targets, **all resolving**. Reuse it rather than inventing
an assembly model.

### W5.6 A `needs-review` mechanism
Determinism rule 4 is binding: when inference cannot decide, emit Layer A verbatim and flag
`needs-review`; never pattern-guess on document content. **There is still no `needs-review` mechanism
in `emit-html`.** For mass conversion this is essential — it is how a human knows which of N hundred
forms to look at, and it is the difference between "convert and tweak" and "convert and re-check
everything".

### W5.7 End-to-end population test on a real policy
Never done. Fetch a real policy (`GET /api/policy/{num}?env=tst`), build Server XML through the
section builders, render both renderers, compare. This is the only test that can fail on a **wrong
value** — every gate built so far checks that an element is *present*, not that it is *right*.

---

## Open decisions (need a human)

| # | decision | why it matters |
|---|---|---|
| **D1** | Confirm §0: HTML binds to **Server XML paths**, and there is exactly one CDM→Server XML mapping (the section builders). | Two earlier designs got this wrong. Everything in W2 depends on it. |
| **D2** | Where do converted `.gd` files live — generated straight into the SVN-backed Studio project, or a staging project that Products merges from? | `DmsRevisionInfo.xml` shows the project is SVN-backed (`svn.ghostdraft.com/svn/moe/…`). Mass generation into a shared project needs a story. |
| **D3** | W4.2: gate GhostDraft renders against **legacy FAP2PDF** or against **our Chromium HTML render**? | Determines whether a render pipeline must be automated (needs publish + credentials) or not. |
| **D4** | Who owns concept-model additions (W5.3), and is hand-editing `.gdm` acceptable? | Blocks any form whose fields are not already in the model. |
| **D5** | Priority order across the ten packages. | We have 2 of 10; each needs its own export before any of its forms can be bound. |

---

## Suggested order

```
W5.1  FormDefinition as the shared model        ← do before W2/W3 diverge
W1.1  CALocationLevelCoverages section          ← unblocks the largest ISO gap
W1.2  VehicleNumber + the six other pairs       ← 106 templates, cheap, ordering bug
W3.1  FAP+DDT → gdauthor spec  (one form first: pick a FAP whose DDT has a unit
      iterator AND a presence test, convert it, render it, then batch)
W2.1/2.2  HTML data-bind + population runtime   ← parallel with W3, shares W3.1's resolver
W3.2  FAP geometry → RTF                        ← gates W4
W4.1  exclusion set / conversion queue
W3.3  batch convert + gdvalidate as the gate
W4.2  fidelity gate  (after D3)
W5.7  end-to-end on a real policy               ← the only test that catches wrong values
```

---

## Tooling reference

```bash
# binding extraction and verification
python tools/gdbindings.py <pkg> --out output/gdbindings.csv     # all templates
python tools/gdbindings.py <pkg> --form "<name>"                 # one, printed
python tools/gdbindings.py <pkg> --templates <dir-of-our-gd>     # resolve OUR output
python tools/gdxsdcheck.py <bindings.csv> <pkg>/GDXSD.xsd [--selftest]
python tools/gdspeccheck.py <bindings.csv> <pkg> "<IntegrationSpec.zip>"

# demand vs supply
python tools/xmlsupply.py <bindings.csv> <BuilderRenderOutput> --kind Builder
python tools/xmlsupply.py <bindings.csv> "<pkg>/Test Cases" --kind "*"

# authoring
python tools/gdauthor.py <pkg> --demo out.gd                     # emit + verify grammar
python tools/gdauthor.py <pkg> --selftest out.gd                 # prove verify() fails
python tools/gdgrammar.py "<glob of .gd>"                        # placement invariants
python tools/gdidrule.py                                         # re-derive the naming rule

# project + validation
python tools/gdproject.py <project.gdproj> <file.gd> [--dry-run]
python tools/gdvalidate.py <project.gdproj> [--diff "<name>"]     # GhostDraft's compiler
python tools/gdpackage.py <extracted-pkg> out.gdsp <file.gd>      # server-bound package
```

**Environment gotchas that have each cost real time:** a `.gdsp` is a zip, extract under
`output/iso-packages/<name>/`; read the Integration Spec zip **in place** (template names exceed the
Windows 260-char path limit); the same limit breaks any attempt to clone a Studio project; and
**bash heredocs eat backslashes** — write Python containing `\r\n`, `\\` or `\"` with the Write tool,
never a heredoc.
