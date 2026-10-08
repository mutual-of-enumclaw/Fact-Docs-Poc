namespace FapPdfTools.Server.Models;

// ---------------------------------------------------------------------------
// Geometry — all coordinates in PDF points (72 DPI).
// Letter page: 612 × 792 pts.
// ---------------------------------------------------------------------------

/// <summary>A fillable field placed on an authored form.</summary>
public record FormDefinitionField(
	string Name,
	int Page,
	float X,
	float Y,
	float Width,
	float Height,
	int FontId,
	float PointSize,
	bool Bold,
	int MaxLength);

/// <summary>A static (non-interactive) text element.</summary>
public record FormDefinitionStaticText(
	string Text,
	int Page,
	float X,
	float Y,
	float Width,
	float Height,
	int FontId,
	float PointSize,
	bool Bold);

/// <summary>A drawn line or rectangle.</summary>
public record FormDefinitionLine(
	int Page,
	float X1,
	float Y1,
	float X2,
	float Y2,
	float LineWidth,
	int Style);

/// <summary>
/// A form definition authored in the app or imported from a legacy FAP.
/// Persisted as JSON under the configured <c>AuthoredFormsDirectory</c>.
/// All coordinates are in PDF points (72 DPI); letter default is 612 × 792 pts.
/// </summary>
public class FormDefinition
{
	public string Id { get; set; } = NewId();
	public string Name { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public string? SourceFormNumber { get; set; }
	public string? SourceEditionDate { get; set; }
	public int PageCount { get; set; } = 1;
	/// <summary>Page width in PDF points. Letter = 612.</summary>
	public float PageWidth { get; set; } = 612f;
	/// <summary>Page height in PDF points. Letter = 792.</summary>
	public float PageHeight { get; set; } = 792f;
	public List<FormDefinitionField> Fields { get; set; } = [];
	public List<FormDefinitionStaticText> StaticTexts { get; set; } = [];
	public List<FormDefinitionLine> Lines { get; set; } = [];
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

	private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
