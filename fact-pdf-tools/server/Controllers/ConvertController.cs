using Microsoft.AspNetCore.Mvc;
using FapPdfTools.Server.Infrastructure;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConvertController : ControllerBase
{
    private readonly FormFileClient _formClient;
    private readonly FapToPdfGenerator _pdfGenerator;
    private readonly FxrFontLibrary _fonts;

    public ConvertController(FormFileClient formClient, FapToPdfGenerator pdfGenerator, FxrFontLibrary fonts)
    {
        _formClient = formClient;
        _pdfGenerator = pdfGenerator;
        _fonts = fonts;
    }

    /// <summary>Convert a FAP form to PDF and return the file.</summary>
    [HttpPost]
    public async Task<IActionResult> Convert([FromBody] ConvertRequest request, CancellationToken ct)
    {
        var entries = await _formClient.ResolveFormFileNameAsync(request.FormNumber, request.EditionDate, ct);

        string fileName;
        if (entries.Count > 0)
        {
            fileName = entries[0].FileName;
        }
        else
        {
            // Fall back: treat FormNumber as a direct FAP file name (e.g. "QTE_BILLINFO")
            fileName = request.FormNumber.Trim();
            var directPath = _formClient.FindFormFilePath(fileName, ".FAP");
            if (directPath == null)
                return NotFound(new { error = $"Form {request.FormNumber} edition {request.EditionDate} not found, and no FAP file named {fileName} exists." });
        }

        var fap = await _formClient.ParseFapFileAsync(fileName, ct);
        if (fap == null)
            return NotFound(new { error = $"FAP file {fileName} not found" });

        DdtParseResult? ddt = await _formClient.ParseDdtFileAsync(fileName, ct);

        // Parse H-line for page dimensions
        var fapPath = _formClient.FindFormFilePath(fileName, ".FAP");
        FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;

        var (pdfBytes, fieldNames) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo);

        Response.Headers.Append("X-Field-Count", fieldNames.Count.ToString());
        Response.Headers.Append("X-Field-Names", string.Join(",", fieldNames.Take(50)));
        return File(pdfBytes, "application/pdf", $"{fileName}.pdf");
    }

    /// <summary>Convert and fill fields in one step.</summary>
    [HttpPost("fill")]
    public async Task<IActionResult> ConvertAndFill([FromBody] FillFieldsRequest request, CancellationToken ct)
    {
        var entries = await _formClient.ResolveFormFileNameAsync(request.FormNumber, request.EditionDate, ct);
        if (entries.Count == 0)
            return NotFound(new { error = $"Form {request.FormNumber} edition {request.EditionDate} not found" });

        var entry = entries[0];
        var fap = await _formClient.ParseFapFileAsync(entry.FileName, ct);
        if (fap == null)
            return NotFound(new { error = $"FAP file {entry.FileName} not found" });

        DdtParseResult? ddt = await _formClient.ParseDdtFileAsync(entry.FileName, ct);
        var fapPath = _formClient.FindFormFilePath(entry.FileName, ".FAP");
        FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;

        var (pdfBytes, fieldNames) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo);

        if (request.FieldValues.Count > 0)
            pdfBytes = _pdfGenerator.FillFields(pdfBytes, request.FieldValues, request.Flatten);

        Response.Headers.Append("X-Field-Count", fieldNames.Count.ToString());
        var suffix = request.Flatten ? "_filled_flat" : "_filled";
        return File(pdfBytes, "application/pdf", $"{entry.FileName}{suffix}.pdf");
    }

    /// <summary>Export a FAP form as a GhostDraft .gd document file.</summary>
    [HttpPost("export-gd")]
    public async Task<IActionResult> ExportGhostDraft([FromBody] ConvertRequest request, CancellationToken ct)
    {
        var entries = await _formClient.ResolveFormFileNameAsync(request.FormNumber, request.EditionDate, ct);

        string fileName;
        if (entries.Count > 0)
        {
            fileName = entries[0].FileName;
        }
        else
        {
            fileName = request.FormNumber.Trim();
            if (_formClient.FindFormFilePath(fileName, ".FAP") == null)
                return NotFound(new { error = $"Form {request.FormNumber} edition {request.EditionDate} not found." });
        }

        var fap = await _formClient.ParseFapFileAsync(fileName, ct);
        if (fap == null)
            return NotFound(new { error = $"FAP file {fileName} not found" });

        var formTitle = string.IsNullOrWhiteSpace(request.EditionDate)
            ? request.FormNumber
            : $"{request.FormNumber} {request.EditionDate}";

        // Detect WIP forms (powtype DDT fields) and use shape-based RTF layout
        var classification = await _formClient.ClassifyFormAsync(fileName, ct);
        bool isWip = string.Equals(classification, "WIP", StringComparison.OrdinalIgnoreCase);

        byte[]? backgroundEmf = null;
        if (isWip)
        {
            // Render the FAP to a PDF page image, then convert to EMF for GhostDraft.
            // GhostDraft WIP format requires \emfblip — it ignores \pngblip.
            var fapPath = _formClient.FindFormFilePath(fileName, ".FAP");
            FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;
            var ddt = await _formClient.ParseDdtFileAsync(fileName, ct);
            var (pdfBytes, _) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo);

            using var pdfDoc = new Spire.Pdf.PdfDocument();
            pdfDoc.LoadFromBytes(pdfBytes);
            using System.Drawing.Image img = pdfDoc.SaveAsImage(0, 150, 150);
            using var bitmap = new System.Drawing.Bitmap(img);

            // Wrap bitmap in an EMF metafile so GhostDraft can embed it.
            // Use a 1x1 off-screen bitmap for the reference HDC — Graphics.FromHwnd(IntPtr.Zero)
            // is unreliable in server contexts and produces a corrupt metafile.
            var emfStream = new System.IO.MemoryStream();
            using var refBmp = new System.Drawing.Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var refGfx = System.Drawing.Graphics.FromImage(refBmp);
            var hdc = refGfx.GetHdc();
            try
            {
                using var metafile = new System.Drawing.Imaging.Metafile(
                    emfStream, hdc,
                    new System.Drawing.RectangleF(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.MetafileFrameUnit.Pixel,
                    System.Drawing.Imaging.EmfType.EmfOnly);
                using var g = System.Drawing.Graphics.FromImage(metafile);
                g.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
            }
            finally { refGfx.ReleaseHdc(hdc); }
            backgroundEmf = emfStream.ToArray();
        }

        var gdXml = FapToGhostDraftGenerator.Generate(fap, formTitle, fileName, isWip, backgroundEmf, _fonts);
        var bytes = System.Text.Encoding.UTF8.GetBytes(gdXml);
        return File(bytes, "application/octet-stream", $"{fileName}.gd");
    }
}
