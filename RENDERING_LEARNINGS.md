# FAP → PDF Rendering: Agent Learnings

Lessons learned diagnosing and fixing the `FapToPdfGenerator` for WIP multi-page forms like **MCS90A**. Read this before touching `FapToPdfGenerator.cs`, `FormFileClient.cs`, or any FAP parsing code.

---

## Diagnostic Workflow

### Render a form to PNG for visual inspection

The `demo/` project has a `render-preview` command that renders a PDF from disk to per-page PNGs:

```powershell
# First produce the PDF via the API
Invoke-WebRequest -Uri "http://localhost:5035/api/convert" `
    -Method POST -ContentType "application/json" `
    -Body '{"formNumber":"MCS90A","editionDate":""}' `
    -OutFile "C:\src\fact-pdf-tools\output\MCS90A_preview.pdf"

# Then rasterise each page to PNG (output\MCS90A_p1.png, p2.png, p3.png)
cd C:\src\fact-pdf-tools\demo
dotnet run -- render-preview
```

The `render-preview` branch in `demo/Program.cs` loads `output\MCS90A_preview.pdf` and calls `Spire.Pdf.PdfDocument.SaveAsImage(pageIndex, dpiX, dpiY)`. **Use `view_image` on the PNGs to diagnose layout issues visually before touching code.**

---

## The Three Root Causes of "Visual Mess" in WIP Forms

### 1. M,TT Multi-Column Text — Column Bleeding

**Problem:** Documaker pre-positions every word token (M,TT) at absolute `(row1,col1,row2,col2)` coordinates. In multi-column layouts (e.g. two-column definitions on MCS90A page 2), both columns occupy the same Y range. Calling `canvas.DrawString(text, font, brush, x, y)` with no bounding rect lets long tokens overflow their column boundary and bleed into the adjacent column.

**Visual symptom:** Pages 2 and 3 looked like garbled overlapping text, impossible to read.

**Fix:** Before rendering each M,H text area, push a graphics state and set a clip path to the area's `(col1, row1, col2, row2)` rectangle. Tokens render at their absolute pre-positioned X/Y but are clipped to their column. Pop the state after the area.

```csharp
// In FapToPdfGenerator.RenderTextAreas:
var clipPath = new PdfPath();
clipPath.AddRectangle(new RectangleF(areaX, areaY, areaW, areaH));
canvas.SetClip(clipPath);
// ... render tokens at their absolute positions ...
canvas.Restore(state);
```

**Do NOT** use the token-level bounding rect as a DrawString rect — that word-wraps and clips single-line tokens to their tiny height box, making them invisible.

---

### 2. M,TT Area Structure — Parent vs Column Blocks

Many M,H blocks come in triplets: one **parent** full-width block followed by two (or three) **column** blocks that contain the actual tokens. The parent always has 0 tokens. The column blocks overlap the same Y range but each covers only one column's X range.

Example from MCS90A:
```
M,H,(3900,3300,7900,19300),...   ← full-width parent — 0 tokens (skip)
M,H,(3900,3300,7900,11050),...   ← left column — 17 tokens
M,H,(3900,11550,7900,19300),...  ← right column — 14 tokens
```

The `FlushTextArea()` call already skips blocks with 0 tokens, so this works correctly without special handling. Just be aware of the pattern when reading FAP files.

---

### 3. Phantom F, Field Positions — A,T1 Inline Anchors

**Problem:** Some form fields (POLICYNUM, PRIMARY, EXCESS, LIMIT, TELNUM in MCS90A) have `F,` lines that place the field at col ~1311–1499 — the **far left margin**. These are phantom data-capture positions for the Documaker engine, not the visual position. The true visual position is inline within the M,TT text flow, marked by:
1. An `M,TT` token containing a single `"X"` character at the visual insertion point
2. Immediately followed by `A,T1,"FIELDNAME",...`

**Visual symptom:** Checkbox/text fields floating in the left margin, literal "X" characters appearing mid-sentence.

**Fix:** Parse `A,T1` lines during FAP parsing. When a named `A,T1,"FIELDNAME"` follows an M,TT token:
- Record the token's position in `FapParseResult.InlineFieldPositions[fieldName]`
- Flag the token with `IsFieldPlaceholder = true` so it is **not** rendered as static text
- In `RenderFields`, prefer `InlineFieldPositions` over the F, line's position when available

```
M,TT,(11296,3300,11616,6852),(10110,...),25,Amending Policy Number:
M,TT,(11296,6852,11616,7036),(10010,...),1,X          ← placeholder token
A,T1,"POLICYNUM ",0,(0,0,0,0),1,0," ",0              ← A,T1 names it
```

**Result:** POLICYNUM field renders inline after "Amending Policy Number:" label.

---

### 4. A,T1 Space-Only Entries — Ignore Them

Not all `A,T1` lines name a field. Lines like `A,T1," ",...` (space in quotes) are line-break hints for the Documaker flow engine. `ParseAT1FieldName` should return `null` for whitespace-only names.

---

## Code Architecture Notes

### Where parsing happens: `FormFileClient.cs`

- `ParseFapFileAsync()` — full FAP parser
  - Increments `currentPage` on each `H,` line
  - Calls `FlushTextArea()` on `M,H,` / `M,E`
  - Collects `FapTextToken` objects on `M,TT,` lines
  - **NEW:** Processes `A,T1,` lines to populate `inlineFieldPositions` and set `IsFieldPlaceholder` on the preceding token

- `ParseAT1FieldName(line)` — extracts field name from `A,T1,"FIELDNAME",...`

### Key model additions: `FormFileClient.cs`

```csharp
// FapTextToken now has:
bool IsFieldPlaceholder = false   // true = "X" placeholder for an A,T1 anchor; do not render

