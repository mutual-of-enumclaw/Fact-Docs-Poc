# CLAUDE.md — fact-pdf-tools

A three-pillar app for working with Mutual of Enumclaw legacy Documaker (FAP/DDT) insurance forms:
1. **Convert** — render any FAP form to a fillable PDF
2. **Author** — drag/drop canvas editor to create or modify forms
3. **Populate** — load live policy data from the Commercial API, fill form fields, save test scenarios

---

## Running the App

**Server** (ASP.NET Core 9, port 5035):
```bash
cd server
dotnet run
```

**Client** (React + Vite, port 5173):
```bash
cd client
npm install   # first time only
npm run dev
```

Both must be running. The client calls `http://localhost:5035/api` directly (hardcoded in `client/src/api/formsApi.ts`).

**Server config** lives in `server/appsettings.Development.json`. The server uses `IOptions<T>` — it does **not** hot-reload config, so restart after changing paths.

---

## Configuration (`appsettings.Development.json`)

```json
{
  "FormFiles": {
    "FormDatPath":          "C:\\EDrive\\FORM.DAT",
    "FormsDirectory":       "C:\\src\\FaCT-DocProd-Development\\mstrres\\MOEC0\\FORMS",
    "DdtDirectory":         "C:\\src\\FaCT-DocProd-Development\\mstrres\\MOEC0\\DDTLIB",
    "PdfOutputDirectory":   "C:\\src\\fact-pdf-tools\\output",
    "FxrPath":              "C:\\EDrive\\moec0\\Mstrres\\MOEC0\\DEFLIB\\REL103.FXR",
    "AuthoredFormsDirectory": "C:\\src\\fact-pdf-tools\\output\\authored-forms",
    "ScenariosDirectory":   "C:\\src\\fact-pdf-tools\\output\\scenarios"
  }
}
```

---

## Converting a Form

In the UI: go to **Convert**, enter the Form Number and Edition Date, click **Convert & Preview**.

- **Form Number** — the FORM.DAT key (e.g. `CA0001`) or a direct FAP filename with no extension (e.g. `QTE_BILLINFO`).
- **Edition Date** — 4-digit MMYY (e.g. `0306`, `1013`). Leave blank when using a direct FAP filename.

The server resolves the FAP filename via FORM.DAT. If no match is found it falls back to treating the Form Number as a literal FAP filename and searches `FormsDirectory`.

**API equivalent:**
```http
POST http://localhost:5035/api/convert
Content-Type: application/json
{ "formNumber": "QTE_BILLINFO", "editionDate": "" }
→ application/pdf (binary)
```

---

## FAP File Format Reference

FAP files live in `FormsDirectory` (e.g. `mstrres/MOEC0/FORMS/`). The matching AGCYLNK directory (`mstrres/AGCYLNK/FORMS/`) is always an identical copy — changes must be applied to both.

### Coordinate System

All FAP coordinates are in **2400-DPI units** (row, col). Origin is the top-left of the page.

- Default portrait Letter page: width = `20400`, height = `26400`
- Convert to PDF points: `pdfPts = fapUnits × (72 / 2400) = fapUnits × 0.03`
- Convert from PDF points: `fapUnits = pdfPts × (2400 / 72) ≈ pdfPts × 33.33`
- Typical left margin: col `1800` (~54pt / ~0.75 in from left)
- Typical top margin: row `500–1000`

Coordinate tuple order is always **(row1, col1, row2, col2)** where row1/col1 is top-left and row2/col2 is bottom-right.

---

### Line Types

#### `H,` — Page Header (first line of every FAP file)
```
H,2400,(98,0),(1800,0,8400,20400),FILENAME
```
Defines resolution (2400 DPI) and page dimensions. The parser extracts `PageWidth` and `PageHeight` from this line.

---

#### `V,` — Version Entry (skip)
Audit history. Do not modify. The parser ignores these.

---

#### `A,N1,` — Annotation/Version Stamp
```
A,N1,(0,0,0,0)," ","June 30, 2026 "," ","CMorehouse "," ",0,1
```
Update the date and author when you make changes. The parser ignores these but Documaker uses them for audit.

---

#### `T,` — Static Text
```
T,(row1,col1,row2,col2),(fontId,s1,s2,s3),length,Text here
```
Renders fixed (non-editable) text. The bounding box is the full text area including padding. Text starts at the top-left of the box.

Example:
```
T,(500,1800,932,7200),(16114,544,325,432),24,Available Payment Plans:
```

**Font codes** (most common):
| Code | Usage |
|---|---|
| `16114` | Large bold heading |
| `16112` | Standard bold (column headers) |
| `16012` | Standard regular (body/data) |
| `16010` | Small regular (footnotes/fine print) |

---

#### `F,` — Fillable Field
```
F,(row1,col1,row2,col2),(fontId,s1,s2,s3),length,FIELDNAME
```
Creates an AcroForm text field in the PDF. The field name is used as-is for `FillFields()`.

