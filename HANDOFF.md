# Handoff — fact-pdf-tools

Two separate initiatives live in this repo. **Which one you are continuing depends on the branch.**

| branch | initiative | plan doc |
|---|---|---|
| `main` | FAP → **GhostDraft `.gd`** export (Quote concept model, 419 quote forms) | `PROGRESS.md` §6 |
| `features/form-studio-p0` ← **you are here** | **Form Studio**: FAP → HTML → Chromium PDF, with measured fidelity gates | `FORM-STUDIO-PLAN.md` |

The six auto-loaded memory files describe the **`main`** GhostDraft work. Still accurate for that
branch, largely irrelevant here.

---

## Form Studio (`features/form-studio-p0`)

**Goal:** convert legacy Documaker FAP/DDT forms into a modern, editable, schema-bound format,
deterministically, and prove the output is indistinguishable from the legacy render.

`FORM-STUDIO-PLAN.md` is the design *and* the running lab notebook. §10–37 record what was measured,
**including the negative results** — read §19, §25, §32, §34 and §37 before attempting anything on
the geometry axes, because they document five failed fixes, seven refuted hypotheses, and one whole
failure list that was misclassified. Sections are append-only; do not rewrite history.

### Where it stands — 1,000-form stratified sweep

| measure | result | gates? |
|---|---|---|
| **Tier 1 — nothing dropped** (content) | **912/913** | yes |
| **Line placement** (§36, §37) | **798/807**, median 100% | candidate |
| Tier 2 — glyphs within 3.0pt | **898/914** | yes (superseded?) |
| Tier 2 at the 1.0pt quality bar | 619/914 | no, diagnostic |
| **Lost token whitespace** (§37) | **0 forms** short by ≥1.4pt | no, diagnostic |
| **Vector rules** (§38, §39) — recall / precision | **585/587** both ways; recall <90% on 2, precision <90% on **0** | candidate |
| Non-text ink — recall / precision | **562/571** / median 100% | recall gates |
| Glyph shape | median 0.85 | no, diagnostic |
| `.gd` content gate | 10/10 goldens, no vacuous passes | yes |

Section 37 moved four of these: line placement 794 → 798 (metric bugs), Tier 2 878 → 898 and the
1.0pt bar 552 → 619, and Tier 1's word match 654 → 788 forms at 100% — all from **one** parser
fix, restoring leading whitespace inside text tokens.

Section 38 added the vector rule gate and, with it, underlines: recall below 90% went 54 → 22,
and the ten forms that drew **no rules at all** where legacy draws hundreds of points went to
zero. Note what did NOT move: the raster ink gate stayed at exactly 538/571, zero forms either
way, while 51 forms gained real ink. That is a measurement of its blind spot, not an argument
about it.

**Section 39 is the big one, and it came from Products, not from a metric.** The `A,X1`
annotation after every `X,` record carries an EDGE SUPPRESSION MASK (1 top, 2 bottom, 4 left,
8 right; bit set = Documaker hides that edge). Honouring it took rule precision from a 65.1%
median to 99.8%, **forms below 90% precision from 71 to 0**, recall failures 22 → 2, and ink
recall 538 → 562/571 — with zero regressions. It also **retires three heuristics** (§23's height
threshold, §32's group rule, §36's sibling-row rule), which were all fitting the average of a
field nobody had parsed.

All 1000 render without error, plus the 2 pinned accepted forms the strata do not pick. `.gd` golden
suite green. `emit-html` byte-identical across runs.

The human-validated set, all on fresh renders: `EB2410A` 100% / Tier 2 100.0%, `EB22489Q` 100% /
99.2%, `A0238C` 100% / 90.6%, `P0010G` 100% / 100.0%, `BAN01` 100% / 45.5%, `BANSPECH` 100% / 42.2%.
No regressions from §37.

### NEXT STEP — a review pack is built and waiting

`output/review-pack/` (17 comparisons + `README.md` + `manifest.csv`), built 2026-08-24 against a
fully re-measured build. It asks **one decision and three checks**, and the decision is the one
that sets the next significant piece of work:

