using Microsoft.AspNetCore.Mvc;
using FapPdfTools.Population;
using FapPdfTools.Server.Infrastructure;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Controllers;

/// <summary>
/// Pre-built PDF template library endpoints. The <c>build</c> action is the OFFLINE step
/// (it parses FAP/DDT once and stores a fillable PDF). Every other action is REQUEST-TIME
/// and does NO FAP parsing — it loads the stored template and fills its AcroForm fields.
/// </summary>
[ApiController]
[Route("api/templates")]
public class TemplatesController : ControllerBase
{
	private readonly FormFileClient _formClient;
	private readonly FapToPdfGenerator _pdfGenerator;
	private readonly TemplateStore _templateStore;
	private readonly FormFieldMapRegistry _mapRegistry;

	public TemplatesController(
		FormFileClient formClient,
		FapToPdfGenerator pdfGenerator,
		TemplateStore templateStore,
		FormFieldMapRegistry mapRegistry)
	{
		_formClient = formClient;
		_pdfGenerator = pdfGenerator;
		_templateStore = templateStore;
		_mapRegistry = mapRegistry;
	}

	/// <summary>
	/// OFFLINE build: convert a legacy FAP form to a fillable PDF and store it as a template.
	/// Run once per form/edition to seed the library. (This is the only template endpoint that
	/// parses FAP.)
	/// </summary>
	[HttpPost("build")]
	public async Task<IActionResult> Build([FromBody] BuildTemplateRequest request, CancellationToken ct)
	{
		var parsed = await ResolveAndParseAsync(request.FormNumber, request.EditionDate, ct);
		if (parsed == null)
			return NotFound(new { error = $"Form '{request.FormNumber}' edition '{request.EditionDate}' not found in FORM.DAT (and no matching FAP file)." });

		var (fileName, fap, ddt, pageInfo) = parsed.Value;

		// Apply any saved per-field overrides (font size / max length / bold) at build time.
		var overrides = _templateStore.GetOverrides(request.FormNumber, request.EditionDate);
		var (pdfBytes, fieldNames) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo, overrides);
		_templateStore.Save(request.FormNumber, request.EditionDate, pdfBytes);