Example:
```
F,(1200,1800,1600,8400),(16012,464,424,368),20,INSURED_NAME
```

---

#### `X,` — Line or Rectangle

**CRITICAL**: The format requires **two** parenthetical groups. Missing the second group causes the parser to silently discard the line (no error, no grid).

```
X,(row1,col1,row2,col2),(24,24),lineWidth,style
```

**Rendering rules** (from `FapToPdfGenerator.RenderLines`):
- If `row1 == row2` (±10): draws a **horizontal line**
- If `col1 == col2` (±10): draws a **vertical line**
- Otherwise: draws a **rectangle** (outer box, or a single cell)

Use **one X line per table cell** (not one outer box + separate dividers). Each cell is its own rectangle. Adjacent cells share edges at the same coordinates.

Example — a 2-column, 2-row table from col 1800–10800, row 1680–3120:
```
X,(1680,1800,2160,6300),(24,24),1,0   ← header row, col 1
X,(1680,6300,2160,10800),(24,24),1,0  ← header row, col 2
X,(2160,1800,2640,6300),(24,24),1,0   ← data row, col 1
X,(2160,6300,2640,10800),(24,24),1,0  ← data row, col 2
```

Line thickness: `lineWidth` is in FAP units. `Math.Max(0.5f, lineWidth × 0.03)` PDF points. Use `1` for a standard ~1pt border.

---

#### `M,H,` / `M,TT,` / `M,P,` / `M,E` — Pre-positioned Text Block (legacy)
Used in older FAP files for flowing/wrapped text blocks. Each `M,TT` is a single pre-positioned word token.

```
M,H,(row1,col1,row2,col2),0,(fontId,...),text,cols,flags...
M,P,0,33,0,0,0,0,0,0          ← paragraph break
M,TT,(row1,col1,row2,col2),(fontId,...),length,Word text
M,O,(row,endCol,row,endCol),(fontId,...),1,0,0,0,0  ← token end marker
M,E                            ← end of text area
```

Prefer `T,` lines for new content. The `M,TT` format is harder to position and maintain.

---

#### `N,` — Image Reference
```
N,(row1,col1,row2,col2),0,imageType,length,Description
```
References embedded images (e.g. company logo). Do not remove — leave as-is.

---

## Creating a Table

To create a table that matches the DocProd visual style (like the payment plans table):

1. **Calculate column widths.** Page usable width = col `1800` to col `18600` = 16800 units. Divide equally: `16800 / numCols` units per column.

2. **Define row heights.** Header + data rows at ~480 units each (`2160 - 1680 = 480`). Adjust for content.

3. **Write one `X,` per cell** using correct format `(coords),(24,24),1,0`.

4. **Write `T,` for each cell's text.** Position text ~100 units inside the cell boundary:
   - Text left edge = cell col1 + 100
   - Text right edge = cell col2 - 100
   - Text top = cell row1 + 20
   - Text bottom = cell row2 - 20
   - Use `(16112,472,282,376)` for header row, `(16012,464,424,368)` for data rows.

5. **Write footnotes** as `T,` lines below the table using font `(16010,392,352,312)`. Each `T,` line spans the full table width (col `1800` to `18600`). Allow ~368 row units per footnote line.

### Complete 5-Column Table Example

(Table from `QTE_BILLINFO.FAP` — 5 equal columns, rows 1680–4080, cols 1800–18600)

```
T,(500,1800,932,7200),(16114,544,325,432),24,Available Payment Plans:

X,(1680,1800,2160,5160),(24,24),1,0
X,(1680,5160,2160,8520),(24,24),1,0
X,(1680,8520,2160,11880),(24,24),1,0
X,(1680,11880,2160,15240),(24,24),1,0
X,(1680,15240,2160,18600),(24,24),1,0
X,(2160,1800,2640,5160),(24,24),1,0
X,(2160,5160,2640,8520),(24,24),1,0
X,(2160,8520,2640,11880),(24,24),1,0
X,(2160,11880,2640,15240),(24,24),1,0
X,(2160,15240,2640,18600),(24,24),1,0
X,(2640,1800,3120,5160),(24,24),1,0
X,(2640,5160,3120,8520),(24,24),1,0
X,(2640,8520,3120,11880),(24,24),1,0
X,(2640,11880,3120,15240),(24,24),1,0
X,(2640,15240,3120,18600),(24,24),1,0
X,(3120,1800,3600,5160),(24,24),1,0
X,(3120,5160,3600,8520),(24,24),1,0
X,(3120,8520,3600,11880),(24,24),1,0
X,(3120,11880,3600,15240),(24,24),1,0
X,(3120,15240,3600,18600),(24,24),1,0
X,(3600,1800,4080,5160),(24,24),1,0
X,(3600,5160,4080,8520),(24,24),1,0
X,(3600,8520,4080,11880),(24,24),1,0
X,(3600,11880,4080,15240),(24,24),1,0
X,(3600,15240,4080,18600),(24,24),1,0

T,(1700,1900,2140,5060),(16112,472,282,376),12,Payment Plan
T,(1700,5260,2140,8420),(16112,472,282,376),9,Frequency
T,(1700,8620,2140,11780),(16112,472,282,376),10,Paper Bill
T,(1700,11980,2140,15140),(16112,472,282,376),14,Paperless Bill
T,(1700,15340,2140,18500),(16112,472,282,376),14,Easy Pay (EFT)

T,(2180,1900,2620,5060),(16012,464,424,368),7,10-Pay*
T,(2180,5260,2620,8420),(16012,464,424,368),14,Easy Pay (EFT)
T,(2180,8620,2620,11780),(16012,464,424,368),5,$3.00
T,(2180,11980,2620,15140),(16012,464,424,368),5,$1.00
T,(2180,15340,2620,18500),(16012,464,424,368),17,No service charge
...
```

