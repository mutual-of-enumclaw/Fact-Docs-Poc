namespace FaCT.DocDesigner.POC.Templates;

/// <summary>Writes a file through a temp file and a replace, so readers never see half a file.</summary>
internal static class AtomicFile
{
	public static async Task WriteAllTextAsync(string path, string text)
	{
		var temp = path + ".tmp";
		await File.WriteAllTextAsync(temp, text);
		// Windows can briefly hold a just-written file (indexer / antivirus): replacing it right away may be refused.
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				File.Move(temp, path, overwrite: true);
				return;
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
			{
				await Task.Delay(20 * attempt);
			}
		}
	}
}
