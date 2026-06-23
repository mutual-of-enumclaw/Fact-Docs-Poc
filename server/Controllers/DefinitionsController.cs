using System.Drawing;
using Microsoft.AspNetCore.Mvc;
using Spire.Pdf;
using Spire.Pdf.Texts;
using Spire.Pdf.Widget;
using FapPdfTools.Server.Infrastructure;
using FapPdfTools.Server.Models;

namespace FapPdfTools.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DefinitionsController : ControllerBase
{
	private readonly FormDefinitionStore _store;
	private readonly FormFileClient _formClient;
	private readonly FapToPdfGenerator _pdfGenerator;

	public DefinitionsController(
		FormDefinitionStore store,
		FormFileClient formClient,
		FapToPdfGenerator pdfGenerator)
	{
		_store = store;
		_formClient = formClient;
		_pdfGenerator = pdfGenerator;
	}

	/// <summary>List all authored form definitions.</summary>
	[HttpGet]
	public IActionResult ListDefinitions()
	{
		IReadOnlyList<FormDefinition> defs = _store.ListAll();
		return Ok(defs.Select(d => new
		{
			d.Id,
			d.Name,
			d.Description,
			d.SourceFormNumber,
			d.SourceEditionDate,
			d.PageCount,
			d.PageWidth,
			d.PageHeight,
			FieldCount = d.Fields.Count,
			d.CreatedAt,
			d.UpdatedAt,
		}));
	}

	/// <summary>Get a single authored form definition.</summary>
	[HttpGet("{id}")]
	public IActionResult GetDefinition(string id)
	{
		FormDefinition? def = _store.Get(id);
		if (def == null) return NotFound(new { error = $"Definition '{id}' not found." });
		return Ok(def);
	}

	/// <summary>Create or replace a form definition.</summary>
	[HttpPut("{id}")]
	public IActionResult SaveDefinition(string id, [FromBody] FormDefinition def)
	{
		def.Id = id;
		_store.Save(def);
		return Ok(def);
	}

	/// <summary>Delete a form definition.</summary>
	[HttpDelete("{id}")]
	public IActionResult DeleteDefinition(string id)
	{
		if (!_store.Delete(id))
			return NotFound(new { error = $"Definition '{id}' not found." });
		return NoContent();
	}

	/// <summary>Render an authored form definition to a fillable PDF.</summary>
	[HttpPost("{id}/render")]
	public IActionResult RenderDefinition(
		string id,
		[FromBody] RenderDefinitionRequest? request)
	{
		FormDefinition? def = _store.Get(id);
		if (def == null) return NotFound(new { error = $"Definition '{id}' not found." });

		(byte[] pdfBytes, IReadOnlyList<string> fieldNames) = _pdfGenerator.GeneratePdfBytes(def);

		if (request?.FieldValues is { Count: > 0 } values)
			pdfBytes = _pdfGenerator.FillFields(pdfBytes, values, request.Flatten);

		Response.Headers.Append("X-Field-Count", fieldNames.Count.ToString());
		return File(pdfBytes, "application/pdf", $"{def.Name}.pdf");
	}

	/// <summary>
	/// Render one page of the definition as a PNG image for canvas background display.
	/// </summary>
	[HttpGet("{id}/page-image")]
	public IActionResult GetPageImage(string id, [FromQuery] int page = 1, [FromQuery] int dpi = 144)
	{
		FormDefinition? def = _store.Get(id);
		if (def == null) return NotFound(new { error = $"Definition '{id}' not found." });

		(byte[] pdfBytes, _) = _pdfGenerator.GeneratePdfBytes(def);

		using var pdfDoc = new PdfDocument();
		pdfDoc.LoadFromBytes(pdfBytes);

		int pageIndex = Math.Clamp(page - 1, 0, pdfDoc.Pages.Count - 1);
		using System.Drawing.Image img = pdfDoc.SaveAsImage(pageIndex, dpi, dpi);
		using var ms = new System.IO.MemoryStream();
		img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
		byte[] pngBytes = ms.ToArray();
		Response.Headers.Append("Cache-Control", "public, max-age=300");
		return File(pngBytes, "image/png");
	}

