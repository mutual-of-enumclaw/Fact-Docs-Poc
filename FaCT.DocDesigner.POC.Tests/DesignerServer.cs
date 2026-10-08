using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>
/// Finds or starts the designer the UI tests drive. A designer already running at the default address is reused only
/// when it is this build (GET /api/version = the referenced FaCT.DocDesigner.POC assembly's module id); a designer from
/// another build (e.g. a `dotnet run` left open before a code change) would test old code, so the tests start their
/// own on a free port instead. An explicit DESIGNER_URL is used as given, but a different build there fails fast.
/// </summary>
public static class DesignerServer
{
	public static readonly Uri DefaultUri = new("http://localhost:5199/");

	public enum Choice { Reuse, StartOwn, Stale }

	/// <summary>The build these tests were compiled against.</summary>
	public static Guid ExpectedBuild => typeof(Program).Assembly.ManifestModule.ModuleVersionId;

	public static Choice Decide(bool isUp, Guid? runningBuild, Guid expectedBuild, bool explicitUrl)
	{
		if (!isUp) return Choice.StartOwn;
		if (runningBuild == expectedBuild) return Choice.Reuse;
		return explicitUrl ? Choice.Stale : Choice.StartOwn;
	}

	/// <summary>The running designer's build id, or null when nothing (or something else) answers.</summary>
	public static async Task<Guid?> RunningBuildAsync(HttpClient http, Uri baseUri)
	{
		try
		{
			using var response = await http.GetAsync(new Uri(baseUri, "api/version"));
			if (!response.IsSuccessStatusCode) return null;
			var json = await response.Content.ReadFromJsonAsync<JsonElement>();
			return json.TryGetProperty("build", out var build) && Guid.TryParse(build.GetString(), out var id) ? id : null;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
		{
			return null;
		}
	}

	public static async Task<bool> IsUpAsync(HttpClient http, Uri baseUri)
	{
		try
		{
			// Answers without signing in, also when the designer runs with security on.
			using var response = await http.GetAsync(new Uri(baseUri, "api/version"));
			return response.IsSuccessStatusCode;
		}
		catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
		{
			return false;
		}
	}

	public static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
		finally { listener.Stop(); }
	}

	/// <summary>Starts FaCT.DocDesigner.POC (dotnet run --no-build) listening on <paramref name="baseUri"/>; extra arguments
	/// are configuration overrides such as <c>--ImportReview:OutputRoot=C:\temp\x</c>.</summary>
	public static Process Start(Uri baseUri, params string[] settings)
	{
		var project = Path.Combine(GoldenCases.RepoRoot, "FaCT.DocDesigner.POC", "FaCT.DocDesigner.POC.csproj");
		var urls = baseUri.GetLeftPart(UriPartial.Authority);
		var extra = string.Concat(settings.Select(s => " \"" + s.Replace("\"", string.Empty) + "\""));
		var server = Process.Start(new ProcessStartInfo("dotnet", $"run --no-build --project \"{project}\" -- --urls {urls}{extra}")
		{
			WorkingDirectory = Path.GetDirectoryName(project)!,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		}) ?? throw new InvalidOperationException("Could not start the designer.");
		server.OutputDataReceived += (_, _) => { };
		server.ErrorDataReceived += (_, _) => { };
		server.BeginOutputReadLine();
		server.BeginErrorReadLine();
		return server;
	}

	public static async Task WaitUntilUpAsync(HttpClient http, Uri baseUri, Process server, TimeSpan timeout)
	{
		var deadline = DateTime.UtcNow + timeout;
		while (!await IsUpAsync(http, baseUri))
		{
			if (server.HasExited || DateTime.UtcNow > deadline)
			{
				throw new InvalidOperationException($"Designer did not start at {baseUri}. Build FaCT.DocDesigner.POC first (dotnet build).");
			}
			await Task.Delay(500);
		}
	}
}
