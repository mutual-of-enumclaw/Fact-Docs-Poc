using System.Text.Json;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Images;

/// <summary>
/// Turns image references in the message data into data: URIs before rendering (the PDF renderer blocks network).
/// A reference is a string value "docimage:{blob name}". Only real raster images are embedded (checked by content,
/// not by name); missing or rejected images resolve to "" so the template's Data Image is simply left out.
/// </summary>
public sealed partial class DocumentImageResolver(IDocumentImageStore store, ILogger<DocumentImageResolver> logger)
{
	public const string Prefix = "docimage:";
	public const int MaxImagesPerDocument = 300;
	public const int MaxImageBytes = 5 * 1024 * 1024;

	/// <summary>Blob name inside a reference, e.g. "BAP000001002/moodys/2026-09-29/{requestId}/001/B1.jpg"; null if the value isn't a valid reference.</summary>
	public static string? BlobName(string? value) =>
		value is not null && value.StartsWith(Prefix, StringComparison.Ordinal) && NamePattern().IsMatch(value[Prefix.Length..])
			? value[Prefix.Length..]
			: null;

	/// <summary>Loads every distinct reference in the data. Returns reference => data: URI ("" when unavailable).</summary>
	public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(JsonElement data, CancellationToken cancellationToken = default)
	{
		var references = new HashSet<string>(StringComparer.Ordinal);
		Collect(data, references);
		if (references.Count > MaxImagesPerDocument)
		{
			throw new InvalidOperationException($"The data references {references.Count} images; the limit is {MaxImagesPerDocument}.");
		}

		var loaded = await Task.WhenAll(references.Select(async reference => (reference, uri: await LoadAsync(reference, cancellationToken))));
		return loaded.ToDictionary(x => x.reference, x => x.uri, StringComparer.Ordinal);
	}

	/// <summary>Image bytes and content type for a blob name (designer canvas preview), or null.</summary>
	public async Task<(byte[] Bytes, string ContentType)?> GetImageAsync(string name, CancellationToken cancellationToken = default)
	{
		if (!NamePattern().IsMatch(name)) return null;
		var bytes = await store.GetAsync(name, cancellationToken);
		var contentType = bytes is null || bytes.Length > MaxImageBytes ? null : Sniff(bytes);
		return contentType is null ? null : (bytes!, contentType);
	}

	private async Task<string> LoadAsync(string reference, CancellationToken cancellationToken)
	{
		var name = BlobName(reference);
		if (name is null)
		{
			logger.LogWarning("Invalid image reference {Reference}", reference);
			return string.Empty;
		}
		var image = await GetImageAsync(name, cancellationToken);
		if (image is null)
		{
			logger.LogWarning("Image {Name} is missing, too large or not a PNG/JPEG/WebP/GIF", name);
			return string.Empty;
		}
		return "data:" + image.Value.ContentType + ";base64," + Convert.ToBase64String(image.Value.Bytes);
	}

	private static void Collect(JsonElement element, HashSet<string> references)
	{
		switch (element.ValueKind)
		{
			case JsonValueKind.Object:
				foreach (var property in element.EnumerateObject()) Collect(property.Value, references);
				break;
			case JsonValueKind.Array:
				foreach (var item in element.EnumerateArray()) Collect(item, references);
				break;
			case JsonValueKind.String when element.GetString()!.StartsWith(Prefix, StringComparison.Ordinal):
				references.Add(element.GetString()!);
				break;
		}
	}

	// Raster formats only: SVG can carry script and external references.
	private static string? Sniff(byte[] b) =>
		b.Length > 12 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
		: b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg"
		: b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P' ? "image/webp"
		: b.Length > 6 && b[0] == 'G' && b[1] == 'I' && b[2] == 'F' && b[3] == '8' ? "image/gif"
		: null;

	// {policy}/{image folder}/segments... inside the policydocs container. Only image folders are allowed, so a
	// reference can't reach the policy's forms, ratings or trace worksheets. Every segment starts with a letter or
	// digit, so "." and ".." can't appear.
	[GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/(moodys)(/[A-Za-z0-9][A-Za-z0-9._-]{0,127}){1,6}$")]
	private static partial Regex NamePattern();
}
