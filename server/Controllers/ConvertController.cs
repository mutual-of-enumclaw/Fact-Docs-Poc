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

    public ConvertController(FormFileClient formClient, FapToPdfGenerator pdfGenerator)
    {
        _formClient = formClient;
        _pdfGenerator = pdfGenerator;
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
}