// FapParseResult now has:
IReadOnlyDictionary<string, (int Row1, int Col1, int Row2, int Col2)> InlineFieldPositions
    // Key = field name (OrdinalIgnoreCase). Value = position of the "X" M,TT token.
```

### Where rendering happens: `FapToPdfGenerator.cs`

- `RenderTextAreas(canvas, areas, pi, fonts)` — clips each area, skips `IsFieldPlaceholder` tokens
- `RenderFields(doc, page, fields, ddtLookup, pi, usedNames, fonts, inlinePositions)` — uses inline position when available; estimates width from `field.Length × height × 0.55` for inline fields

### Field width estimation for inline fields

When a field uses an inline (A,T1) position, `Col2 - Col1` of the M,TT placeholder token is typically only 1–2 characters wide (just the "X"). Width is instead estimated as:

```csharp
w = field.Length * h * 0.55f;  // proportional font: ~0.55× line height per char
```

For checkbox/toggle fields (length = 1) use the normal FAP rect from the F, line.

---

## FAP Line Types Reference (Additions to CLAUDE.md)

### `A,T1,` — Inline Field Anchor / Line-Break Hint

```
A,T1,"FIELDNAME",0,(0,0,0,0),1,0," ",0
A,T1," ",0,(0,0,0,0),1040,0," ",0
```

- If the quoted name is non-blank: marks the visual position of a field, immediately after the preceding M,TT token
- If the quoted name is blank/space: line-break hint for the flow engine — ignore
- The FAP parser must look **back** at the last token in `currentTokens` to get the inline position

### `M,O,` — Token End Marker

```
M,O,(row,endCol,row,endCol),(fontId,...),1,0,0,0,0
```

A positional marker after a sequence of tokens. The parser currently ignores it. Do not confuse with `M,TT,`.

### `M,P,` — Paragraph Break

```
M,P,0,33,0,0,0,0,0,0
```

Paragraph break within a text area. No visual effect in the PDF renderer — the token positions are already absolute.

### `A,F1,` / `A,F6,` — Field Format/Style Attributes

```
A,F1," ",x ," ",0,1,0,(255,0,0,0),0,0,
A,F6," ",0,1,1,0,0," ",0,0,600,0,0,0,0,0,0,0,0,0,0," ",0
```

Appear after `F,` field lines. Carry Documaker-specific render/validation attributes. The FAP parser currently ignores them and they do not affect PDF generation.

---

## Multi-Page FAP Forms

MCS90A is 3 pages. Each page starts with an `H,` line:
```
H,2400,(0,0),(1200,400,26400,20400),MCS90A
H,2400,(0,0),(1200,400,26400,20400),MCS90A
H,2400,(0,0),(1200,400,26400,20400),MCS90A
```

- `FormFileClient` increments `currentPage` on each H-line (0-indexed)
- Every parsed element gets `PageIndex = currentPage`
- `FapToPdfGenerator` creates `PageCount` pages up front and filters elements by `PageIndex`
- `FapParseResult.PageInfos` holds per-page dimensions; index = page number

**Common gotcha:** If `PageCount` is wrong (e.g. only 1 when it should be 3), all elements pile onto page 0 creating an extremely dense single page.

---

## Known Remaining Limitations

| Issue | Cause | Status |
|---|---|---|
| Slight token boundary overlap in bold→regular transitions | Spire.PDF font metrics differ from Documaker's internal metrics | Acceptable; would require Documaker font data to fix |
| Right-edge token clipping | Column clip rect ends at M,H Col2; tokens whose rendered width exceeds their FAP width get clipped | Acceptable; form is readable |
| G, image references not rendered | `G,(row1,col1,row2,col2),imageName` — image embedded in Documaker binary; not available as a file | Not fixable without image extraction tool |
| Checkbox fields (PRIMARY_CHK, EXCESS_CHK) appear at FAP F, position | These have no A,T1 anchor — their F, position IS the visual position | Working correctly |

---

## Debugging Tips

1. **Print M,H token counts per block** to identify which blocks are parents vs columns:
   ```powershell
   $inBlock=$false; $count=0; foreach ($line in (Get-Content "X.FAP")) {
     if ($line -match "^M,H,") { if ($inBlock) { "$blockName -> $count" }; $inBlock=$true; $blockName=$line.Substring(0,40); $count=0 }
     elseif ($line -match "^M,TT,") { $count++ }
     elseif ($line -match "^M,E") { if ($inBlock) { "$blockName -> $count" }; $inBlock=$false; $count=0 }
   }
   ```

2. **Find A,T1 field anchors with their context:**
   ```powershell
   Get-Content "X.FAP" | Select-String "A,T1" | Where-Object { $_ -notmatch 'A,T1," "' }
   ```

3. **Check all F, fields and compare to A,T1 names** to find fields with phantom F, positions:
   ```powershell
   Get-Content "X.FAP" | Select-String "^F,"
   Get-Content "X.FAP" | Select-String '^A,T1,"[^ ]'
   ```
   If a field appears in both lists, its F, position is likely phantom — the A,T1 position is correct.