		return Ok(new
		{
			request.FormNumber,
			request.EditionDate,
			sourceFile = fileName,
			fieldCount = fieldNames.Count,
			overridesApplied = overrides?.Count ?? 0,
			fieldNames,
		});
	}

	/// <summary>
	/// Effective per-field metadata (FAP/FXR defaults merged with any saved overrides) — a
	/// ready-to-edit starting point for tuning font size / max length / bold. Authoring endpoint;
	/// parses FAP (not a hot path).
	/// </summary>
	[HttpGet("{formNumber}/{editionDate}/overrides")]
	public async Task<IActionResult> GetOverrides(string formNumber, string editionDate, CancellationToken ct)
	{
		var parsed = await ResolveAndParseAsync(formNumber, editionDate, ct);
		if (parsed == null)
			return NotFound(new { error = $"Form '{formNumber}' edition '{editionDate}' not found." });

		var (_, fap, ddt, pageInfo) = parsed.Value;
		var descriptors = _pdfGenerator.GetFieldDescriptors(fap, ddt, pageInfo);
		var saved = _templateStore.GetOverrides(formNumber, editionDate);

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var fields = new List<TemplateFieldMeta>();
		foreach (var d in descriptors)
		{
			if (!seen.Add(d.Name)) continue;
			FieldOverride? ov = null;
			saved?.TryGetValue(d.Name, out ov);
			bool overridden = ov != null && (ov.PointSize.HasValue || ov.MaxLength.HasValue
				|| ov.Bold.HasValue || !string.IsNullOrWhiteSpace(ov.Format));
			fields.Add(new TemplateFieldMeta(
				d.Name,
				d.FontId,
				ov?.PointSize ?? d.PointSize,
				ov?.MaxLength ?? d.MaxLength,
				ov?.Bold ?? d.Bold,
				overridden,
				ov?.Format,
				ov?.Decimals,
				ov?.Prefix));
		}

		return Ok(new TemplateOverridesResponse(formNumber, editionDate, fields));
	}

	/// <summary>
	/// Save per-field overrides and rebuild the template applying them. Body is a map of
	/// field name → { pointSize?, maxLength?, bold? }.
	/// </summary>
	[HttpPut("{formNumber}/{editionDate}/overrides")]
	public async Task<IActionResult> PutOverrides(
		string formNumber, string editionDate,
		[FromBody] Dictionary<string, FieldOverride> overrides,
		CancellationToken ct)
	{
		if (overrides == null)
			return BadRequest(new { error = "Body must be a map of fieldName → { pointSize?, maxLength?, bold? }." });

		_templateStore.SaveOverrides(formNumber, editionDate, overrides);

		var parsed = await ResolveAndParseAsync(formNumber, editionDate, ct);
		if (parsed == null)
			return Ok(new { saved = overrides.Count, rebuilt = false, note = "Overrides saved, but form not found to rebuild a template." });

		var (_, fap, ddt, pageInfo) = parsed.Value;
		var (pdfBytes, fieldNames) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo, overrides);
		_templateStore.Save(formNumber, editionDate, pdfBytes);

		return Ok(new { saved = overrides.Count, rebuilt = true, fieldCount = fieldNames.Count });
	}

	// Resolve a form to its FAP file and parse it (FAP/DDT/H-line). Returns null if not found.
	private async Task<(string FileName, FapParseResult Fap, DdtParseResult? Ddt, FapPageInfo? PageInfo)?>
		ResolveAndParseAsync(string formNumber, string editionDate, CancellationToken ct)
	{
		var entries = await _formClient.ResolveFormFileNameAsync(formNumber, editionDate, ct);
		string fileName = entries.Count > 0 ? entries[0].FileName : formNumber.Trim();
		if (entries.Count == 0 && _formClient.FindFormFilePath(fileName, ".FAP") == null)
			return null;

		var fap = await _formClient.ParseFapFileAsync(fileName, ct);
		if (fap == null) return null;

		DdtParseResult? ddt = await _formClient.ParseDdtFileAsync(fileName, ct);
		var fapPath = _formClient.FindFormFilePath(fileName, ".FAP");
		FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;
		return (fileName, fap, ddt, pageInfo);
	}

	/// <summary>
	/// REQUEST-TIME: return the fillable fields of a pre-built template so a UI can render an
	/// input per field. No FAP parsing.
	/// </summary>
	[HttpGet("{formNumber}/{editionDate}/fields")]
	public IActionResult GetFields(string formNumber, string editionDate)
	{
		byte[]? template = _templateStore.Get(formNumber, editionDate);
		if (template == null)
			return NotFound(BuildMissingTemplatePayload(formNumber, editionDate));

		var fields = _pdfGenerator.ReadTemplateFields(template);
		return Ok(new TemplateFieldsResponse(formNumber, editionDate, fields.Count, fields));
	}

	/// <summary>
	/// REQUEST-TIME: fill a pre-built template with caller-supplied field values (e.g. user
	/// entered data). No FAP parsing.
	/// </summary>
	[HttpPost("fill")]
	public IActionResult Fill([FromBody] FillFieldsRequest request)
	{
		byte[]? template = _templateStore.Get(request.FormNumber, request.EditionDate);
		if (template == null)
			return NotFound(BuildMissingTemplatePayload(request.FormNumber, request.EditionDate));

		var values = FormatValues(request.FormNumber, request.EditionDate, request.FieldValues);
		byte[] pdf = values is { Count: > 0 }
			? _pdfGenerator.FillFields(template, values, request.Flatten)
			: template;

		return File(pdf, "application/pdf", $"{request.FormNumber}_{request.EditionDate}_filled.pdf");
	}

	/// <summary>
	/// REQUEST-TIME: populate a pre-built template from a Common Data Model policy view supplied
	/// in the request body. This is the endpoint DocGen calls in place of GhostDraft. Requires a
	/// registered <see cref="IFormFieldMap"/> for the form+edition. No FAP parsing.
	/// </summary>
	[HttpPost("populate-from-model")]
	public IActionResult PopulateFromModel([FromBody] PopulateFromModelRequest request)
	{
		IFormFieldMap? map = _mapRegistry.Resolve(request.FormNumber, request.EditionDate);
		if (map == null)
		{
			return UnprocessableEntity(new
			{
				error = $"No field map registered for form '{request.FormNumber}' edition '{request.EditionDate}'.",
				knownForms = _mapRegistry.KnownForms,
			});
		}

		if (request.Policy == null)
			return BadRequest(new { error = "A policy (Policy) is required in the request body." });

		byte[]? template = _templateStore.Get(request.FormNumber, request.EditionDate);
		if (template == null)
			return NotFound(BuildMissingTemplatePayload(request.FormNumber, request.EditionDate));

		IReadOnlyDictionary<string, string> values = FormatValues(
			request.FormNumber, request.EditionDate, map.BuildValues(request.Policy));
		// Final policy documents must always be locked, so this path always flattens regardless of
		// the request flag. (The user-entry /fill path stays flag-controlled for editable previews.)
		byte[] pdf = _pdfGenerator.FillFields(template, values, flatten: true);

		return File(pdf, "application/pdf", $"{request.FormNumber}_{request.EditionDate}_populated.pdf");
	}

	/// <summary>
	/// REQUEST-TIME: return the template's fillable fields pre-filled with values the field map
	/// derives from the supplied policy — for an editable "interactive" form (pre-fill, then the
	/// user edits, then render via /fill). No flatten, no FAP parsing.
	/// </summary>
	[HttpPost("values-from-model")]
	public IActionResult ValuesFromModel([FromBody] PopulateFromModelRequest request)
	{
		IFormFieldMap? map = _mapRegistry.Resolve(request.FormNumber, request.EditionDate);
		if (map == null)
		{
			return UnprocessableEntity(new
			{
				error = $"No field map registered for form '{request.FormNumber}' edition '{request.EditionDate}'.",
				knownForms = _mapRegistry.KnownForms,
			});
		}

		if (request.Policy == null)
			return BadRequest(new { error = "A policy (Policy) is required in the request body." });

		byte[]? template = _templateStore.Get(request.FormNumber, request.EditionDate);
		if (template == null)
			return NotFound(BuildMissingTemplatePayload(request.FormNumber, request.EditionDate));

		var fields = _pdfGenerator.ReadTemplateFields(template);
		IReadOnlyDictionary<string, string> values = map.BuildValues(request.Policy);
		var overrides = _templateStore.GetOverrides(request.FormNumber, request.EditionDate);

		// A field the map owns (has a value for) is policy-derived → pre-filled and locked.
		// Everything else is left editable for the user (the WIP hybrid model). Policy-mapped
		// values are formatted per the field's type so the prefill matches the rendered output.
		var merged = fields
			.Select(f =>
			{
				bool locked = values.TryGetValue(f.Name, out var v);
				FieldOverride ov = null;
				overrides?.TryGetValue(f.Name, out ov);
				string val = locked ? FieldValueFormatter.Apply(v, ov) : string.Empty;
				return new TemplateFieldValue(f.Name, f.Type, f.MaxLength, val, locked, ov?.Format);
			})
			.ToList();

		return Ok(new TemplatePrefillResponse(request.FormNumber, request.EditionDate, merged.Count, merged));
	}

	// Apply each field's value-format override (money / date / percent) to the supplied values.
	private IReadOnlyDictionary<string, string> FormatValues(
		string formNumber, string editionDate, IReadOnlyDictionary<string, string> values)
	{
		if (values == null || values.Count == 0) return values;
		var overrides = _templateStore.GetOverrides(formNumber, editionDate);
		if (overrides == null || overrides.Count == 0) return values;

		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var kv in values)
			result[kv.Key] = overrides.TryGetValue(kv.Key, out var ov)
				? FieldValueFormatter.Apply(kv.Value, ov)
				: kv.Value;
		return result;
	}

	private object BuildMissingTemplatePayload(string formNumber, string editionDate) => new
	{
		error = $"No pre-built template for form '{formNumber}' edition '{editionDate}'. Build it first via POST /api/templates/build.",
		knownTemplates = _templateStore.KnownTemplates,
	};
}

/// <summary>
/// Request to populate a template from a policy supplied in the body. The policy is the same
/// <see cref="MoE.CommonDataModel.Policy"/> shape DocGen builds in-process.
/// </summary>
public record PopulateFromModelRequest(
	string FormNumber,
	string EditionDate,
	MoE.CommonDataModel.Policy Policy,
	bool Flatten = false);
