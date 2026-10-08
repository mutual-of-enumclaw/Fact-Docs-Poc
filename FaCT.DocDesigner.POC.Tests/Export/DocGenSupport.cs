#if DOCGEN_FILLER
using System.Diagnostics;

// DocGen's DocxTemplateFiller is compiled into the tests from the sibling fact-docgen repo (see the .csproj); this is the
// one type it needs from DocGen's Core project.
namespace MoE.Commercial.Documents.Generation.Core.Components.CustomDocuments
{
	public class CustomDocumentTemplateException(string message) : Exception(message);
}

namespace FaCT.DocDesigner.POC.Tests.Export
{
	/// <summary>DOCX => PDF with headless LibreOffice, the way DocGen's LibreOfficePdfConverter does it.</summary>
	internal static class LibreOffice
	{
		private static readonly string[] KnownPaths =
		[
			@"C:\Program Files\LibreOffice\program\soffice.exe",
			@"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
		];

		private static readonly SemaphoreSlim Slots = new(2);

		public static string? Path => KnownPaths.FirstOrDefault(File.Exists);

		public static async Task<byte[]> ToPdfAsync(byte[] docx)
		{
			var soffice = Path ?? throw new InvalidOperationException("LibreOffice is not installed.");
			await Slots.WaitAsync();
			var work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "parity-" + Guid.NewGuid().ToString("N"));
			try
			{
				Directory.CreateDirectory(work);
				var input = System.IO.Path.Combine(work, "input.docx");
				await File.WriteAllBytesAsync(input, docx);
				var start = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
				start.ArgumentList.Add($"-env:UserInstallation={new Uri(System.IO.Path.Combine(work, "profile")).AbsoluteUri}");
				foreach (var arg in new[] { "--headless", "--norestore", "--nolockcheck", "--convert-to", "pdf", "--outdir", work, input })
				{
					start.ArgumentList.Add(arg);
				}
				using var process = Process.Start(start)!;
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
				await process.WaitForExitAsync(timeout.Token);
				var output = System.IO.Path.Combine(work, "input.pdf");
				if (!File.Exists(output))
				{
					throw new InvalidOperationException($"LibreOffice failed ({process.ExitCode}): {await stdout} {await stderr}");
				}
				return await File.ReadAllBytesAsync(output);
			}
			finally
			{
				Slots.Release();
				try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
		}
	}
}
#endif
