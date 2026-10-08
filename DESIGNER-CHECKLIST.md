# Document Designer – checklist

Tick items as they land (`[x]`). Each item: code + tests, then `dotnet test --filter "FullyQualifiedName~FaCT.DocDesigner.POC.Tests.Import"`.

Each finished item also needs a User Guide section: add/update it in `FaCT.DocDesigner.POC/Help/user-guide.json` and list the item number in the section's `covers` (a test fails otherwise). The guide PDF is the published `user-guide` template rendered with that file: designer app bar ⓘ or `/api/help/user-guide.pdf`.

## Import follow-ups
- [x] 1. Word headers/footers imported as running header/footer (page numbers via PAGE / NUMPAGES), Word page margins kept
  - Default header/footer of the last section; first-page / even-page variants and multi-section docs are reported in the import notes.
- [x] 2. Placeholder text in Word (`{{ name }}`, `«Name»`, `[Insured Name]`) offered as Data Fields
  - Import dialog ticks `{{ }}` and `«»` by default; `[ ]` is opt-in (the import notes count bracketed phrases that look like placeholders).
  - Found across Word's run splits; the field keeps the placeholder's formatting; `{{ x | currency }}` and MERGEFIELD `\#`/`\@`/`\* Upper` switches become the field's format.
- [x] 3. PDF form fields auto-mapped to model paths by name (suggest + one-click accept)
  - Model panel "Suggest mappings…" → dialog of candidates per field; confident, unambiguous matches are pre-ticked; Apply maps them (with a default format by type).
  - Matcher (`Mapping/FieldMatcher.cs`, `POST /api/mapping/suggest`): camelCase/separator splitting, form abbreviations (NO, #, EFF, INSD…), near-miss spelling, date/amount type hint.
- [x] 4. PDFs over 100 pages: import a chosen page range instead of refusing
  - `?pages=5-40` on `/api/import`; a too-long PDF returns its page count and the designer asks for From/To pages (default 1-100), then imports those pages.
- [x] 5. Hide the `no-liquid` separator spans from the Layer Manager
  - `no-liquid` component type: not layerable/selectable/hoverable/draggable; still exported in the template.
- [x] 6. Designer UI tests: detect a stale server on :5199 (old build) instead of silently reusing it
  - `GET /api/version` (assembly module id); the fixture reuses :5199 only for this build, otherwise starts its own on a free port (`--urls`); an explicit DESIGNER_URL of another build fails fast.
- [x] 7. Import proof: visual (page image) diff, low scores routed to the review page
  - Pages rasterized with PDFium (Docnet, test project only); "visual" = ink overlap within ~4pt; overlays (original red, render blue, both black) per page.
  - Score = min(word recall, visual); below 90% => review case under output/import-proof/review, shown at `review.html?source=import` (decisions in import-review.json).
- [x] 8. PDF fonts: use the embedded fonts where they can be extracted (fewer spacing gaps)
  - TrueType (FontFile2) / OpenType (FontFile3) programs become web fonts `'F_{name}_{hash}', <standard fallback>` when they have outlines and a Unicode map covering the text; subset fonts with symbolic maps (common in ISO forms) keep the standard family. Notes report used/replaced fonts.
- [ ] 9. Scanned PDFs: OCR text layer (e.g. Azure Document Intelligence) — deferred; revisit options (Azure Document Intelligence / Tesseract / Windows OCR)
- [x] 10. Prove merge fields / fillable PDFs on real files (need sample mail-merge letters and AcroForms)
  - Fixtures in `FaCT.DocDesigner.POC.Tests/Samples/RealWorld` (SOURCES.txt): 10 Word 2010/Mac 2011 mail-merge files from docx-mailmerge (MIT) and 6 IRS fillable forms (public domain).
  - Word: every merge field imported as expected: fields split across runs, spaces in names (`Hello_world`), the field code winning over stale shown text, fields in tables, headers and footers, and page breaks.
  - PDF: the IRS (LiveCycle) forms first imported **0 fields**, because every field is nested under `topmostSubform[0]`. Fixed in `PdfImporter.FormFields`: it walks the field tree, and a repeated name gets its parent path (`Page2[0].f1_01[0]`). An XFA note was also added. All fields now import (W-9 23, W-4 48, W-7 69, SS-4 89, 1040 199, 8822 25), at their exact widget rectangles and with max lengths.
  - Findings, not acted on:
    - ~~Check boxes import as plain field boxes.~~ Fixed (2026-10-02): PDF check boxes import as check-box fields (`field-check`, "Check box" in the designer). They have a Checked when setting and print `{{ path | checkmark }}` / `{{ path | checkmark: "Corporation" }}` (`Rendering/CheckMarks.cs`): an X centred in the box. Tests: `Forms/CheckMarkTests.cs`; a W-9 box gets its X measured by pixels.
    - ~~IRS coded names (`f1_01[0]`) defeat Suggest mappings; nearby label text could help later.~~ Done (2026-10-02): `Import/FieldLabels.cs` finds each field's printed label. A check box takes the text to its right. A text field takes the closer of the text before it on its line and the line above it, read from the start of that paragraph. Line numbers and explanations are dropped ("1 Name of entity/individual. An entry is…" → "Name of entity/individual"). The label is carried as `data-label`, becomes the designer prop `formLabel` (not exported) and is sent to `/api/mapping/suggest` (`labels`). `FieldMatcher.LabelScore` measures how much of the path the label covers, × 0.95. The suggestions list shows the label, and so does the unmapped-field hover. Tests: `Import/FieldLabelTests.cs`, mapping endpoint, and a W-9 designer test (f1_01[0] → entity.name ticked).
    - XFA-only forms (no AcroForm fields) still import no fields.
  - Tests: `Import/RealWorldImportTests.cs` (32), including designer import + render of a Word letter and a mapped W-9 field landing inside its box.

## Productivity
- [x] 11. Shared clauses / partials (`{% render %}`), versioned
  - Clauses have the template lifecycle (Draft -> Published -> Retired) in App_Data/clauses (`/api/clauses/...`, plus `/content` for previews and `/usage` for which templates include a clause).
  - Templates use `{% include 'name' %}` (latest published) or `'name@3'` (pinned); drafts never render; nested clauses and clause CSS are included; missing clauses / loops give clear errors; a clause can't be saved including itself.
  - Designer: Kind = Template / Clause switch; Clause block (Data) with a published-clause picker and version pin; read-only, sanitized canvas preview that refreshes when a clause is published.
- [x] 12. Multiple test-data scenarios per template, preview all
  - Named test payloads per template/clause name (App_Data/scenarios, `/api/{kind}/{name}/scenarios`), kept across versions and never published; max 50 per template, 2 MB each.
  - Toolbar "Test data" picker drives canvas sample values and Preview PDF; the status lists fields the scenario has no value for and empty lists.
  - Scenarios dialog: new (starts from the data on show) / edit / rename / delete; Preview all renders the model example + every scenario (`/api/render/scenarios`, up to 20) with page counts, gaps and per-scenario errors, one PDF viewer.
- [x] 13. Diff before publish (HTML + rendered PDF vs the published version)
  - `POST /api/{kind}/{name}/diff` (`?against=N` for another version): line diffs of the HTML (one tag per line), CSS (one rule per line) and printed PDF text, both PDFs and page counts, rendered with the same data (the chosen test scenario). DiffPlex for the line diff; unchanged stretches collapse around changes.
  - Publishing over a published version opens "Review changes before publishing" (printed text / PDFs side by side / HTML / CSS tabs) with Publish / Cancel; first publish keeps the plain confirmation. "Compare with Published" in the More menu shows the same without publishing. A side that can't render (e.g. missing clause) is explained.
- [x] 14. Field usage search ("which templates use `policy.number`?")
  - Liquid scanner (FieldUsage): outputs, filter arguments, if/elsif/unless/case/when, for/tablerow lists with aliases resolved (`location.name` -> `locations[].name`), assign/capture locals, comment/raw and string literals ignored.
  - `POST /api/usage/fields` {path, html?, kind?, allVersions?}: matching template/clause versions (newest + published by default) with paths and counts, templates that use it through a clause, and the unsaved canvas; a parent path finds everything under it. `GET /api/usage/fields`: every path in use with its document count.
  - Designer: Model panel "Field usage" search (suggestions from the index, All versions, Open a result) and a usage button on each model property.
- [x] 15. Save selection as a reusable custom block
  - Shared block library (App_Data/blocks, `/api/blocks`): a copy of the selection (GrapesJS components) with id styles moved onto the components (fresh ids per drop) and the rules of the classes it uses; the class CSS is added when a block is dropped. Labels and categories are shown as text.
  - Designer: "Save as block" on the selection toolbar and in the three-dot menu (name + category), blocks appear under their category in the Blocks panel; Manage Saved Blocks lists and deletes them. Copies are independent (use clauses for linked wording).
- [x] 16. Calculated fields / expression builder
  - Expression language compiled to Liquid on the server (`Templates/ExpressionCompiler.cs`, `POST /api/expressions/compile`): + − × ÷ with precedence and parentheses, sum / count / average over lists, min, max, round (half away from zero), abs, concat, default, days_between; safe division (0 instead of an error), exact decimals, error messages with positions; only paths, numbers and quoted text reach the Liquid.
  - Designer: Calculated Field block + builder (operators, functions, field picker with filter, format, live result with the selected test data via `POST /api/expressions/preview`); canvas shows the result (first item inside repeats); binding problems for unknown fields; saved HTML re-opens as calculated fields.
- [x] 17. Find & replace, spell check, reviewer comments
  - Find and replace (three-dot menu, Ctrl+F) over the author's own text only (never data fields, calculations, clauses or other generated content): match case, whole words, next / previous selects the element, replace one / all; a replacement can't introduce `{{` / `{%`.
  - Spell check (`Spelling/SpellChecker.cs`, WeCantSpell.Hunspell + LibreOffice en_US dictionary from SCOWL, `Spelling/insurance-words.txt`): misspellings with counts and suggestions (a suggestion replaces the whole word everywhere), Ignore, Add to dictionary (shared `App_Data/spelling/custom-words.txt`); acronyms, words with digits and Liquid in typed text are skipped.
  - Reviewer comments (Comments tab, "Comment" on the selection toolbar; `/api/{kind}/{name}/comments`): on an element (anchor kept on the element and saved with the project, never exported; position fallback for unsaved anchors), replies, resolve / reopen, delete, Open / All filter, open-count on the tab, orange outline on commented elements, "no longer on the page" for removed ones. Kept per template name across versions. Reviewer name remembered in the browser.
- [x] 18. Template gallery / duplicate template
  - `GET /api/gallery` (templates and clauses: newest + published version, saved date, scenario and open-comment counts), `GET /api/{kind}/{name}/preview.html` (rendered page for thumbnails: sanitized, CSP `default-src 'none'`, shown in a sandboxed iframe), `POST /api/{kind}/{name}/duplicate` (a version, default published else newest, becomes draft v1 of a new name with its model and optionally its scenarios; 409 if the name exists; comments not copied).
  - Designer: Gallery button (cards with live thumbnails, find by name, kind filter, sort by name / recently saved, Open, Duplicate) and Duplicate… in the three-dot menu; the copy opens in the designer.
- [x] 19. Roles (author / reviewer / publisher) and audit log
  - ASP.NET Core cookie sign-in + role policies (`Security/DesignerSecurity.cs`): every API needs a signed-in user (fallback policy); CanEdit (Author: drafts, imports, scenarios, blocks, duplicate, dictionary), CanPublish (Publisher), CanComment (any role); 401/403 as JSON, cookie HttpOnly + SameSite=Strict. `Security:Enabled` (default false = everyone is the local designer with all roles), `Security:Users` (POC sign-in list; production: Entra ID with the same role claims), `Security:RequireSecondPersonToPublish` (no publishing a version you saved).
  - Audit log (`Security/AuditLog.cs`, append-only `App_Data/audit/audit.jsonl`, `GET /api/audit`): saves, discards, publishes / rollbacks, duplicates, scenarios, blocks, comments, dictionary words, sign-in / out, refusals. Comments are signed with the signed-in name.
  - Designer: sign-in screen (nothing loads before), user chip + Sign out, buttons greyed out by role with the needed role in the tooltip, History dialog (this document / All activity).

## Customization
- [x] 20. Page setup per template (size, orientation, margins) and header/footer designer (first page, odd/even, Page X of Y)
  - Stored in the template's HTML as a hidden `page-setup` component (`div.doc-setup`, settings as classes `ds-size-*`, `ds-orient-*`, `ds-margins-T_R_B_L`, `ds-font-*` because the sanitizer strips data-*), with header/footer slots (`.doc-hf-header|footer` × `default|first|even`, left/center/right). Slot tokens: `[Page]`, `[Pages]`, `[Date]`, `[Logo]`, `{field.path}` (→ Liquid); other text is encoded. Header/footer text goes through Liquid with the document, so fields print.
  - `PdfRenderer`: reads and removes `.doc-setup`, sets paper (Letter/Legal/A4), orientation (also overrides the brand `@page` size), margins and inline-styled Chrome header/footer templates; first-page / even-page variants are printed separately and merged page by page. No setup = the standard Letter page and footer, unchanged. Form/GhostDraft documents keep their own layout.
  - Designer: Page Setup… in the three-dot menu (paper, orientation, margins, header/footer text size, left/center/right parts, token buttons + field list, Different first page, Different odd and even pages, Use Standard Setup); the canvas width follows the page's text area. User Guide: "Page setup, header and footer".
- [x] 21. Watermarks (DRAFT / SPECIMEN / VOID)
  - Template watermark: hidden `watermark` component → `div.doc-watermark` (classes `wm-color-grey|red|green|blue`, `wm-strength-light|medium|strong`, `wm-size-small|medium|large`, `wm-angle-diagonal|horizontal`) + `span.doc-watermark-text` (encoded, ≤ 40 chars). Optional condition (Show If rules) wraps it in `{% if %}`. Brand CSS makes it `position: fixed`, so Chrome prints it centered on every page, over the content and see-through.
  - Render-time stamp: `DocumentComposer.ComposeAsync(..., stamp)` adds a red diagonal word and hides the template's own (`body.doc-watermark-stamped`). `POST /api/render` `{ watermark }`; `POST /api/templates/{name}/pdf?watermark=SPECIMEN`; a **draft** version (`?version=N`) is stamped DRAFT automatically (`X-Watermark` header). Stamp text: letters, numbers, spaces, `.` and `-`, at most 40.
  - Designer: Watermark… in the three-dot menu (presets + other text, color, strength, size, angle, On every document / Only when field condition, Remove Watermark); shown on the canvas with clicks passing through. User Guide: "Watermarks".
- [x] 22. Brand / theme manager (tokens per brand or LOB, custom font upload)
  - `Themes/ThemeStore.cs` (Themes:Root, App_Data/themes): a theme overrides the brand stylesheet's color tokens (read from `:root` of `moe-document.css`, hex only) and the text / heading fonts (`--moe-font-sans`, new `--moe-font-heading`); system fonts or uploaded fonts (WOFF2/WOFF/TTF/OTF checked by magic bytes, ≤ 5 MB, one file per family/weight/style). Theme CSS = `@font-face` (inline data) + `:root{...}`.
  - A template uses a theme through a hidden `theme-ref` component (`div.doc-theme.theme-{name}`); `DocumentComposer` adds the theme CSS after the brand stylesheet (missing theme = clear render error). Theme changes reach published templates at once.
  - API: `GET /api/themes` (tokens, system fonts, themes, fonts), `GET/PUT/DELETE /api/themes/{name}`, `GET /api/themes/{name}/theme.css` (canvas), `GET /api/themes/{name}/usage`, `POST /api/themes/fonts?family&weight&style` (raw file), `DELETE /api/themes/fonts/{id}`. Saving / deleting / fonts need Publisher; audited; themes used by templates and the last file of a used font can't be deleted (409 with who uses them).
  - Designer: Theme… in the three-dot menu (picker with color/font preview, canvas restyled live) and Manage Themes… (Publishers: colors with reset, fonts, uploads, save, delete). User Guide: "Themes: brand colors and fonts".
- [x] 23. Custom formats (masks, locale currency/date, zero/empty text)
  - `Rendering/ValueFormats.cs` + Liquid filters `format: "<.NET pattern>"[, "<culture>"]` and `mask: "(###) ###-####"` (# digit, * hidden digit; wrong digit count prints unchanged). Format follows DocGen's Word filler rule for rule (numbers via decimal.ToString, date-like text via DateTime.ToString, upper/lower), so English (US) formats print identically in the PDF and the Word export (`{{path:pattern}}`). Zero text and (negative) via pattern sections (`$#,##0.00;($#,##0.00);'None'`); empty text stays "If empty, show". Languages: en-US, es-US, es-MX, en-CA, fr-CA.
  - `POST /api/formats/preview` (canvas samples + dialog previews), `GET /api/formats/cultures`. Word export: other languages and masks are reported as warnings.
  - Designer: Format trait (data field, calculated field, total, form field) = presets + Custom… → Custom format dialog (Number builder: style, decimals, thousands, negatives, zero text; Date presets; Mask presets; language; editable pattern; live examples). User Guide: "Custom formats" (+ "Exporting to Word and HTML").
  - Export parity (Word/HTML export from another session): `Tests/Export/ExportParityTests.cs` renders each design through the designer (Chromium) and through DocGen's own `DocxTemplateFiller` (compiled in from ../fact-docgen when present) + LibreOffice, and compares the words of every page (body, header, footer), paper size and margins. Fixes: the Word export now carries the page setup (size, orientation, margins, header/footer with PAGE/NUMPAGES/DATE fields, first/even variants, logo) instead of printing it as body text, and its standard margins match the PDF (0.5/0.5/0.6/0.5).
- [x] 24. Conditional styling
  - Any element can have up to 10 rules (`condStyles` prop: field, Show If operator, value, style). Export: `{% capture cs_<hash> %}{% if cond %}cs-style{% endif %} ...{% endcapture %}` before the element and `{{ cs_<hash> }}` in its class attribute (GrapesJS escapes < > inside attributes, so conditions can't live there). Works per item inside Repeat / Data Table. Styles are brand-CSS classes (`cs-red-text`, `cs-green-text`, `cs-muted`, `cs-bold`, `cs-italic`, `cs-strike`, `cs-highlight-yellow|red|green`, rows highlight all cells).
  - Designer: Conditional Styling… in the three-dot menu (rules dialog, fields in scope incl. loop item fields); canvas applies the styles the test data gives + dashed purple outline with the rules as tooltip. Word export: stripped and reported. User Guide: "Conditional styling".
  - Also (export parity): plain values now print like DocGen's Word filler — true/false as Yes/No (`YesNoValue`, still boolean in conditions), numbers without trailing zeros (1200.0 → 1200).
- [x] 25. Barcodes / QR codes, signature blocks, charts
  - `Rendering/Barcodes.cs` (ZXing.Net 0.16, Apache-2.0): Liquid `{{ x | barcode: "qr|code128|code39|datamatrix|pdf417" }}` => inline SVG, one path, 4-module quiet zone for 2D, 1D stretch to their box height; unencodable / empty / over 1000 chars prints nothing. Tests scan the printed PDF back with ZXing (all five kinds).
  - `Rendering/Charts.cs`: `{{ list | chart: "column|bar|line|pie|donut", "labelPath", "valuePath", "format" }}` => inline SVG (640x320 box, scales to width), value axis incl. negatives, legend with shares for pie/donut, max 50 points, items without numbers skipped, labels encoded; colors are brand-token CSS classes (themes recolor). Sanitizer allows SVG g/text/line/circle/polyline + geometry attributes only.
  - Signature block: line + invisible e-signature anchor (`\s1\`, white 1pt text), name/title from data, role, date (line / print date / data field), optional signature image.
  - Designer: Barcode / QR Code, Chart, Signature blocks (Data); settings with model-aware pickers; canvas previews drawn by the server with the test data. Word export: barcodes/charts/e-sign anchors reported; signature lines kept as a bottom-bordered paragraph. User Guide: "Barcodes, charts and signatures".
- [ ] 26. Tagged PDF (PDF/UA) and PDF/A output, alt-text/contrast checker — skipped for now (2026-10-02)
- [x] 27. Language variants (EN/ES) sharing bindings
  - **Storage:** `Templates/DocumentLanguages.cs` defines en (en-US, the base), es (es-US) and fr (fr-CA). `TemplateStore.Language(code)` keeps each language's versions in `{root}/_lang/{code}` under the same name, with its own Draft -> Published -> Retired lifecycle. Clauses get the same through `ClauseStore.Versions`. The gallery and the template list show base names only.
  - **Language marker:** a variant's HTML carries a hidden `div.doc-language.lang-es`. The server keeps it in step with the `?lang` the version is saved under; English has none, and clauses never carry one.
  - **Composer:** each language has its own Fluid options (culture plus clause provider). The page gets `<html lang>`. Booleans print Sí/No or Oui/Non, and `format` with no language uses the document culture. "No data" on charts and the standard PDF footer (Página X de Y) are translated. Preset formats stay en-US.
  - **Clauses:** a Spanish document includes the published Spanish clause, or a pinned Spanish version; otherwise it falls back to the English clause.
  - **API:** `?lang=` on template and clause versions, draft, discard, publish and diff, on template pdf and exports, and on clause content.
    - A translation needs the English version first (409).
    - `POST /api/templates/{name}/pdf?lang=es` falls back to published English, with `X-Template-Language: en; requested es`.
    - `GET /api/{kind}/{name}/languages`: versions per language, plus the field check. It lists missing and extra data paths compared with English (newest versions, fields of included clauses count) and clauses still printing in English.
  - **Audit:** entries record the language name as detail. Second-person publish compares saves per language, since version numbers overlap across languages.
  - **Word export:** a variant warns that DocGen prints Yes/No, months and numbers in English.
  - **Designer:**
    - A Language selector on the toolbar opens that language's newest version. When the language has no versions, the canvas is kept to be translated and Save Draft creates its v1. The status bar reports the field check.
    - Languages… in the three-dot menu shows the per-language table with Open/Start.
    - The badge shows `· ES`. Canvas samples (Yes/No, custom formats) and clause previews follow the language.
  - Follow-ups: Duplicate copies each translation (published, else newest) as that language's draft v1 (`languagesCopied`; a name held only by a leftover translation counts as taken). Field usage search covers translations (`language` on hits, clause hits show `(es)`) and the index counts a document once. Clause usage lists translated templates (`language`). Gallery entries list `languages` ("also in Spanish"). User Guide: "Languages: Spanish and French versions".

## To discuss (added 2026-10-03)
- [ ] 28. UI for the people who will use it: Word and GhostDraft Studio users. Creating and editing a document must be as easy as in those tools, ideally easier.
  - Who are they day to day (form analysts, developers, business users)? Which tasks take them longest in Word / GhostDraft Studio today?
  - Word habits to keep: typing straight onto the page, ribbon-style formatting, styles, tables, track changes / comments, Ctrl+Z, copy/paste from Word (with formatting).
  - GhostDraft Studio habits to keep: fill points, conditions, repeating sections, packages / reusable content, data model tree, test data, preview.
  - What in the designer is unfamiliar today: GrapesJS panels (Blocks / Layers / Settings / Styles), three-dot menu, Liquid terms (Repeat, Show If). Rename to their vocabulary?
  - Simple vs advanced mode (hide styling panels, Liquid view, form-page tools for most users)?
  - Starting points: start from Word (.docx import), from a GhostDraft template (gd2designer), from a PDF, or from a gallery template. Which is the main path?
  - Walk through 2–3 real documents end to end with a user (create, bind fields, preview, publish) and time it against Word / GhostDraft Studio.
- [ ] 29. Source control of documents.
  - Today: each template / clause / theme is a JSON file in `App_Data` with Draft -> Published -> Retired versions, diff before publish, audit log. No Git, branches or pull requests.
  - Options: keep the designer's own versioning as the system of record; mirror every publish into a Git repo (template HTML/CSS, project JSON, model, scenarios) for history and review; or make Git the store (designer commits, PR = review / second-person publish).
  - What is reviewed and by whom (business sign-off vs developer review)? Do tests / golden renders run on a PR?
  - How clauses, themes, fonts, blocks and translations are versioned together with the templates that use them.
- [ ] 30. Publishing to environments (Dev / Test / UAT / Prod).
  - Today: one store; "Published" means live for whoever renders from it. No environment promotion.
  - Options: one designer per environment with promotion (export / import a release package); one designer with per-environment published versions (published to Test, promoted to Prod); or a release pipeline (Azure DevOps) deploying from Git to each environment's DocGen store.
  - What a release contains (templates + the exact clause / theme / font versions they use), and how to roll one back.
  - How fact-docgen finds the published version in each environment (blob storage, API, database), and who may promote to Prod (roles, second person, change ticket).

## Roll-out
- [x] 31. Feature flags (2026-10-05)
  - `Features/FeatureFlags.cs`: 25 known features, all on by default. `Features:{Name}=false` switches one off; `Features:Profile=demo` applies `FeatureProfiles:demo` from appsettings.json, and a direct flag wins over the profile. `GET /api/features` returns `{ profile, features }`.
  - Designer: controls tagged `data-feature` get the `feature-off` class and are hidden. Blocks of features that are off are removed (Clauses, CalculatedFields, Visuals, MaterialBlocks). With Clauses off the gallery shows templates only; with Duplicate off it has no Duplicate button. Ctrl+F follows FindReplace, and Liquid view falls back to Sample values.
  - Demo profile: `dotnet run --launch-profile demo`. It hides Languages, Themes, Watermarks, Conditional styling, Barcodes/charts/signatures, Calculated fields, Saved blocks, Field usage, Export, Liquid view, GhostDraft and Documaker import, and Material blocks.
  - The server endpoints stay available; flags only hide the UI. Tests: `Features/FeatureFlagTests.cs`, including a designer started with the demo profile.
- [x] 32. Add to Designer Library from the GhostDraft review page (2026-10-05)
  - The review page has an **Add to Designer Library** button (GhostDraft cases only; feature flag `ReviewToLibrary`). It opens `/?addToLibrary={case}` in a new tab, where the designer imports the form's gd2designer conversion and saves it as draft v1 of a template named after the form.
  - `GET /api/review/cases/{case}/designer` returns the conversion. `Review/GhostDraftConversions.cs` maps the case's FormCode_Edition to the GhostDraft form via `ghostdraft-forms.csv`, then to the batch JSON whose `source` is that .gd. Paths are configurable: `GhostDraftReview:FormsCsv` and `GhostDraftReview:TemplatesRoot`. All 79 current cases resolve.
  - Tests: `Review/LibraryTests.cs`, including the review page → designer → saved draft flow.
- [x] 33. Fix binding problems from the Model panel; + to add a property (2026-10-05)
  - A "not in the data model" problem has **Add to model**: the path is created in the model with an example value from how it is used (format, condition, check box, image). Paths through a loop item name go on every item of the list; `list.size` makes an empty list; the lists of the surrounding Repeats / Data Tables are created first and the loops re-take their item names. **Add all to model** when there are several.
  - `group.size` (GhostDraft conversions count groups) offers **Use {list}.size** for each list inside the group instead (real case: `Auto.Snowmobiles2.OtherAutoCoverages.size` on CA 20 21).
  - A + on every group and list in the Model tree, and at the top of the panel: name + type (text, number, date, yes/no, group, list).
  - Clicking the problem still selects the element. Tests: `ModelPanel/ModelQuickFixDesignerTests.cs`.

## End-of-October POC: Orbital model → Commercial Auto Dec page, end to end
- [x] 34. Dec page document model + template (week 1, started 2026-10-05)
  - Master model: Orbital `Orbital.Schemas/schemas/quote/quote.bundled.schema.json` (import into the designer: 461 fields, 66 lists; JSON Schema import now fills in from every property, using the schema's example only for values).
  - Document model: `models/commercial-auto-dec/commercial-auto-dec.schema.json` = `quote` (Orbital, unchanged) + `policy`, `carrier`, `agency`, `rating` (premium and covered auto symbols per coverage line), `autos` (each vehicle joined with its garaging address and premiums), `forms`. Sample: `commercial-auto-dec.sample.json` (Orbital commercial-auto-monoline sample + supplements).
  - Template `commercial-auto-dec` v1 (draft): ITEM ONE named insured / period / agent, ITEM TWO coverage schedule (a line prints only when it has a premium), ITEM THREE covered autos, forms schedule. Generate: `POST /api/templates/commercial-auto-dec/pdf` with the model.
- [ ] 35. Hydrator: build the document model for a quote / policy (Orbital + POINT + agency + forms rules)
- [ ] 36. Templates published to blob storage; Designer renderer in fact-docgen (ICustomDocumentRenderer)
- [ ] 37. Designer page in fact-commercial-web (Entra) + "Generate Dec page" on a policy
  - Target look: manager's Claude Design prototype "Document Designer" (Designer / Library management, Templates · Data · Assets · Logic rail, Design / Preview / Liquid, Properties). POC scope agreed: single-template publish; libraries (versioned sets), tabs, assets and the Logic list after the POC.
  - Started 2026-10-06 in `fact-commercial-web` (uncommitted, on top of the local working copy): `src/views/DocumentDesigner.vue` (`/document-designer`, side-nav entry), `src/composables/DocumentDesigner/UseDesignerBridge.ts` (window messages, origin-checked; unit tests `src/tests/unit/useDesignerBridge.test.ts`), `UseDocumentDesignerApi.ts` (templates, document models, render via `useFetch`), `src/models/DocumentDesignerModels.ts`, `.env` `VUE_APP_DOC_DESIGNER_URL`. Top bar (Designer / Library, open template, unsaved, Preview values / Field names / Liquid, Save, Publish, Generate PDF), left rail Templates (v-data-table, New template dialog) and Data (document model, Show sample record, quote/policy number placeholder for the hydrator), the designer embedded.
  - Designer side: CORS for `Embed:AllowedOrigins` (Development: http://localhost:4200), `GET /api/document-models` + `/{name}/sample|schema` (repo `models/` folder, `DocumentModels:Root`).
  - Still to do: Entra sign-in for the designer API, hydrator (35), blob storage (36).
- [x] 38. Template details + embedded mode (2026-10-06)
  - Template details (title, form code, edition, type, category) are saved with each version (`TemplateDetails`, checked on save) and listed by `GET /api/templates` (with the newest version's status) and `/versions`. The designer's Settings tab edits them when nothing is selected. Choices: `GET /api/template-details`.
  - Embedded mode `/?embed=1`: no title bar; the host app drives the designer with window messages (`open`, `new`, `save`, `publish`, `preview`, `setData` = show a real record, `setModel`, `setDetails`, `view`, `getState`) and hears `ready`, `state`, `status` and a `result` per command. Only parent pages from `Embed:AllowedOrigins` (https, or http on localhost) are listened to, and every response carries `Content-Security-Policy: frame-ancestors 'self' <allowed origins>`.
  - Tests: `Embedding/EmbedAndDetailsTests.cs` (a host page on a second origin creates, fills, saves and publishes a template; a non-allowed origin can't frame it).
