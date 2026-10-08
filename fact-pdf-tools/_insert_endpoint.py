with open(r"C:\src\fact-pdf-tools\server\Controllers\DefinitionsController.cs", "rb") as f:
    content = f.read().decode("utf-8")

marker = "\t/// <summary>\n\t/// Seed a new authored form definition from a legacy FAP form."

new_block = (
    "\t/// <summary>\n"
    "\t/// Render one page of the definition as a PNG image for canvas background display.\n"
    "\t/// </summary>\n"
    "\t[HttpGet(\"{id}/page-image\")]\n"
    "\tpublic IActionResult GetPageImage(string id, [FromQuery] int page = 1, [FromQuery] int dpi = 144)\n"
    "\t{\n"
    "\t\tFormDefinition? def = _store.Get(id);\n"
    "\t\tif (def == null) return NotFound(new { error = $\"Definition '{id}' not found.\" });\n"
    "\n"
    "\t\t(byte[] pdfBytes, _) = _pdfGenerator.GeneratePdfBytes(def);\n"
    "\n"
    "\t\tusing var pdfDoc = new Spire.Pdf.PdfDocument();\n"
    "\t\tpdfDoc.LoadFromBytes(pdfBytes);\n"
    "\n"
    "\t\tint pageIndex = Math.Clamp(page - 1, 0, pdfDoc.Pages.Count - 1);\n"
    "\t\tusing System.Drawing.Image img = pdfDoc.SaveAsImage(pageIndex, dpi, dpi);\n"
    "\t\tusing var ms = new System.IO.MemoryStream();\n"
    "\t\timg.Save(ms, System.Drawing.Imaging.ImageFormat.Png);\n"
    "\t\tbyte[] pngBytes = ms.ToArray();\n"
    "\t\tResponse.Headers.Append(\"Cache-Control\", \"public, max-age=300\");\n"
    "\t\treturn File(pngBytes, \"image/png\");\n"
    "\t}\n"
    "\n"
    "\t/// <summary>\n"
    "\t/// Seed a new authored form definition from a legacy FAP form."
)

fixed = content.replace(marker, new_block, 1)
if fixed == content:
    print("NO MATCH - marker not found")
else:
    with open(r"C:\src\fact-pdf-tools\server\Controllers\DefinitionsController.cs", "wb") as f:
        f.write(fixed.encode("utf-8"))
    print("Saved OK.")
    for i, line in enumerate(fixed.splitlines(), 1):
        if "GetPageImage" in line or "page-image" in line:
            print(f"  L{i}: {line.rstrip()}")