**The over-drawn-tables decision is ANSWERED and FIXED** — Products said send it back, then
asked the question that solved it (§39). Table-structure inference is **off the roadmap**.

What the pack is still for: confirm the three fixes shipped 2026-08-24 that no human has seen
(underlines, leading spaces, edge masks); confirm `BAN01`/`BANSPECH` still look identical, which
is the evidence for retiring Tier 2; and sign off artwork, which no check can verify.

Rebuild with `python tools/dashboard.py && python tools/reviewpack.py`. The groups are driven by
the questions that are actually open, so **edit `question_groups()` when they change** -- a pack
that re-asks a settled question spends the only scarce resource here, which is a reviewer's
attention. Selection deliberately avoids forms carrying an unrelated defect, so a "does the text
sit right?" question is not answered by a form that is also visibly over-ruled.

### THE OPEN DECISION — read this first

**Does line placement replace Tier 2 as the placement gate?** It is the one thing blocking a clean
statement of what is left, and it is Products' call, not an engineering one.

Products reviewed a sample (2026-08-23) and gave verdicts that are now the project's ground truth:

| form | verdict | Tier 2 | line placement |
|---|---|---|---|
| `BAN01` | "looks identical" | 45.5% | **100%** |
| `BANSPECH` | "looks identical" | 42.2% | **100%** |
| `IM74561R` | "missing lines in various places" | 48.2% | **51%** (correctly fails) |
| `M7902AA` | boxes where legacy underlines | passes | fixed, now 100% |

Tier 2 disagrees with the human on two of four; line placement agrees on all four, and on the four
forms accepted in August. **No Tier 2 tolerance fixes this** — BAN01/BANSPECH do not reach 90% even at
8pt, more than a line height. The quantity is wrong, not the threshold. Re-measured on fresh renders
after the §37 fix, `BAN01` and `BANSPECH` are **unmoved** at 45.5% and 42.2%, so the disagreement is
exactly what it was.

If line placement is adopted, the real remaining list is **nine**, in two classes, both real and
both deferred with a decoded mechanism:

| class | n | forms |
|---|---:|---|
| **Missing tab-leader fill** (`M,P1`, leader char in field 3) | 4 | `IM74561R` `IM79014O` `M7208A` `IM74054O` |
| **Page-number overlay** (`PageNumbers = Yes` in `FSISYS.INI`) | 5 | `EB9909SCHEDA` `EP990{7,8,9}SCHED*` `EP9910SCHEDC` |

Both need a model this design does not have — text flow for the leaders, and a Documaker
printer-driver artefact for the page numbers (see the note below; supplying the value would not
even pass the gate). Together they are 40 of 4,210 forms.

**The "missing space at a run boundary" class in the previous handoff did not exist** — see §37.
It was the legacy side of the comparison being read as ours. The defect it was groping towards
was real but elsewhere, and much larger: the parser was deleting leading whitespace from every
text token, on 1,340 of 4,478 forms. That is fixed.

### Other open work

- **The page-number overlay is half decoded** (§37). `PageNumbers = Yes` in `FSISYS.INI` makes the
  Documaker PDF driver overlay two digit runs on a `Page      of` static — 16 of 4,210 forms. The
  current-page digit is a constant 28.6pt from the text start on all seven renders measured; the
  total-pages digit is 53.0pt on the six forms whose box starts at col 17235 and 55.4pt on
  `EF0450SCHEDA` at col 16310, and nothing in the FAP or the INI explains the 2.4pt. Note that
  **supplying the value would not pass the gate** — legacy extracts as `Pageof11`, "Page 1 of 1"
  keys as `Page1of1`. Deferred with a reason, not mislabelled.
- **`MC1690a` / `MC1690C`** — one missing 444pt horizontal, the only two vector-rule failures
  left in the library.
