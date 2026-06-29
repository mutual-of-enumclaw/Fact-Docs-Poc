# FaCT PDF Tools

A developer-focused toolkit for working with Mutual of Enumclaw legacy **Documaker** (FAP/DDT) insurance forms. The app has three main pillars:

| Pillar | Page | What it does |
|--------|------|-------------|
| **Convert** | `/convert` | Render any FAP form to a fillable PDF, manually enter field values, and optionally load live policy data to auto-fill fields |
| **Author** | `/design` | Drag-and-drop canvas editor to create new forms or modify existing ones; import a legacy FAP file as a starting point |
| **Populate / Scenarios** | `/scenarios` | Save named test scenarios (form + field values), rerun them at any time, and preview the rendered output |

---

## Architecture

```
client/      React + TypeScript UI  (Vite, port 5173)
server/      ASP.NET Core 9 API     (port 5035)
core/        Shared models + FAP parsing + PDF generation (Spire.PDF)
population/  Policy → field mapping (IFormFieldMap implementations per form)
output/      Runtime output: authored-forms/, scenarios/, generated PDFs
```

**Data flow**

```
Browser  ──HTTP──►  server (ASP.NET Core)
                        │
                        ├─ FormFileClient    ── reads FAP/DDT files from disk
                        ├─ FapToPdfGenerator ── converts FAP parse result → PDF bytes (Spire.PDF)
                        ├─ FormDefinitionStore ─ persists authored forms as JSON under output/
                        ├─ ScenarioStore     ── persists test scenarios as JSON under output/
                        └─ CommercialApiPolicyClient ── fetches live policy from Commercial API
```

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Node.js 20+](https://nodejs.org/) with npm
- Access to the Documaker form files on disk (see Configuration below)
- *(Optional)* Network access to the Commercial API environments for live policy loading

---

## Running the App

Both the server and client must be running at the same time.

### 1 — Start the server (API)

```bash
cd server
dotnet run
```

The server starts on **http://localhost:5035**. On first run it will compile the solution, which may take 30–60 seconds.

### 2 — Start the client (UI)

```bash
cd client
npm install   # first time only
npm run dev
```

The UI starts on **http://localhost:5173**. Open that URL in your browser.

> The server must already be running before you use the UI; all data comes from the API.

---

## Configuration

Server configuration lives in `server/appsettings.Development.json`. Edit this file to point at your local form file directories. **Restart the server** after any config change (the server does not hot-reload config).

```json
{
  "FormFiles": {
    "FormDatPath":            "C:\\EDrive\\FORM.DAT",
    "FormsDirectory":         "C:\\src\\FaCT-DocProd-Development\\mstrres\\MOEC0\\FORMS",
    "DdtDirectory":           "C:\\src\\FaCT-DocProd-Development\\mstrres\\MOEC0\\DDTLIB",
    "PdfOutputDirectory":     "C:\\src\\fact-pdf-tools\\output",
    "FxrPath":                "C:\\EDrive\\moec0\\Mstrres\\MOEC0\\DEFLIB\\REL103.FXR",
    "AuthoredFormsDirectory": "C:\\src\\fact-pdf-tools\\output\\authored-forms",
    "ScenariosDirectory":     "C:\\src\\fact-pdf-tools\\output\\scenarios"
  }
}
```

| Key | Purpose |
|-----|---------|
| `FormDatPath` | Path to `FORM.DAT` — the Documaker form catalog index |
| `FormsDirectory` | Directory containing `.FAP` form layout files |
| `DdtDirectory` | Directory containing `.DDT` field definition files |
| `PdfOutputDirectory` | Where the server writes generated PDF files |
| `FxrPath` | Path to the `.FXR` font library file (maps FAP font IDs to typefaces) |
| `AuthoredFormsDirectory` | Where authored form definitions (JSON) are persisted |
| `ScenariosDirectory` | Where test scenario files (JSON) are persisted |

---

## Using the App

### Catalog (`/catalog`)

Browse all forms registered in `FORM.DAT`. Use the search box to filter by form number, name, or description. Click any row to jump directly to the Convert page with that form pre-loaded.

### Convert (`/convert`)

**Rendering a form:**

1. Enter the **Form Number** — either the `FORM.DAT` key (e.g. `CA0001`) or a direct FAP filename with no extension (e.g. `QTE_BILLINFO`).
2. Enter the **Edition Date** — a 4-digit `MMYY` code (e.g. `0306`). Leave blank when using a direct FAP filename.
3. Click **Convert & Preview**. The rendered PDF appears inline in the browser.

**Manually filling fields:**

After rendering, the page lists all AcroForm fields found in the form. Type values into the fields and click **Fill & Preview** to see the filled result. Check **Flatten** to bake field values into the PDF (non-editable output); uncheck it to keep the PDF interactive.

**Loading field values from a live policy:**

1. Expand the **Load from Policy** section.
2. Select an environment (`dev`, `dev3`, `tst`, `tst2`, `acc`).
3. Enter a policy number and click **Load from Policy**. The app calls the Commercial API and uses the registered `IFormFieldMap` for the current form to populate field values automatically.
4. Click **Fill & Preview** to render the populated form.

> If no field map is registered for the selected form, a message will explain which forms are currently supported.

**Saving a scenario:**

Click **Save as Scenario**, give it a name, and the current form + field values are saved as a reusable test scenario under `output/scenarios/`.

**Opening in the designer:**

Click **Open in Designer** to switch to the Design page with the current form's authored definition pre-loaded (importing from FAP if no authored version exists yet).

**Downloading:**

Click **Download PDF** to save the most recently rendered PDF to your machine.

---

### Design (`/design`)

A canvas editor for creating and modifying forms as structured JSON definitions (stored independently of the original FAP files).

**Loading an existing authored form:**

Pick a form from the left sidebar list.

**Importing a legacy FAP form:**

Click **Import Legacy**, enter a Form Number and Edition Date, and the server parses the FAP file and converts it to an authored definition. The imported definition is automatically selected and ready to edit.

**Canvas elements:**

| Element | How to add | Properties |
|---------|-----------|-----------|
| **Field** (AcroForm text field) | Click **+ Field** | Name, position, size, font, point size, bold, max length |
| **Static Text** | Click **+ Text** | Text content, position, size, font, point size, bold |
| **Line / Rectangle** | Click **+ Line** | Start/end coordinates, line width, style |

- Click an element on the canvas to select it and edit its properties in the right panel.
- Drag elements to reposition them.
- Use the **page selector** (bottom toolbar) to navigate multi-page forms.
- Click **Delete** in the properties panel to remove a selected element.

**Saving:**

Click **Save** to persist the definition to `output/authored-forms/` as a JSON file.

**Preview:**

Click **Preview** to render the current definition to a PDF and display it in a side drawer without saving.

**Deleting a definition:**

Select a form in the sidebar and click the trash icon to permanently delete the authored definition. (The original FAP file on disk is not affected.)

---

### Scenarios (`/scenarios`)

Save and replay named test cases — useful for regression testing form output as FAP files or field maps change.

**Creating a scenario:**

1. Click **New Scenario**.
2. Enter a name, form number, and edition date.
3. Add field name → value pairs using the editor.
4. Click **Save**.

Alternatively, use **Save as Scenario** from the Convert page to capture the current form and field values directly.

**Running a scenario:**

Click the **Run** (play) button on any scenario row. The server renders the form with the stored field values and displays the result in a PDF preview drawer.

**Editing a scenario:**

Click the **Edit** (pencil) button to update the name, form, edition, or field values.

**Deleting a scenario:**

Click the **Delete** (trash) button. Requires confirmation. The JSON file under `output/scenarios/` is removed.

---

## Adding a New Field Map (Policy Population)

To enable live policy loading for a new form:

1. Create a class in `population/Maps/` that implements `IFormFieldMap`:

```csharp
public class MyFormFieldMap : IFormFieldMap
{
    public string FormNumber  => "CA0001";
    public string EditionDate => "0306";

    public Dictionary<string, string> MapFields(CDMPolicyView policy)
    {
        return new Dictionary<string, string>
        {
            ["INSURED_NAME"]   = policy.Insured?.Name ?? "",
            ["POLICY_NUMBER"]  = policy.Number ?? "",
            // ...add more field name → value mappings
        };
    }
}
```

2. Register it in `server/Program.cs`:

```csharp
builder.Services.AddSingleton<IFormFieldMap, MyFormFieldMap>();
```

3. Rebuild and restart the server. The new form/edition combo will now support **Load from Policy** in the Convert page.

---

## API Reference

The server exposes a REST API consumed by the UI. All endpoints are relative to `http://localhost:5035`.

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/forms` | List all FORM.DAT entries. Optional `?search=` prefix filter. |
| `GET` | `/api/forms/{form}/{edition}/info` | Field/text/line counts + field names for a form |
| `GET` | `/api/forms/{form}/{edition}/fields` | Full `FieldDescriptor` list (position, font, DDT method) |
| `POST` | `/api/convert` | `{formNumber, editionDate}` → PDF bytes |
| `POST` | `/api/convert/fill` | `{formNumber, editionDate, fieldValues, flatten}` → filled PDF |
| `GET` | `/api/definitions` | List all authored form definitions |
| `GET` | `/api/definitions/{id}` | Get one authored definition |
| `PUT` | `/api/definitions/{id}` | Save / upsert an authored definition |
| `DELETE` | `/api/definitions/{id}` | Delete an authored definition |
| `POST` | `/api/definitions/{id}/render` | Render an authored definition → PDF |
| `POST` | `/api/definitions/import-legacy` | `{formNumber, editionDate}` → seed definition from FAP |
| `GET` | `/api/policy/{policyNumber}?env=` | Fetch CDM policy (env: dev/dev3/tst/tst2/acc) |
| `POST` | `/api/policy/field-values` | `{formNumber, editionDate, policyNumber, environment}` → field value map |
| `GET` | `/api/scenarios` | List all saved test scenarios |
| `POST` | `/api/scenarios` | `{name, formNumber, editionDate, fieldValues}` → create scenario |
| `PUT` | `/api/scenarios/{id}` | Update a scenario |
| `DELETE` | `/api/scenarios/{id}` | Delete a scenario |
| `POST` | `/api/scenarios/{id}/run` | Render a scenario → PDF |

OpenAPI spec is available at **http://localhost:5035/openapi/v1.json** while running in Development mode.

---

## FAP File Format Overview

FAP files are plain ASCII. Each line type controls a different element:

| Prefix | Element |
|--------|---------|
| `H,` | Page header — defines resolution (2400 DPI) and page dimensions |
| `T,` | Static text |
| `F,` | Fillable AcroForm field |
| `X,` | Line or rectangle (cell-per-`X,` approach required for tables) |
| `M,H/TT/P/E` | Pre-positioned text block (legacy; prefer `T,`) |
| `N,` | Embedded image reference |
| `V,` | Version/audit entry (do not modify) |
| `A,N1,` | Annotation stamp — update when editing a file |

All coordinates are in **2400-DPI units** with `(row1, col1, row2, col2)` order (top-left → bottom-right). Convert to PDF points: `pdfPts = fapUnits × 0.03`.

See `CLAUDE.md` in the repo root for the complete format reference, coordinate system details, and table construction examples.

---

## Editing FAP Files Directly

1. Open the `.FAP` file in any text editor (files are in `FormsDirectory`).
2. Make changes to `T,`, `X,`, or `F,` lines.
3. Update the `A,N1,` annotation line with today's date and your name.
4. Copy the same changes to the matching `AGCYLNK/FORMS/` directory.
5. Re-render in the Convert page — the server reads FAP files fresh on every request; **no server restart is needed**.

---

## Common Troubleshooting

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| Form not found | `FORM.DAT` lookup failed | Verify `FormDatPath` and `FormsDirectory` in config; restart server |
| Config changes not picked up | Server uses `IOptions<T>` (no hot-reload) | Restart the server |
| Table has no grid lines | `X,` lines missing the `(24,24)` group | Format must be `X,(coords),(24,24),1,0` |
| Grid lines render diagonally | Single outer-box `X,` instead of per-cell | Use one `X,` line per cell |
| PDF viewer spins in browser | Blob missing MIME type | Ensure the API client uses `new Blob([...], { type: 'application/pdf' })` |
| "No field map" message | `IFormFieldMap` not registered for this form+edition | Add and register an `IFormFieldMap` implementation |
