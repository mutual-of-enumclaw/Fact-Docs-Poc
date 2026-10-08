using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FaCT.DocDesigner.POC.Tests.Import;

namespace FaCT.DocDesigner.POC.Tests;

/// <summary>Choosing the designer the UI tests drive: never an older build left running.</summary>
public sealed class DesignerServerTests
{
	private static readonly Guid ThisBuild = DesignerServer.ExpectedBuild;
	private static readonly Guid OtherBuild = Guid.NewGuid();

	[Theory]
	[InlineData(false, null, false, DesignerServer.Choice.StartOwn)]
	[InlineData(false, null, true, DesignerServer.Choice.StartOwn)]
	[InlineData(true, "this", false, DesignerServer.Choice.Reuse)]
	[InlineData(true, "this", true, DesignerServer.Choice.Reuse)]
	[InlineData(true, "other", false, DesignerServer.Choice.StartOwn)]
	[InlineData(true, "other", true, DesignerServer.Choice.Stale)]
	[InlineData(true, null, false, DesignerServer.Choice.StartOwn)]
	[InlineData(true, null, true, DesignerServer.Choice.Stale)]
	public void The_running_designer_is_reused_only_when_it_is_this_build(bool isUp, string? running, bool explicitUrl, DesignerServer.Choice expected)
	{
		Guid? build = running switch { "this" => ThisBuild, "other" => OtherBuild, _ => null };
		Assert.Equal(expected, DesignerServer.Decide(isUp, build, ThisBuild, explicitUrl));
	}

	[Fact]
	public void Free_ports_can_be_listened_on()
	{
		var port = DesignerServer.FreePort();
		Assert.InRange(port, 1024, 65535);
		var listener = new TcpListener(IPAddress.Loopback, port);
		listener.Start();
		listener.Stop();
	}

	[Fact]
	public async Task Nothing_listening_means_not_up_and_no_build()
	{
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		var uri = new Uri($"http://localhost:{DesignerServer.FreePort()}/");
		Assert.False(await DesignerServer.IsUpAsync(http, uri));
		Assert.Null(await DesignerServer.RunningBuildAsync(http, uri));
	}

	[Fact]
	public async Task An_older_designer_is_seen_as_up_with_its_own_build()
	{
		var port = DesignerServer.FreePort();
		using var fake = new HttpListener();
		fake.Prefixes.Add($"http://localhost:{port}/");
		fake.Start();
		var serving = Task.Run(async () =>
		{
			for (var i = 0; i < 2; i++)
			{
				var context = await fake.GetContextAsync();
				var body = context.Request.Url!.AbsolutePath == "/api/version"
					? JsonSerializer.Serialize(new { build = OtherBuild })
					: "{}";
				var bytes = Encoding.UTF8.GetBytes(body);
				context.Response.ContentType = "application/json";
				await context.Response.OutputStream.WriteAsync(bytes);
				context.Response.Close();
			}
		});

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		var uri = new Uri($"http://localhost:{port}/");
		var isUp = await DesignerServer.IsUpAsync(http, uri);
		var build = await DesignerServer.RunningBuildAsync(http, uri);
		await serving;

		Assert.True(isUp);
		Assert.Equal(OtherBuild, build);
		Assert.Equal(DesignerServer.Choice.StartOwn, DesignerServer.Decide(isUp, build, ThisBuild, explicitUrl: false));
	}

	[Fact]
	public async Task A_designer_without_a_version_endpoint_has_no_build()
	{
		var port = DesignerServer.FreePort();
		using var fake = new HttpListener();
		fake.Prefixes.Add($"http://localhost:{port}/");
		fake.Start();
		var serving = Task.Run(async () =>
		{
			var context = await fake.GetContextAsync();
			context.Response.StatusCode = 404;
			context.Response.Close();
		});

		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
		Assert.Null(await DesignerServer.RunningBuildAsync(http, new Uri($"http://localhost:{port}/")));
		await serving;
	}
}

/// <summary>The designer's own side: /api/version and --urls.</summary>
public sealed class DesignerVersionTests(ImportAppFactory factory) : IClassFixture<ImportAppFactory>
{
	[Fact]
	public async Task The_version_endpoint_reports_this_build()
	{
		var json = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/version");
		Assert.Equal(DesignerServer.ExpectedBuild, Guid.Parse(json.GetProperty("build").GetString()!));
	}

	[Fact]
	public async Task RunningBuild_reads_the_version_endpoint()
	{
		var client = factory.CreateClient();
		Assert.Equal(DesignerServer.ExpectedBuild, await DesignerServer.RunningBuildAsync(client, client.BaseAddress!));
	}
}

/// <summary>Starting a separate designer for the tests on a free port (what happens when an older build is running).</summary>
[Collection("Designer UI")]
public sealed class DesignerOwnServerTests
{
	[Fact]
	public async Task A_designer_started_on_a_free_port_serves_this_build()
	{
		var uri = new Uri($"http://localhost:{DesignerServer.FreePort()}/");
		using var server = DesignerServer.Start(uri);
		try
		{
			using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
			await DesignerServer.WaitUntilUpAsync(http, uri, server, TimeSpan.FromSeconds(90));

			Assert.Equal(DesignerServer.ExpectedBuild, await DesignerServer.RunningBuildAsync(http, uri));
			var page = await http.GetStringAsync(uri);
			Assert.Contains("Document Designer", page);
		}
		finally
		{
			server.Kill(entireProcessTree: true);
		}
	}
}