	/// <summary>
	/// Seed a new authored form definition from a legacy FAP form. The definition
	/// is immediately saved and its full JSON is returned so the client can open the editor.
	/// </summary>
	[HttpPost("import-legacy")]
	public async Task<IActionResult> ImportLegacy(
		[FromBody] ImportLegacyRequest request,
		CancellationToken ct)
	{
		IReadOnlyList<FapPdfTools.Server.Infrastructure.FormDatEntry> entries =
			await _formClient.ResolveFormFileNameAsync(request.FormNumber, request.EditionDate, ct);
		if (entries.Count == 0)
			return NotFound(new { error = $"Form '{request.FormNumber}' edition '{request.EditionDate}' not found." });

		FapParseResult? fap = await _formClient.ParseFapFileAsync(entries[0].FileName, ct);
		if (fap == null)
			return NotFound(new { error = $"FAP file '{entries[0].FileName}' not found." });

		string name = string.IsNullOrWhiteSpace(request.Name)
			? $"{request.FormNumber} {request.EditionDate}"
			: request.Name;		FormDefinition def = FapFormDefinitionConverter.FromFapParseResult(
			fap, name, request.FormNumber, request.EditionDate);		_store.Save(def);
		return CreatedAtAction(nameof(GetDefinition), new { id = def.Id }, def);
	}

	/// <summary>
	/// Import an existing PDF as an editable form definition.
	/// Extracts text blocks and fillable form fields as individually moveable elements.
	/// </summary>
	[HttpPost("import-pdf")]
	[DisableRequestSizeLimit]
	public async Task<IActionResult> ImportPdf(
		IFormFile file,
		[FromForm] string? name,
		CancellationToken ct)
	{
		if (file == null || file.Length == 0)
			return BadRequest(new { error = "No file uploaded." });

		using MemoryStream ms = new();
		await file.CopyToAsync(ms, ct);
		ms.Position = 0;

		PdfDocument doc = new();
		doc.LoadFromStream(ms);

		float pageW = (float)doc.Pages[0].ActualSize.Width;
		float pageH = (float)doc.Pages[0].ActualSize.Height;

		FormDefinition def = new()
		{
			Id = Guid.NewGuid().ToString("N")[..8],
			Name = string.IsNullOrWhiteSpace(name)
				? System.IO.Path.GetFileNameWithoutExtension(file.FileName)
				: name,
			PageCount = doc.Pages.Count,
			PageWidth = pageW,
			PageHeight = pageH,
		};

		// --- Extract existing fillable text fields ---
		if (doc.Form is PdfFormWidget formWidget)
		{
			for (int i = 0; i < formWidget.FieldsWidget.Count; i++)
			{
				if (formWidget.FieldsWidget[i] is PdfTextBoxFieldWidget tb)
				{
					RectangleF b = tb.Bounds;
					string fieldName = string.IsNullOrWhiteSpace(tb.Name) ? $"Field{i + 1}" : tb.Name;
					def.Fields.Add(new FormDefinitionField(
						fieldName, 0,
						b.X, b.Y,
						b.Width > 0 ? b.Width : 72f,
						b.Height > 0 ? b.Height : 14f,
						0, 10f, false, 50));
				}
			}
		}

		// --- Extract positioned text fragments per page ---
		for (int pi = 0; pi < doc.Pages.Count; pi++)
		{
			PdfPageBase page = doc.Pages[pi];
			PdfTextFinder finder = new(page);
			List<PdfTextFragment> fragments = finder.FindAllText();
			if (fragments == null) continue;

			foreach (PdfTextFragment fragment in fragments)
			{
				if (string.IsNullOrWhiteSpace(fragment.Text)) continue;
				RectangleF[] bounds = fragment.Bounds;
				if (bounds == null || bounds.Length == 0) continue;
				RectangleF r = bounds[0];
				def.StaticTexts.Add(new FormDefinitionStaticText(
					fragment.Text, pi,
					r.X, r.Y,
					r.Width > 0 ? r.Width : 50f,
					r.Height > 0 ? r.Height : 12f,
					0, 10f, false));
			}
		}

		def.CreatedAt = def.UpdatedAt = DateTime.UtcNow;
		_store.Save(def);
		return CreatedAtAction(nameof(GetDefinition), new { id = def.Id }, def);
	}
}

public record RenderDefinitionRequest(
	Dictionary<string, string>? FieldValues,
	bool Flatten = true);

public record ImportLegacyRequest(
	string FormNumber,
	string EditionDate,
	string? Name = null);
