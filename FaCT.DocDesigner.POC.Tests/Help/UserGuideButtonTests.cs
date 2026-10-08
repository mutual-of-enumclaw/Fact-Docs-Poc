using System.Net;
using System.Text.Json;

namespace FaCT.DocDesigner.POC.Tests.Help;

/// <summary>The designer's User Guide button.</summary>
[Collection("Isolated designer")]
public sealed class UserGuideButtonTests(IsolatedDesignerFixture fixture)
{
	[Fact]
	public async Task The_app_bar_links_to_the_user_guide_pdf_in_a_new_tab()
	{
		var page = fixture.Page;
		Assert.Equal("/api/help/user-guide.pdf", await page.EvaluateExpressionAsync<string>("document.getElementById('btnHelp').getAttribute('href')"));
		Assert.Equal("_blank", await page.EvaluateExpressionAsync<string>("document.getElementById('btnHelp').target"));
		Assert.Equal("noopener", await page.EvaluateExpressionAsync<string>("document.getElementById('btnHelp').rel"));
		Assert.Contains("User Guide", await page.EvaluateExpressionAsync<string>("document.getElementById('btnHelp').title"));
		// The icon is drawn.
		Assert.True(await page.EvaluateExpressionAsync<bool>("!!document.querySelector('#btnHelp svg')"));
	}

	[Fact]
	public async Task Without_a_published_guide_template_the_link_explains_why()
	{
		// The isolated designer's template store is empty (unless the guide builder ran in it).
		if (Environment.GetEnvironmentVariable("BUILD_USER_GUIDE") == "1") return;
		using var response = await fixture.Http.GetAsync("api/help/user-guide.pdf");
		Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
		Assert.Equal("The user-guide template has not been published yet.",
			JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString());
	}
}