- **The `A,X1` colour is decoded enough** (§39). Every non-black value sits on a SHADED record and
  the declared colour is NOT what Documaker paints — §22's measured per-style table matches legacy
  exactly on all 25 comparable records and the declared colour would regress 21 of them. Do not
  "fix" this; it is not broken. What the colour means is still unknown.
- **Intra-run horizontal drift** (§19, §25). Five mechanisms tried, all worse or a wash. Anything that
  adjusts a single number per run is exhausted; only per-glyph positioning is untried. Products says
  it is invisible, so this may not be worth fixing at all. But note §37: a real defect had been
  FILED here without being measured into it. Tier 1's `SPACING+MOVED` class was called drift, and
  splitting it showed two populations — merges (median −3.00pt, the lost leading spaces) and
  equal-token-count differences (median −0.20pt, genuine drift). The fix removed the first and left
  the second at −0.22pt, so what remains here really is drift. Do not add to this bucket without
  splitting it first.
- **The `.gd` path is missing all 911 underlines** (§38). `FapToGhostDraftGenerator` writes
  `\ulnone` on every run unconditionally, and the `.gd` content gate compares characters so it
  cannot see it. The flag is decoded and `FapStaticText.Underline` / `FapTextToken.Underline`
  already carry it, so the fix is `\ulnone` → `\ul` on those runs plus a golden recapture.
  Left alone deliberately: it is the `main` branch's deliverable, not Form Studio's.
- **Trailing token whitespace is not restored** (§37). 313,323 of 2,038,456 `M,TT` records declare one
  character more than they store — the inter-word space of a flowed text area lives in the declared
  LENGTH, not in the file. It carries no ink and every token is absolutely positioned, so it changes
  extracted text only. Restore it if a gate ever needs word separation to match legacy exactly.
- **A declarative system-value registry** (total pages, current page, edition, print date). Still the
  last known content gap. `FSISYS.INI` has an `< AutoFields >` section (`SYSDATE = ~DATE`) that is the
  right shape for it.
- **Packet assembly** — ~31% of the library are fragments, so a converted form is not always a
  deliverable document, and FAP2PDF is not a valid oracle for one.
- **Image sign-off.** Images render (§28) but **no gate can check them** — FAP2PDF embeds none.
- **P3 onward** — binding layer, editor, runtime (§8) are all still ahead.

### Done, so nobody redoes it

