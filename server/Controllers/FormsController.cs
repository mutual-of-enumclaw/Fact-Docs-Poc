using Microsoft.AspNetCore.Mvc;
using FapPdfTools.Server.Infrastructure;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FormsController : ControllerBase
{
    private readonly FormFileClient _formClient;
    private readonly FormDefinitionStore _definitionStore;
    private readonly FapToPdfGenerator _pdfGenerator;

    public FormsController(FormFileClient formClient, FormDefinitionStore definitionStore, FapToPdfGenerator pdfGenerator)
    {
        _formClient = formClient;
        _definitionStore = definitionStore;
        _pdfGenerator = pdfGenerator;
    }

    /// <summary>List all forms in the catalog, optionally filtered by search term.</summary>
    [HttpGet]
    public async Task<IActionResult> ListForms([FromQuery] string? search, CancellationToken ct)
    {
        var legacyEntries = await _formClient.ListAllFormsAsync(search, ct);
        var authoredDefinitions = _definitionStore.ListAll()
            .Where(def => MatchesSearch(def, search))
            .Select(def => new FormCatalogEntry(
                !string.IsNullOrWhiteSpace(def.SourceFormNumber)
                    ? string.IsNullOrWhiteSpace(def.SourceEditionDate)
                        ? def.SourceFormNumber!
                        : $"{def.SourceFormNumber} {def.SourceEditionDate}"
                    : def.Name,
                def.Id,
                "JSON",
                def.Fields.Count + def.StaticTexts.Count + def.Lines.Count,
                string.IsNullOrWhiteSpace(def.Description) ? def.Name : def.Description,
                def.SourceFormNumber,
                def.SourceEditionDate,
                def.Description));

        // Classify each legacy entry in parallel (FAP F-line + DDT powtype scan)
        var classifyTasks = legacyEntries
            .Select(e => _formClient.ClassifyFormAsync(e.FileName, ct))
            .ToList();
        var classifications = await Task.WhenAll(classifyTasks);

        var result = legacyEntries
            .Select((e, i) => new FormCatalogEntry(
                e.FormKey,
                e.FileName,
                e.Metadata,
                e.Sections.Count,
                Classification: classifications[i]))
            .Concat(authoredDefinitions)
            .ToList();
        return Ok(result);
    }

    private static bool MatchesSearch(FormDefinition def, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;

        var normSearch = NormalizeSearch(search);
        if (string.IsNullOrWhiteSpace(normSearch)) return true;

        var haystack = string.Join(' ', new string[]
        {
            def.Id,
            def.Name,
            def.Description,
            def.SourceFormNumber,
            def.SourceEditionDate,
        }.Where(value => !string.IsNullOrWhiteSpace(value))!);

        return NormalizeSearch(haystack).Contains(normSearch, StringComparison.Ordinal);
    }

    private static string NormalizeSearch(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>Get detailed info about a specific form (field names, counts, sections).</summary>
    [HttpGet("{formNumber}/{editionDate}/info")]
    public async Task<IActionResult> GetFormInfo(string formNumber, string editionDate, CancellationToken ct)
    {
        var entries = await _formClient.ResolveFormFileNameAsync(formNumber, editionDate, ct);
        if (entries.Count == 0) return NotFound(new { error = $"Form {formNumber} edition {editionDate} not found" });

        var entry = entries[0];
        var fap = await _formClient.ParseFapFileAsync(entry.FileName, ct);
        if (fap == null) return NotFound(new { error = $"FAP file {entry.FileName} not found" });

        var sections = entry.Sections.Select(s => new FormSectionInfo(s.FileName, s.SectionType, s.SectionMetadata)).ToList();
        var classification = await _formClient.ClassifyFormAsync(entry.FileName, ct);

        return Ok(new FormInfoResponse(
            entry.FormKey, entry.FileName,
            fap.Fields.Count, fap.StaticTexts.Count, fap.Lines.Count, fap.TextAreas.Count,
            fap.PageCount, fap.Fields.Select(f => f.Name).ToList(), sections, classification));
    }

    /// <summary>Get the fillable fields for a form with PDF-point bounds, FXR font, and DDT rule.</summary>
    [HttpGet("{formNumber}/{editionDate}/fields")]
    public async Task<IActionResult> GetFormFields(string formNumber, string editionDate, CancellationToken ct)
    {
        var entries = await _formClient.ResolveFormFileNameAsync(formNumber, editionDate, ct);
        if (entries.Count == 0) return NotFound(new { error = $"Form {formNumber} edition {editionDate} not found" });

        var entry = entries[0];
        var fap = await _formClient.ParseFapFileAsync(entry.FileName, ct);
        if (fap == null) return NotFound(new { error = $"FAP file {entry.FileName} not found" });

        var ddt = await _formClient.ParseDdtFileAsync(entry.FileName, ct);
        var fapPath = _formClient.FindFormFilePath(entry.FileName, ".FAP");
        FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;

        var descriptors = _pdfGenerator.GetFieldDescriptors(fap, ddt, pageInfo);
        return Ok(new FormFieldsResponse(entry.FormKey, entry.FileName, fap.PageCount, descriptors));
    }
}
