namespace FaCT.DocDesigner.POC.Images;

/// <summary>
/// Where the Workbench (or fact-commercial-api) puts vendor images (e.g. Moody's aerial evidence) before sending the
/// document message. The message only carries references ("docimage:{policy}/moodys/{yyyy-MM-dd}/{requestId}/{locNum}/B1.jpg").
/// In Azure these live in the existing policydocs container next to forms/, ratings/ and traceworksheets/, read through
/// docgen's IAzureBlobStorageService; the POC uses a local folder with the same layout.
/// </summary>
public interface IDocumentImageStore
{
	/// <summary>Returns the image bytes, or null when the blob does not exist.</summary>
	Task<byte[]?> GetAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Local stand-in for the policydocs container: App_Data/document-images/policydocs/{blob name}.</summary>
public sealed class FileSystemDocumentImageStore : IDocumentImageStore
{
	private readonly string _root;

	public FileSystemDocumentImageStore(IWebHostEnvironment environment)
	{
		_root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "App_Data", "document-images", "policydocs"));
		Directory.CreateDirectory(_root);
	}

	public async Task<byte[]?> GetAsync(string name, CancellationToken cancellationToken = default)
	{
		// Names are validated by DocumentImageResolver; this is the second line of defense against path traversal.
		var path = Path.GetFullPath(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar)));
		if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
		{
			return null;
		}
		return await File.ReadAllBytesAsync(path, cancellationToken);
	}
}
