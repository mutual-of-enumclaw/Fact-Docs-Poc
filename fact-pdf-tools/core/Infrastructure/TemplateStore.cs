using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Infrastructure;

/// <summary>
/// Resolves pre-built fillable PDF templates by (formNumber, editionDate). Templates are
/// produced offline (FAP -> fillable PDF, see the /api/templates/build endpoint) and stored
/// as individual .pdf files under <see cref="FormFileOptions.PdfTemplateDirectory"/>.
/// Request-time endpoints load a template and fill its AcroForm fields without any FAP parsing.
/// Keys are normalized the same way as <c>FormFieldMapRegistry.MakeKey</c> so a form resolves
/// to the same map and the same template.
/// </summary>
public class TemplateStore
{
	private readonly string _directory;
	private readonly ILogger<TemplateStore> _logger;

	public TemplateStore(IOptions<FormFileOptions> opts, ILogger<TemplateStore> logger)
	{
		_directory = string.IsNullOrWhiteSpace(opts.Value.PdfTemplateDirectory)
			? Path.Combine(AppContext.BaseDirectory, "pdf-templates")
			: opts.Value.PdfTemplateDirectory;
		_logger = logger;
		Directory.CreateDirectory(_directory);
	}

	/// <summary>The directory templates are read from / written to.</summary>
	public string Directory_ => _directory;

	/// <summary>Returns the template PDF bytes for the form, or <see langword="null"/> if not built.</summary>
	public byte[]? Get(string formNumber, string editionDate)
	{
		string path = GetPath(formNumber, editionDate);
		return File.Exists(path) ? File.ReadAllBytes(path) : null;
	}

	/// <summary>Whether a template has been built for the form.</summary>
	public bool Exists(string formNumber, string editionDate) => File.Exists(GetPath(formNumber, editionDate));

	/// <summary>Persist a pre-built fillable PDF template for the form.</summary>
	public void Save(string formNumber, string editionDate, byte[] pdfBytes)
	{
		string path = GetPath(formNumber, editionDate);
		File.WriteAllBytes(path, pdfBytes);
		_logger.LogInformation("Saved PDF template {Form} {Edition} ({Bytes} bytes) -> {Path}",
			formNumber, editionDate, pdfBytes.Length, path);
	}

	/// <summary>All stored template file base names (form_edition), for diagnostics.</summary>
	public IReadOnlyList<string> KnownTemplates =>
		System.IO.Directory.GetFiles(_directory, "*.pdf")
			.Select(Path.GetFileNameWithoutExtension)
			.Where(n => !string.IsNullOrEmpty(n))
			.Select(n => n!)
			.OrderBy(n => n)
			.ToList();

	// ---- Per-field overrides (font size / max length / bold), stored beside the template ----

	private static readonly JsonSerializerOptions OverrideJson = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	/// <summary>Load saved per-field overrides for a form, or <see langword="null"/> if none.</summary>
	public Dictionary<string, FieldOverride>? GetOverrides(string formNumber, string editionDate)
	{
		string path = OverridesPath(formNumber, editionDate);
		if (!File.Exists(path)) return null;
		try
		{
			return JsonSerializer.Deserialize<Dictionary<string, FieldOverride>>(File.ReadAllText(path), OverrideJson);
		}
		catch (Exception ex)
		{
			_logger.LogWarning(ex, "Could not load field overrides for {Form} {Edition}", formNumber, editionDate);
			return null;
		}
	}

	/// <summary>Persist per-field overrides for a form.</summary>
	public void SaveOverrides(string formNumber, string editionDate, Dictionary<string, FieldOverride> overrides)
	{
		File.WriteAllText(OverridesPath(formNumber, editionDate), JsonSerializer.Serialize(overrides, OverrideJson));
		_logger.LogInformation("Saved {Count} field overrides for {Form} {Edition}", overrides.Count, formNumber, editionDate);
	}

	private string OverridesPath(string formNumber, string editionDate)
		=> Path.Combine(_directory, $"{Normalize(formNumber)}_{Normalize(editionDate)}.overrides.json");

	private string GetPath(string formNumber, string editionDate)
		=> Path.Combine(_directory, $"{Normalize(formNumber)}_{Normalize(editionDate)}.pdf");

	// Filesystem-safe key: trimmed, upper-cased, spaces removed. Callers pass the same
	// (formNumber, editionDate) they pass to FormFieldMapRegistry, so the two stay aligned.
	private static string Normalize(string s) => (s ?? string.Empty).Trim().ToUpperInvariant().Replace(" ", "");
}
