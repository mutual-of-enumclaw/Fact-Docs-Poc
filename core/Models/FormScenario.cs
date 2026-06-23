namespace FapPdfTools.Server.Models;

/// <summary>
/// A named test scenario: a snapshot of field values for a specific form that
/// can be replayed at any time to render a filled PDF.
/// Persisted as JSON under the configured <c>ScenariosDirectory</c>.
/// </summary>
public class FormScenario
{
	public string Id { get; set; } = NewId();
	public string Name { get; set; } = string.Empty;
	public string FormNumber { get; set; } = string.Empty;
	public string EditionDate { get; set; } = string.Empty;
	/// <summary>Field-name → value pairs. Keys are case-insensitive AcroForm field names.</summary>
	public Dictionary<string, string> FieldValues { get; set; } = [];
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

	private static string NewId() => Guid.NewGuid().ToString("N")[..8];
}