---

## Editing an Existing FAP File

1. Open in any text editor. Files are plain ASCII.
2. Make changes to `T,`, `X,`, or `F,` lines.
3. Update the `A,N1,` annotation line with today's date and your name.
4. **Always apply the same change to both MOEC0 and AGCYLNK.** Simplest: edit MOEC0, then `cp` to AGCYLNK.
5. Reload in the Convert page — the server reads FAP files fresh on every request, no restart needed.

---

## Common Gotchas

| Problem | Cause | Fix |
|---|---|---|
| Table has no grid lines | `X,` lines missing the second `(24,24)` group | Format must be `X,(coords),(24,24),1,0` |
| Grid lines draw diagonally | Single outer box `X` line with different row AND col — rendered as DrawRectangle, not a cross | Use one `X` line per cell instead |
| Parser silently drops X lines | Second paren group missing — parser throws, caught, returns `null` | Add `,(24,24)` before the width |
| PDF viewer spins forever | Blob created via `res.blob()` has no MIME type | Use `new Blob([await res.arrayBuffer()], { type: 'application/pdf' })` |
| Form not found | FORM.DAT lookup failed and FAP filename also not found | Check `FormsDirectory` path in `appsettings.Development.json`; server needs restart after config change |
| Config changes not picked up | Server uses `IOptions<T>` (not `IOptionsMonitor`) | Restart the server |

---

## API Quick Reference

| Method | Endpoint | Purpose |
|---|---|---|
| GET | `/api/forms` | List all FORM.DAT entries (optional `?search=` prefix filter) |
| GET | `/api/forms/{form}/{edition}/info` | Field/text/line counts + field names |
| GET | `/api/forms/{form}/{edition}/fields` | Full FieldDescriptor list (position, font, DDT method) |
| POST | `/api/convert` | `{formNumber, editionDate}` → PDF bytes |
| POST | `/api/convert/fill` | `{formNumber, editionDate, fieldValues, flatten}` → filled PDF |
| GET | `/api/definitions` | List authored form definitions |
| POST | `/api/definitions/import-legacy` | `{formNumber, editionDate}` → seed new authored definition from FAP |
| POST | `/api/definitions/{id}/render` | Render authored form → PDF |
| GET | `/api/policy/{policyNumber}?env=` | Fetch CDM policy (env: dev/dev3/tst/tst2/acc) |
| POST | `/api/policy/field-values` | `{formNumber, editionDate, policyNumber, environment}` → field value map |
| GET | `/api/scenarios` | List saved test scenarios |
| POST | `/api/scenarios` | `{name, formNumber, editionDate, fieldValues}` → create scenario |
| POST | `/api/scenarios/{id}/run` | Render scenario → PDF |

---

## Project Structure

```
core/         Shared models + infrastructure (FormFileClient, FapToPdfGenerator, etc.)
server/       ASP.NET Core API + controllers
client/       React + TypeScript UI (Vite, port 5173)
population/   Policy → form field mapping (IFormFieldMap implementations per form)
output/       Runtime output: authored-forms/, scenarios/, generated PDFs
```

### Key classes

- **`FormFileClient`** — parses FORM.DAT, FAP files, DDT files; resolves file paths
- **`FapToPdfGenerator`** — renders `FapParseResult` or `FormDefinition` → PDF bytes via Spire.PDF
- **`FapFormDefinitionConverter`** — converts between FAP 2400-DPI coords and PDF-point `FormDefinition`
- **`FxrFontLibrary`** — resolves FAP font IDs to real typefaces via the `.FXR` Documaker font file
- **`FormFieldMapRegistry`** — maps `(formNumber, editionDate)` to an `IFormFieldMap` for policy population

### Adding a new field map (policy population)

Implement `IFormFieldMap` in `population/Maps/`, register it in `FormFieldMapRegistry`, and rebuild. The map receives a CDM `PolicyView` and returns `Dictionary<string, string>` of FAP field name → value.
