using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population;

/// <summary>
/// Converts a legacy FAP form into a fillable PDF and populates it directly from
/// a <see cref="CDMPolicyView"/>, bypassing GhostDraft. The rendering/parsing work
/// is delegated to the FapPdfTools.Core services; this type only wires them up
/// (without a DI container) and applies a per-form <see cref="IFormFieldMap"/>.
/// </summary>
public sealed class CdmFormPopulator
{
	private readonly FormFileClient _formClient;
	private readonly FapToPdfGenerator _generator;

	public CdmFormPopulator(FormFileOptions options)
	{
		IOptions<FormFileOptions> opts = Options.Create(options);
		FxrFontLibrary fxr = new(opts, NullLogger<FxrFontLibrary>.Instance);
		_formClient = new FormFileClient(opts, NullLogger<FormFileClient>.Instance);
		_generator = new FapToPdfGenerator(NullLogger<FapToPdfGenerator>.Instance, fxr);
	}

	/// <summary>
	/// Render the form named by <paramref name="map"/> and fill it with values
	/// resolved from <paramref name="policy"/>. Returns the populated PDF bytes.
	/// </summary>
	/// <param name="flatten">When true, fields are flattened (read-only) after filling.</param>
	public async Task<byte[]> PopulateAsync(
		IFormFieldMap map, CDMPolicyView policy, bool flatten = true, CancellationToken ct = default)
	{
		var entries = await _formClient.ResolveFormFileNameAsync(map.FormNumber, map.EditionDate, ct);
		if (entries.Count == 0)
			throw new InvalidOperationException(
				$"Form '{map.FormNumber}' edition '{map.EditionDate}' was not found in FORM.DAT.");

		var entry = entries[0];
		var fap = await _formClient.ParseFapFileAsync(entry.FileName, ct)
			?? throw new InvalidOperationException($"FAP file '{entry.FileName}' was not found.");
		var ddt = await _formClient.ParseDdtFileAsync(entry.FileName, ct);

		var (pdfBytes, _) = _generator.GeneratePdfBytes(fap, ddt);
		var values = map.BuildValues(policy);
		return _generator.FillFields(pdfBytes, values, flatten);
	}
}
