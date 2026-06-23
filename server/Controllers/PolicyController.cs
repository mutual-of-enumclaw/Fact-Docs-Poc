using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using FapPdfTools.Population;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Infrastructure;

namespace FapPdfTools.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PolicyController : ControllerBase
{
	private readonly CommercialApiOptions _apiOptions;
	private readonly FormFileClient _formClient;
	private readonly FapToPdfGenerator _pdfGenerator;
	private readonly FormFieldMapRegistry _mapRegistry;

	public PolicyController(
		IOptions<CommercialApiOptions> apiOptions,
		FormFileClient formClient,
		FapToPdfGenerator pdfGenerator,
		FormFieldMapRegistry mapRegistry)
	{
		_apiOptions = apiOptions.Value;
		_formClient = formClient;
		_pdfGenerator = pdfGenerator;
		_mapRegistry = mapRegistry;
	}

	/// <summary>
	/// Fetch a policy from the Commercial API and return its CDM JSON.
	/// Useful for the client to show policy details and inspect available data.
	/// </summary>
	[HttpGet("{policyNumber}")]
	public async Task<IActionResult> GetPolicy(
		string policyNumber,
		[FromQuery] string? env,
		CancellationToken ct)
	{
		string? baseUrl = _apiOptions.ResolveBaseUrl(env);
		if (baseUrl == null)
			return StatusCode(503, new { error = "No Commercial API URL configured for this environment." });

		using CommercialApiPolicyClient client = new(baseUrl);
		try
		{
			MoE.GhostDraftDataModel.SDK.CDMPolicyView policy = await client.GetPolicyAsync(policyNumber, ct);
			return Ok(policy);
		}
		catch (HttpRequestException ex)
		{
			return StatusCode(502, new { error = $"Commercial API error: {ex.Message}" });
		}
	}

	/// <summary>
	/// Populate a form from a live Commercial API policy. Returns a filled PDF.
	/// Requires a registered <see cref="IFormFieldMap"/> for the form+edition.
	/// </summary>
	[HttpPost("populate")]
	public async Task<IActionResult> Populate(
		[FromBody] PopulateRequest request,
		CancellationToken ct)
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

		string? baseUrl = _apiOptions.ResolveBaseUrl(request.Env);
		if (baseUrl == null)
			return StatusCode(503, new { error = "No Commercial API URL configured for this environment." });

		using CommercialApiPolicyClient client = new(baseUrl);
		MoE.GhostDraftDataModel.SDK.CDMPolicyView policy;
		try
		{
			policy = await client.GetPolicyAsync(request.PolicyNumber, ct);
		}
		catch (HttpRequestException ex)
		{
			return StatusCode(502, new { error = $"Commercial API error: {ex.Message}" });
		}

		IReadOnlyList<FapPdfTools.Server.Infrastructure.FormDatEntry> entries =
			await _formClient.ResolveFormFileNameAsync(request.FormNumber, request.EditionDate, ct);
		if (entries.Count == 0)
			return NotFound(new { error = $"Form '{request.FormNumber}' edition '{request.EditionDate}' not found in FORM.DAT." });

		FapParseResult? fap = await _formClient.ParseFapFileAsync(entries[0].FileName, ct);
		if (fap == null)
			return NotFound(new { error = $"FAP file '{entries[0].FileName}' not found." });

		DdtParseResult? ddt = await _formClient.ParseDdtFileAsync(entries[0].FileName, ct);
		string? fapPath = _formClient.FindFormFilePath(entries[0].FileName, ".FAP");
		FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;

		(byte[] pdfBytes, _) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo);

		IReadOnlyDictionary<string, string> values = map.BuildValues(policy);
		pdfBytes = _pdfGenerator.FillFields(pdfBytes, values, request.Flatten);

		string suffix = request.Flatten ? "_policy_flat" : "_policy";
		return File(pdfBytes, "application/pdf", $"{entries[0].FileName}{suffix}.pdf");
	}

	/// <summary>
	/// Return the field values that would be populated for a policy without rendering.
	/// Useful for the client "Load from policy" preview before filling.
	/// </summary>
	[HttpPost("field-values")]
	public async Task<IActionResult> GetFieldValues(
		[FromBody] PopulateRequest request,
		CancellationToken ct)
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

		string? baseUrl = _apiOptions.ResolveBaseUrl(request.Env);
		if (baseUrl == null)
			return StatusCode(503, new { error = "No Commercial API URL configured for this environment." });

		using CommercialApiPolicyClient client = new(baseUrl);
		try
		{
			MoE.GhostDraftDataModel.SDK.CDMPolicyView policy = await client.GetPolicyAsync(request.PolicyNumber, ct);
			IReadOnlyDictionary<string, string> values = map.BuildValues(policy);
			return Ok(values);
		}
		catch (HttpRequestException ex)
		{
			return StatusCode(502, new { error = $"Commercial API error: {ex.Message}" });
		}
	}
}

public record PopulateRequest(
	string FormNumber,
	string EditionDate,
	string PolicyNumber,
	string? Env = null,
	bool Flatten = true);