The baseline anchor (§18), shading (§22), `M,PX` rules and box sizing (§21, §23), `M,I` bullets (§24),
the DocuDings font substitution and the 0-byte oracle (§27), `.LOG` image decoding (§28 — all 76
assets, every library reference resolves), the `.gd` content gate (§20), sub-point rectangles (§35),
**the `A,X1` edge suppression mask (§39 — it retires §23's, §32's and §36's edge heuristics)**,
**leading token whitespace (§37 — 11,599 records across 1,340 forms)**,
and the line-placement grouping fallback (§37 — it was reading its own grouping and had thrown away
the start-x check), and **text underlines (§38 — bit 0 of the `A,T1` flag, 911 runs across 186
forms)**. The lone `X,` record is CLOSED as undecidable from the record (§38), not open.

---

## P3 / BINDING — the format is DECODED (2026-08-26). Read this before designing anything.

Products supplied the **ISO Commercial Auto Project (2607.0)** package. It answered the matched-pair
question outright. **Sections 40, 41 and 42 of `FORM-STUDIO-PLAN.md` are the record; the summary
below is not a substitute for §42, which contains the one mistake worth not repeating.**

### The three layers, now with the middle one read rather than assumed

```
DB2  --(fact-commercial-api: 148 mappers in Data.Provider/Mapping/Db2)-->  CDM
CDM  --(fact-docgen: ISectionBuilders, 13 registered root sections)------>  Server XML
Server XML  --(model.xml projects concept GUIDs onto element names)------>  GhostDraft template
```

**A template does not store a data path as a string.** It stores a typed instruction tree whose
paths are `(rootguid, pathNodes[guid])` tuples against a concept library, and `model.xml` — shipped
in the package — is the projection onto Server XML element names. That is the whole binding
mechanism, and a Form Studio field's `data-bind` should carry the resolved Server XML path.

Instruction vocabulary, complete across all 491 templates: `fillPointType` (14,107),
`conditionalInstructionType` (10,362), `subscriptionType` (885), `listInstructionType` (691),
`annotationType` (10). RTF carries `%[ID]` markers keyed to the instruction IDs.

### Where it stands — verified against three authorities, each of which caught something

| | |
|---|---|
| instructions resolved | **26,141 / 26,145** (the 4 are list-position built-ins, reported) |
| fill points resolved | **14,107 / 14,107** |
| paths present in the package XSD | **19,112 / 19,112 = 100.00%** |
| XSD-check sensitivity selftest | 200 baselines accepted, **1,000 mutations all rejected** |
| GhostDraft Integration Spec recall | **100.00%** over the 491 packaged templates |
| ...precision / exact-set match | 99.90% / **482 of 491** templates |

**Each authority caught what the others could not.** The XSD found `elementId`-vs-`elementName`
(117 of 296 lists differ; 698 wrong paths, and the matched pair passed anyway) and the
mutex-group-as-enum rule. The Integration Specification found two unread FORMAT FIELDS — read §42.

### The five findings that constrain any design

1. **A value and its rendering are separate elements.** `X` and `XWording`: **3,191** paired
   attributes against **6** unpaired, and **36%** of all fill-point demand is on the `Wording`
   half. There is a third mechanism too — `adornmentPath` (`with comma grouping`, `as MM/dd/yyyy`)
   — which is the modern counterpart of a legacy DDT picture clause and is **formatting, not a
   binding**.
2. **Selection is a selector, not a field.** A DDT's DB2 filter chain becomes one named boolean per
   list item. 422 selectors, each an `xs:boolean` on the item class.
3. **Concatenation and DAL functions move upstream** into the section builder. Three DB2 columns
   become one attribute; `CALL("Insured_Address_LongName")` becomes `Insured/PrimaryNamedInsured`.
4. **The map is many-to-one, and the DDT RULE — not the FAP field — is the unit of provenance.**
   `A2134FN`'s 13 fields become 9 bindings. Any design mapping FAP field → binding one-for-one is
   wrong before it starts, which is exactly why `FapToGhostDraftGenerator.LookupBinding`'s
   field-NAME matching can never reach 100%.
5. **A form is not one template.** 346 of 491 subscribe to another; 885 edges, 172 distinct
   targets, all resolving (4 only case-insensitively). Composition is by document NAME. This is the
   packet-assembly model the plan lists as open, already specified.

### Supply: what fact-docgen actually emits

`tools/xmlsupply.py` joins demand to the **65 `Builder.xml` artefacts** the fact-docgen integration
tests write for real policies — the authority, not a grep over the 35 section builders. Of 2,140
demanded data paths: 238 supplied, 142 emitted-always-empty, 1,760 absent from the sample. Weighted
by template usage, **30.1% supplied**, which is the number to quote.

**Supply is a FLOOR** — 65 policies cannot exercise 2,140 paths. The one sample-independent part:

| paths | root section with NO builder at all |
|---:|---|
| 284 | `CommonState-SpecificPolicyLevelCoverage` |
| 245 | `CALocationLevelCoverages` |
| 54 | `CommonPolicyLevelCoverages` |
| 38 | `SingleInterestAutoPhysicalDamageInsurance` |
| 2 | `RetrospectivePremiumPlan` |
| **623** | **29.1% of demand.** The `[SectionBuilder]` registry agrees independently. |

And one lead with a line number: **`Auto/VehicleNumber` is the 5th most-demanded element in the
library** (106 templates: 43 fill point, 81 list SORT KEY, 41 condition — the spec independently
says 106) and `CAAutoLevelCoveragesSection.cs:340` emits only `VehicleNumberWording`, 201
occurrences against 0. On 81 templates that is what the auto list is ORDERED BY, so it renders as
plausible-but-wrong rather than blank. Six smaller value-half-missing pairs share the shape; the
list is in §41.4.

### NEXT STEP — the end-to-end test, now properly specified

Unchanged in principle and much better specified in practice: **take one converted form, fetch a
real policy (`GET /api/policy/{num}?env=tst`), build the Server XML, render it populated.** The
target XML shape is no longer a guess — `output/gdbindings-ca2607.csv` gives every path a template
needs and `output/xmlsupply-builder.csv` says which are already supplied.

Be precise about what is and is not true: the `.gd` path **converts** (427/427 quote forms,
brace-balanced) and has **never been demonstrated to populate**. Nothing in this session changed
that. What changed is that the gap is now enumerated rather than estimated.

Two things the join structurally cannot see, so do not quote it as coverage:

* **It cannot fail on a wrong VALUE** — only on a missing element. A builder emitting the wrong
  policy number scores as supplied.
* Outside the five NEVER rows, `missing` conflates a real gap with a path this policy mix does not
  reach, and `empty-only` (142 paths) is ambiguous by construction.

The end-to-end render is the fix for all three.

### Tooling

```bash
python tools/gdbindings.py <pkg> --form "CA 21 34 10 13 Schedule"   # one template, resolved
python tools/gdbindings.py <pkg> --out output/gdbindings-ca2607.csv # all 491
python tools/gdxsdcheck.py <bindings.csv> <pkg>/GDXSD.xsd [--selftest]
python tools/gdspeccheck.py <bindings.csv> <pkg> "<IntegrationSpec.zip>"
python tools/xmlsupply.py <bindings.csv> <BuilderRenderOutput-dir> --kind Builder
```

A `.gdsp` is a zip; extract under `output/iso-packages/<name>/` (gitignored) and copy the package's
`GDXSD.xsd` in beside `model.xml`. **Read the Integration Spec zip in place** — several template
names exceed the Windows 260-character path limit and extraction fails partway through. Note the
`.gdsp` ships **491** templates while the spec documents **1,110**, the 491 a strict subset;
comparing against all 1,110 measures the package export, not the extraction.

`tools/bindgap.py` still ranks the LEGACY demand off the DDT and its docstring is still right that
the coverage percentage is a floor. Its **ordering** now has an outside witness: it put
`PMSP0200.SYMBOL`/`POLICY0NUM` first, and the modern ranking puts `Policy/PolicyNumber` first at
305 of 491 templates.

### Still open on this axis

- **Seven more packages.** `PackageNames.cs` names eight; this is one. Everything above is
  Commercial Auto only.
- **`FormDefinition` is not the shared document model it should be.** `emit-html` and
  `FapToGhostDraftGenerator` both read `FapParseResult` and are siblings, not a chain, so an EDITED
  form cannot reach GhostDraft. This bit twice already (the underline fix in §38 and §39). Fix
  before the P3 editor work; `FormDefinition` needs to carry images, shading, underline and edge
  masks first.
- **`MC1690a`/`MC1690C`** — the only two vector-rule failures left, one missing 444pt horizontal
  each; `M,O` was investigated as the cause and REFUTED. They are tab-leader forms, the known class.
- The **Fillpoint Usage List** xlsx in the package folder is unread. It may be a fourth authority
  or may just restate the spec.

## Hard-won rules — do not relearn these

- **Two authorities agreeing is not enough, and a check that can only VALIDATE cannot find an
  OMISSION.** (§42.) The binding extractor was verified against `model.xml` and the package XSD and
  scored 100% on both — while silently never reading two path-bearing fields. The XSD is
  structurally blind to a path you never produced: it can only confirm that what you produced
  exists. GhostDraft's own Integration Specification states the expected SET per template, so it
  could fail, and did, at 89.6%. When you add an instrument, ask which DIRECTION of error it can
  catch, not just whether it agrees.
- **A resolver that reports "unresolved" beats one that guesses.** `adornmentPath` produced 1,393
  rows with roots named `with comma grouping` — legible as formatting in ten seconds. A
  name-matching resolver would have turned the same input into 1,393 plausible wrong bindings.
- **"Audit the parser against the FORMAT" means the FIELDS, and it is easy to apply one level too
  shallow.** §39 wrote that rule; §40 then enumerated the five instruction TYPES and called that
  the audit; §42 found `orderByList` and `adornmentPath`, which are fields of types already parsed.
  Enumerate by PARENT, exhaustively, and count what you consume against what exists.
- **Validate a path resolver on the LIBRARY, never on one form.** Reading `elementName` where the
  XSD uses `elementId` was wrong on 117 of 296 lists and 698 paths — and passed the matched pair
  perfectly, because on `Auto` the two strings coincide.
- **Check the denominator belongs to you.** The first spec comparison scored 47% recall; 619 of the
  1,110 templates it graded simply are not in the package. That measured the package export, not
  the extraction.
- **Validate any new gate against the forms Products accepted** (`EB2410A`, `A0238C`, `EB22489Q`,
  `P0010G`, and now `BAN01`, `BANSPECH`) *before* trusting it. **Seven** metrics here have reported
  defects the render did not have. A gate that invents work is worse than no gate.
- **A gate that cannot FAIL is worse still.** Three have shipped that way: the Tier 1 fill allowance
  was vacuous on forms declaring no fields (`0 >= 0`), hiding 808 missing characters; a
  leader-collapse regex deleted leader runs instead of collapsing them; and line placement's
  grouping fallback verified content and baseline but **not start x**, so shifting a whole page 3pt
  right scored 100%. Test sensitivity deliberately — delete characters, move the page, and confirm
  the gate fires. There is a harness for this in the scratch notes of §37.
- **Ask what a check is BLIND to.** Six blind spots so far: Tier 2 could not see drift inside a run;
  Tier 1 and Tier 2 are text-only and could not see a missing logo or rule; **Tier 1 strips
  whitespace and line placement collapses it, so NEITHER can fail on lost whitespace** — that is what
  hid the leading-space defect on 1,340 forms, and `tools/tokenspace.py` now exists because of it;
  nothing measured glyph identity (a wrong font passed everything); and the `.gd` golden suite passed
  real parser changes because no form in it used the construct.
- **Look at what your dismissed classes are made of.** The leading-space defect was reported by
  Tier 1 on 181 of 913 forms and written off as intra-run drift. Splitting the population on one
  binary — did our token COUNT drop — separated it cleanly: median −3.00pt (one space) against
  −0.20pt. Two phenomena had been pooled and dismissed together.
- **Check which side of the comparison a string came from.** A whole failure class was invented by
  reading a metric's `misses` output — which prints the LEGACY line — as if it were ours.
- **When a metric keeps producing artefacts, suspect the KIND of measurement, not the tuning.**
  Four artefacts across §36–38 trace to one root: every gate compared RASTERS, and a raster forces
  a threshold and a mask — then whatever the mask ate got attributed to the render. `G2032C` scores
  4.6% ink precision because the text mask removes 98.9% of legacy's rule ink and 72.9% of ours.
  A different kind of instrument (`vectorrules.py`, comparing PDF vectors) found 911 missing
  underlines in an afternoon that five sections of threshold work had not.
- **Look at the render before shipping a fix, and before asking anyone else to look at it.**
  The vector gate rated a visibly broken underline (ten segments with 7.4pt holes where legacy
  draws two continuous bars) at **87.2%**, and a worse one at 92.0% -- numbers that read as
  "nearly right" in a table of 614 forms. Both would have shipped. Opening the comparison caught
  it in seconds (§38.7). Every gate here RANKS; none of them knows what a reader notices.
- **Audit the parser against the FORMAT, not against the output.** Every instrument here compares
  our render to Documaker's; none can tell you the INPUT has a field you never read. Nine sections
  of geometry work, and a written conclusion of "undecidable", rested on a dropped per-record field
  that appears in all 7,055 `A,X1` annotations in the library (§39). §24 audited record TYPES;
  nobody had audited the FIELDS of the types we already parse.
- **When a heuristic stalls in the 60-80% band across several attempts, stop tuning and go looking
  for an unread input.** That band is what fitting the average of a hidden discriminator looks
  like. Five attempts over four sections sat at 58-80% before the mask turned up (§39).
- **A one-sided error distribution is evidence about the model, not noise.** The mask hypothesis
  was reported REFUTED at 13-48% agreement because it was tested with the polarity inverted; read
  the right way round the same numbers are 87/73/82/84%, and every mismatch pointed the same
  direction, which was the shared-edge signature. An inverted-sign refutation looks exactly like a
  real one — and this one closed a line of enquiry a domain expert had opened correctly.
- **Read the authority, do not infer from a proxy.** Five artefacts in one day traced to this:
  `CPL1`/`CPP` false-missings (fixed by DB2 schema), selector-vs-value columns (fixed by DB2
  schema), CDM properties scraped from builder expressions instead of the CDM types, DB2->CDM
  read from SQL `AS` aliases instead of the 148 mapper classes, and the `A,X1` mask itself.
  Every one moved the answer substantially. If a number comes from a regex over source text,
  assume it is a floor.
- **Measure both directions.** A recall-only ink gate cannot see ink you INVENT — and it actively
  *rewards* over-drawing, which is why boxes-instead-of-underlines survived until a human looked.
- **How accurate a heuristic must be depends on WHICH WAY it fails.** An 80% rule ships where being
  wrong draws a spurious rule; a 92% rule was reverted where being wrong drops real edges.
- **A better model of one quantity is not a better model of another.** A change that cut run-width
  error 5.4× still regressed glyph placement on every accepted form.
- **Never let a metric impose its own tokenisation.** Tier 1 had to go to character level, Tier 2 to
  glyph level, and line placement needed a grouping-tolerant match after **six** rounds of threshold
  tuning traded one artefact for another. And when a match must tolerate grouping, tolerate it
  against the RAW SPANS — grouping first, then comparing, reintroduces the artefact you were
  compensating for (§37).
- **Correlation generates a hypothesis; it never closes one.** Prefer finding the mechanism over
  fitting a correction — eight calibration variants failed before the real cause (box-bottom anchoring)
  turned out to be a one-line geometry fix. Likewise the leading-space defect: the mechanism was
  visible in a single FAP record once someone read one.
- **Never measure a fix without confirming the artefact is newer than the binary**, at the point of
  MEASUREMENT rather than of rendering. `output/sweep-work/` caches per form, and the set you compare
  is usually not the set you re-rendered. This has bitten twice.
- **A freshness check is not a coverage check.** The stratified sample is drawn from construct
  counts, so it had no reason to contain `EB2410A` or `EB22489Q` — and did not. Two of the six
  human-validated forms were being read off renders from the *previous* build while the freshness
  check truthfully reported 1000/1000, because it only ever asked about the forms the sweep covered.
  `sweep.py` now pins an `ACCEPTED` list into every sample (§37.7). Add to it whenever a human gives
  a verdict on a form.
- Determinism rule 4 is binding: when inference cannot decide, emit Layer A verbatim and flag
  `needs-review`. Never pattern-guess on document content (hence "Page N of" is *not* special-cased).
  There is still no `needs-review` mechanism in `emit-html`; it is a gap.

## Environment gotchas

- **`FAP2PDF.EXE` is a deficient oracle in three known ways**: it embeds **no images**, renders
  composable fragments (`PSUM-*`, `PSCP-*`, `QCPP*`, `QFRM*`, `BQ-*`) nearly blank, and substitutes
  **base-14 fonts without embedding them** — so the legacy column's typeface comes from the
  rasterizer, not Documaker. Do not treat a difference in any of those three as our defect.
- It needs `FSISYS.INI` **and the Documaker TTFs** in the working directory (`sweep.py` copies both).
  Without the fonts it prints "created successfully", writes a **0-byte** PDF and exits 0.
- Under Git Bash use `-I=` not `/I=` (MSYS mangles the path).
- `sweep.py` needs an **absolute** output path with **forward slashes** — a `C:\\...` argument gets
  mangled by bash into a bogus directory.
- A full 1,000-form sweep takes about **2 hours**, not 40 minutes. Pipe it to a file, not through
  `tail` — a pipe buffers and you see nothing until it finishes. Watch progress by counting
  `output/sweep-work/*_ours.pdf` newer than the demo DLL.
- **Bash heredocs eat backslashes.** Writing Python or C# with `\n`, `\"` or `\\` through a heredoc
  silently corrupts it; this cost several rounds. Use the Write tool, or build strings from `chr(92)`.
- Stale `dotnet run` processes lock DLLs. Kill the listener on :5035 before rebuilding the server.
- Do not rebuild while a sweep is running, and do not rebuild between a sweep and reading its scores —
  the freshness check compares artefact mtime against the binary's.

## Tooling

Sweep artefacts are cached in `output/sweep-work/` (FAP, legacy `.PDF`, `.html`, `_ours.pdf` per form),
so the gates re-measure **without re-rendering**. Re-run `sweep.py` only after changing `emit-html`.

```bash
python tools/gdbindings.py <iso-pkg> --out output/gdbindings-ca2607.csv  # binding extraction
python tools/gdxsdcheck.py <bindings.csv> <iso-pkg>/GDXSD.xsd [--selftest]
python tools/gdspeccheck.py <bindings.csv> <iso-pkg> "<IntegrationSpec.zip>"
python tools/xmlsupply.py <bindings.csv> <BuilderRenderOutput-dir> --kind Builder
dotnet run --project demo/FapPdfTools.Demo.csproj -- emit-html EB2410A out.html
python tools/sweep.py 200 "C:/src/fact-pdf-tools/output"   # full pipeline; absolute, forward slashes
python tools/dashboard.py                                  # ALL measures, per form and stratum
python tools/contentdiff.py                                # Tier 1 gate
python tools/lineplace.py                                  # line placement (candidate gate)
python tools/tier2.py                                      # Tier 2 (3.0pt gate + 1.0pt diagnostic)
python tools/tokenspace.py                                 # lost whitespace INSIDE a token
python tools/vectorrules.py                                # rules by VECTOR, both directions
python tools/dashboard.py && python tools/reviewpack.py    # build the Products review pack
python tools/nontextink.py                                 # rules/shading/artwork, recall + precision
python tools/glyphshape.py --selftest EB2410A              # glyph identity; prove it reacts to shape
python tools/gdcontent.py                                  # .gd content gate
python tools/defectzoom.py FORM --reason line|ink          # zoom to the disagreeing region
python tools/reviewpack.py                                 # build a Products review pack
python tools/logdecode.py --scan                           # decode every Documaker .LOG
```

`lineplace.py` and `tokenspace.py` take `@file-of-form-names` and print how many forms they were
ASKED for as well as how many scored — passing a few hundred names on the Git Bash command line
silently delivers only the last one, and a subset run reported `1/1 forms pass`.

After changing `emit-html`, re-render **every form you intend to measure** and confirm each
`_ours.pdf` is newer than the demo binary before reading any score. But mtime is only a PROXY —
the real question is whether the artefact matches what the current build emits. Re-emitting all
1,292 HTML files takes seconds and Chromium is what costs the hour, so **re-emit everything, diff
against the cached `.html`, and re-render only what differs**. That turned a 100-minute sweep into
a 271-form re-render and proved the blast radius (exactly the 51 in-sample forms predicted) instead
of assuming it. A form whose HTML is byte-identical still has a correct PDF, whatever its mtime
says.

`dotnet run -- regress` is the golden-file suite for the **`.gd`** generator — run it after any change
to the shared FAP parser in `core/`, since Form Studio and GhostDraft share it. **Five** parser bugs
have now been found via Form Studio that had silently corrupted the `.gd` path too; the leading-space
one changed three of the ten goldens, by exactly the number of spaces each form declares.
