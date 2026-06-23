using Microsoft.AspNetCore.Mvc;
using FapPdfTools.Server.Infrastructure;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ScenariosController : ControllerBase
{
	private readonly ScenarioStore _store;
	private readonly FormFileClient _formClient;
	private readonly FapToPdfGenerator _pdfGenerator;

	public ScenariosController(
		ScenarioStore store,
		FormFileClient formClient,
		FapToPdfGenerator pdfGenerator)
	{
		_store = store;
		_formClient = formClient;
		_pdfGenerator = pdfGenerator;
	}

	/// <summary>List all saved scenarios.</summary>
	[HttpGet]
	public IActionResult ListScenarios()
	{
		return Ok(_store.ListAll());
	}

	/// <summary>Get a single scenario.</summary>
	[HttpGet("{id}")]
	public IActionResult GetScenario(string id)
	{
		FormScenario? scenario = _store.Get(id);
		if (scenario == null) return NotFound(new { error = $"Scenario '{id}' not found." });
		return Ok(scenario);
	}

	/// <summary>Create a new scenario.</summary>
	[HttpPost]
	public IActionResult CreateScenario([FromBody] FormScenario scenario)
	{
		// Ensure a fresh ID if the client omitted it or sent an existing one.
		scenario = new FormScenario
		{
			Name = scenario.Name,
			FormNumber = scenario.FormNumber,
			EditionDate = scenario.EditionDate,
			FieldValues = scenario.FieldValues,
		};
		_store.Save(scenario);
		return CreatedAtAction(nameof(GetScenario), new { id = scenario.Id }, scenario);
	}

	/// <summary>Update an existing scenario.</summary>
	[HttpPut("{id}")]
	public IActionResult UpdateScenario(string id, [FromBody] FormScenario scenario)
	{
		scenario.Id = id;
		_store.Save(scenario);
		return Ok(scenario);
	}

	/// <summary>Delete a scenario.</summary>
	[HttpDelete("{id}")]
	public IActionResult DeleteScenario(string id)
	{
		if (!_store.Delete(id))
			return NotFound(new { error = $"Scenario '{id}' not found." });
		return NoContent();
	}

	/// <summary>Render the scenario's form with its saved field values and return a PDF.</summary>
	[HttpPost("{id}/run")]
	public async Task<IActionResult> RunScenario(string id, CancellationToken ct)
	{
		FormScenario? scenario = _store.Get(id);
		if (scenario == null) return NotFound(new { error = $"Scenario '{id}' not found." });

		IReadOnlyList<FormDatEntry> entries =
			await _formClient.ResolveFormFileNameAsync(scenario.FormNumber, scenario.EditionDate, ct);
		if (entries.Count == 0)
			return NotFound(new { error = $"Form '{scenario.FormNumber}' edition '{scenario.EditionDate}' not found." });

		FapParseResult? fap = await _formClient.ParseFapFileAsync(entries[0].FileName, ct);
		if (fap == null)
			return NotFound(new { error = $"FAP file '{entries[0].FileName}' not found." });

		DdtParseResult? ddt = await _formClient.ParseDdtFileAsync(entries[0].FileName, ct);
		string? fapPath = _formClient.FindFormFilePath(entries[0].FileName, ".FAP");
		FapPageInfo? pageInfo = fapPath != null ? FapToPdfGenerator.ParseHLine(fapPath) : null;

		(byte[] pdfBytes, _) = _pdfGenerator.GeneratePdfBytes(fap, ddt, pageInfo);
		pdfBytes = _pdfGenerator.FillFields(pdfBytes, scenario.FieldValues, flatten: false);

		return File(pdfBytes, "application/pdf", $"{scenario.Name}.pdf");
	}
}
