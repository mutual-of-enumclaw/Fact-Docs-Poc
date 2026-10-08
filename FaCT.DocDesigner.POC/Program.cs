using System.Text.Json;
using FaCT.DocDesigner.POC.Export;
using FaCT.DocDesigner.POC.Images;
using FaCT.DocDesigner.POC.Import;
using FaCT.DocDesigner.POC.Legacy;
using FaCT.DocDesigner.POC.Mapping;
using FaCT.DocDesigner.POC.Rendering;
using FaCT.DocDesigner.POC.Review;
using FaCT.DocDesigner.POC.Security;
using FaCT.DocDesigner.POC.Spelling;
using FaCT.DocDesigner.POC.Templates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Local-only POC. `--urls` (e.g. the UI tests' own copy on a free port) overrides the default address.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
	builder.WebHost.UseUrls("http://localhost:5199");
}

builder.Services.AddSingleton<DocumentComposer>();
builder.Services.AddSingleton<PdfRenderer>();
builder.Services.AddSingleton<DocxTemplateExporter>();
// Templates:Root / Clauses:Root move the version stores out of App_Data (tests use temp folders).
builder.Services.AddSingleton(sp => new TemplateStore(StoreRoot(sp, "Templates:Root", "templates")));
builder.Services.AddSingleton(sp => new ClauseStore(StoreRoot(sp, "Clauses:Root", "clauses")));
builder.Services.AddSingleton(sp => new ScenarioStore(StoreRoot(sp, "Scenarios:Root", "scenarios")));
builder.Services.AddSingleton<FieldUsageSearch>();
builder.Services.AddSingleton(sp => new BlockStore(StoreRoot(sp, "Blocks:Root", "blocks")));
builder.Services.AddSingleton(sp => new FaCT.DocDesigner.POC.Themes.ThemeStore(StoreRoot(sp, "Themes:Root", "themes"),
	Path.Combine(sp.GetRequiredService<IWebHostEnvironment>().WebRootPath, "brand", "moe-document.css")));
builder.Services.AddSingleton(sp => new CommentStore(StoreRoot(sp, "Comments:Root", "comments")));
builder.Services.AddSingleton<Gallery>();
builder.Services.AddSingleton(sp => new SpellChecker(
	sp.GetRequiredService<IWebHostEnvironment>().ContentRootPath, StoreRoot(sp, "Spelling:Root", "spelling")));
builder.Services.AddSingleton<LegacyAssetStore>();
builder.Services.AddSingleton<LegacyFormImporter>();
builder.Services.AddSingleton<PdfImporter>();
builder.Services.AddSingleton<DocxImporter>();
// Vendor images (Moody's) referenced from message data. Swap for a Blob-backed store in Azure.
builder.Services.AddSingleton<IDocumentImageStore, FileSystemDocumentImageStore>();
builder.Services.AddSingleton<DocumentImageResolver>();
builder.Services.AddSingleton<ReviewStores>();
// Features:{Name}=false hides a feature in the designer; Features:Profile=demo picks the FeatureProfiles:demo set.
builder.Services.AddSingleton<FaCT.DocDesigner.POC.Features.FeatureFlags>();
builder.Services.AddSingleton<FaCT.DocDesigner.POC.Embedding.EmbedOptions>();
builder.Services.AddSingleton<FaCT.DocDesigner.POC.DocumentModels.DocumentModelStore>();
builder.Services.AddCors();

// ---- Security: roles and audit ------------------------------------------------------------------------------------
// Security:Enabled=false (the default) keeps the POC open: everyone is the local designer with every role. When it is
// on, people sign in (POC: pick a user from Security:Users; production: Entra ID) and each change needs its role.
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection("Security"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<SecurityOptions>>().Value);
builder.Services.AddSingleton(sp => new AuditLog(StoreRoot(sp, "Audit:Root", "audit")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
	options.Cookie.Name = "designer.auth";
	options.Cookie.HttpOnly = true;
	// Strict: the cookie is never sent with requests started by other sites (no CSRF).
	options.Cookie.SameSite = SameSiteMode.Strict;
	options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
	options.ExpireTimeSpan = TimeSpan.FromHours(8);
	options.SlidingExpiration = true;
	// An API: answer 401/403 instead of redirecting to a login page.
	options.Events.OnRedirectToLogin = context =>
	{
		context.Response.StatusCode = StatusCodes.Status401Unauthorized;
		return context.Response.WriteAsJsonAsync(new { error = "Sign in to use the designer." });
	};
	options.Events.OnRedirectToAccessDenied = async context =>
	{
		var audit = context.HttpContext.RequestServices.GetRequiredService<AuditLog>();
		await audit.AppendAsync(SecurityOptions.NameOf(context.HttpContext.User), "access.denied",
			detail: context.Request.Method + " " + context.Request.Path);
		context.Response.StatusCode = StatusCodes.Status403Forbidden;
		await context.Response.WriteAsJsonAsync(new { error = "Your role doesn't allow this." });
	};
});
builder.Services.AddAuthorization(options =>
{
	// Every API needs a signed-in user unless marked otherwise.
	options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
	options.AddPolicy(DesignerRoles.CanEdit, policy => policy.RequireRole(DesignerRoles.Author));
	options.AddPolicy(DesignerRoles.CanPublish, policy => policy.RequireRole(DesignerRoles.Publisher));
	options.AddPolicy(DesignerRoles.CanComment, policy => policy.RequireRole(DesignerRoles.All.ToArray()));
});

var app = builder.Build();
// Read after Build so test hosts' configuration overrides apply.
var security = app.Services.GetRequiredService<SecurityOptions>();
var embedding = app.Services.GetRequiredService<FaCT.DocDesigner.POC.Embedding.EmbedOptions>();

// Only this site and the configured host apps (Embed:AllowedOrigins, e.g. Commercial Web) may show these pages in a frame.
app.Use((context, next) =>
{
	context.Response.Headers.ContentSecurityPolicy = "frame-ancestors " + embedding.FrameAncestors;
	return next(context);
});

app.UseDefaultFiles();
// POC: always revalidate so designer edits show up without a hard refresh.
app.UseStaticFiles(new StaticFileOptions
{
	OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache"
});

var grapesDist = Path.Combine(app.Environment.ContentRootPath, "node_modules", "grapesjs", "dist");
if (!Directory.Exists(grapesDist))
{
	throw new InvalidOperationException("GrapesJS not found. Run 'npm install' in the project folder.");
}
app.UseStaticFiles(new StaticFileOptions
{
	FileProvider = new PhysicalFileProvider(grapesDist),
	RequestPath = "/lib/grapesjs"
});

// The designer page and its scripts are public (it shows the sign-in); the APIs below are not.
app.UseRouting();
// Host apps that embed the designer (Embed:AllowedOrigins, e.g. Commercial Web) also call its API: template list, render.
app.UseCors(policy => policy
	.WithOrigins([.. embedding.AllowedOrigins])
	.WithMethods("GET", "POST", "PUT")
	.WithHeaders("Content-Type", "Authorization", "X-User-Id")
	.WithExposedHeaders("X-Template-Version", "X-Template-Language", "X-Watermark"));
app.UseAuthentication();
if (!security.Enabled)
{
	var local = SecurityOptions.Principal(
		new DesignerUser { Id = "local", Name = SecurityOptions.LocalUserName, Roles = [.. DesignerRoles.All] }, "Local");
	app.Use((context, next) =>
	{
		context.User = local;
		return next(context);
	});
}
app.UseAuthorization();

var sampleDataPath = Path.Combine(app.Environment.ContentRootPath, "SampleData", "workbench-results.json");

static string StoreRoot(IServiceProvider services, string setting, string folder) =>
	services.GetRequiredService<IConfiguration>()[setting] is { Length: > 0 } root
		? root
		: Path.Combine(services.GetRequiredService<IWebHostEnvironment>().ContentRootPath, "App_Data", folder);

async Task<JsonElement> LoadSampleDataAsync() =>
	JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(sampleDataPath));

static string ExportFileName(string? name, string extension)
{
	var clean = name is not null && TemplateStore.IsValidName(name) ? name : "template";
	return clean + extension;
}

IResult ExportDocx(string html, string? css, string? name, DocxTemplateExporter exporter, HttpResponse response)
{
	var export = exporter.Export(html, css);
	response.Headers["X-Export-Warnings"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(export.Warnings)));
	response.Headers.AccessControlExposeHeaders = "X-Export-Warnings, Content-Disposition";
	return Results.File(export.Docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ExportFileName(name, ".docx"));
}

async Task<IResult> ExportHtmlAsync(string html, string? css, JsonElement? data, string? name, DocumentComposer composer)
{
	var composed = data is { } d ? await composer.ComposeAsync(html, css, d) : await composer.ComposeTemplatePageAsync(html, css);
	return composed.Html is null
		? Results.BadRequest(new { error = composed.Error })
		: Results.File(System.Text.Encoding.UTF8.GetBytes(composed.Html), "text/html; charset=utf-8", ExportFileName(name, data is null ? ".template.html" : ".html"));
}

async Task<IResult> RenderPdfAsync(string html, string? css, JsonElement data, DocumentComposer composer, PdfRenderer pdf, string? stamp = null)
{
	var composed = await composer.ComposeAsync(html, css, data, stamp);
	if (composed.Error is not null)
	{
		return Results.BadRequest(new { error = composed.Error });
	}

	return Results.File(await pdf.RenderAsync(composed.Html!), "application/pdf");
}

// Sample message "data" payload: the default data model for new templates.
app.MapGet("/api/sample-data", () => Results.File(sampleDataPath, "application/json"));

// The User Guide is made with the designer: Help/user-guide.json is the content (data), the published "user-guide"
// template is the layout. Content is updated as features change; the PDF is always rendered fresh.
var userGuidePath = Path.Combine(app.Environment.ContentRootPath, "Help", "user-guide.json");
app.MapGet("/api/help/user-guide.json", () => Results.File(userGuidePath, "application/json"));
app.MapGet("/api/help/user-guide.pdf", async (HttpResponse response, TemplateStore templates, DocumentComposer composer, PdfRenderer pdf) =>
{
	var template = await templates.GetPublishedAsync("user-guide");
	if (template is null)
	{
		return Results.NotFound(new { error = "The user-guide template has not been published yet." });
	}
	var content = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(userGuidePath));
	response.Headers.ContentDisposition = "inline; filename=\"MOE-Document-Designer-User-Guide.pdf\"";
	return await RenderPdfAsync(template.Html, template.Css, content, composer, pdf);
});

// Which build is running (the assembly's module id): lets the UI tests refuse to drive a designer left running from
// an older build.
app.MapGet("/api/version", () => Results.Ok(new { build = typeof(Program).Assembly.ManifestModule.ModuleVersionId })).AllowAnonymous();

// ---- Sign-in (POC: choose a configured user; production: Entra ID with the same role claims) ----------------------
app.MapGet("/api/me", (HttpContext context) =>
	context.User.Identity?.IsAuthenticated == true
		? Results.Ok(new
		{
			id = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
			name = SecurityOptions.NameOf(context.User),
			roles = DesignerRoles.All.Where(context.User.IsInRole).ToArray(),
			securityEnabled = security.Enabled,
			requireSecondPersonToPublish = security.RequireSecondPersonToPublish
		})
		: Results.Json(new { error = "Sign in to use the designer.", securityEnabled = security.Enabled }, statusCode: StatusCodes.Status401Unauthorized))
	.AllowAnonymous();

// Who can sign in (only with security on). A real deployment signs in with Entra ID instead of picking a name.
app.MapGet("/api/users", () => Results.Ok(security.Enabled
	? security.Users.Select(u => new { u.Id, u.Name, roles = u.Roles.Where(r => DesignerRoles.All.Contains(r)).ToArray() })
	: [])).AllowAnonymous();

app.MapPost("/api/signin", async (SignInRequest request, HttpContext context, AuditLog audit) =>
{
	if (!security.Enabled) return Results.BadRequest(new { error = "Sign-in is not enabled." });
	var user = security.Find(request.Id);
	if (user is null) return Results.BadRequest(new { error = "Unknown user." });
	var principal = SecurityOptions.Principal(user, CookieAuthenticationDefaults.AuthenticationScheme);
	await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
	await audit.AppendAsync(user.Name, "user.signed-in", detail: string.Join(", ", user.Roles));
	return Results.Ok(new { user.Id, user.Name, roles = user.Roles });
}).AllowAnonymous();

app.MapPost("/api/signout", async (HttpContext context, AuditLog audit) =>
{
	if (security.Enabled && context.User.Identity?.IsAuthenticated == true)
	{
		await audit.AppendAsync(SecurityOptions.NameOf(context.User), "user.signed-out");
		await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
	}
	return Results.NoContent();
}).AllowAnonymous();

// Which designer features are on (the designer hides the rest).
app.MapGet("/api/features", (FaCT.DocDesigner.POC.Features.FeatureFlags flags) =>
	Results.Ok(new { profile = flags.Profile, features = flags.All() }));

// Embedded designer (/?embed=1): the host apps it may talk to.
app.MapGet("/api/embed", () => Results.Ok(new { allowedOrigins = embedding.AllowedOrigins }));

// Document models (the repository's models folder): what kinds of document can be designed, with an example record each.
app.MapGet("/api/document-models", async (FaCT.DocDesigner.POC.DocumentModels.DocumentModelStore models) =>
	Results.Ok(await models.ListAsync()));
app.MapGet("/api/document-models/{name}/{kind}", async (string name, string kind, FaCT.DocDesigner.POC.DocumentModels.DocumentModelStore models) =>
	await models.ReadAsync(name, kind) is { } json ? Results.Ok(json) : Results.NotFound());

// The choices for a template's details (type, category) and their limits.
app.MapGet("/api/template-details", () => Results.Ok(new
{
	types = TemplateDetails.Types,
	categories = TemplateDetails.Categories,
	maxTitle = TemplateDetails.MaxTitle,
	maxFormCode = TemplateDetails.MaxFormCode
}));

// The audit log: newest first, optionally for one template / clause, user or kind of action.
app.MapGet("/api/audit", async (string? kind, string? name, string? user, string? action, int? take, AuditLog audit) =>
	Results.Ok(await audit.ReadAsync(kind, name, user, action, take ?? 200)));

// Designer preview: render the current, unsaved designer output with sample data. "watermark" stamps a word over every
// page (e.g. DRAFT), replacing the template's own watermark.
app.MapPost("/api/render", async (RenderRequest request, DocumentComposer composer, PdfRenderer pdf) =>
	await RenderPdfAsync(request.Html, request.Css, request.Data ?? await LoadSampleDataAsync(), composer, pdf,
		string.IsNullOrWhiteSpace(request.Watermark) ? null : request.Watermark));

// Export: the design as a Word template (.docx, for DocGen), as an HTML template (placeholders kept) or as HTML filled
// with the sample data. "download" names the file. A Word export lists what could not be converted in the X-Export-Warnings
// header (base64 of a JSON array) so nothing is dropped silently.
app.MapPost("/api/export/docx", (RenderRequest request, DocxTemplateExporter exporter, HttpResponse response, string? name) =>
	ExportDocx(request.Html, request.Css, name, exporter, response));

app.MapPost("/api/export/html", async (RenderRequest request, DocumentComposer composer, string? name, bool? withData) =>
	await ExportHtmlAsync(request.Html, request.Css, withData == true ? request.Data ?? await LoadSampleDataAsync() : null, name, composer));

app.MapGet("/api/templates/{name}/export.docx", async (string name, int? version, string? lang, TemplateStore store, DocxTemplateExporter exporter, HttpResponse response) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is null) return UnknownLanguage(lang);
	store = store.Language(lang);
	var template = version is null ? await store.GetPublishedAsync(name) ?? await store.GetAsync(name) : await store.GetAsync(name, version);
	return template is null ? Results.NotFound() : ExportDocx(template.Html, template.Css, name, exporter, response);
});

app.MapGet("/api/templates/{name}/export.html", async (string name, int? version, bool? withData, string? lang, TemplateStore store, DocumentComposer composer) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is null) return UnknownLanguage(lang);
	store = store.Language(lang);
	var template = version is null ? await store.GetPublishedAsync(name) ?? await store.GetAsync(name) : await store.GetAsync(name, version);
	if (template is null) return Results.NotFound();
	var data = withData == true ? template.Model is { ValueKind: JsonValueKind.Object } model ? model : await LoadSampleDataAsync() : (JsonElement?)null;
	return await ExportHtmlAsync(template.Html, template.Css, data, name, composer);
});

// Legacy forms converted by fact-pdf-tools (`demo emit-html`). The body must be text/html: a cross-site form can't
// send that content type without a CORS preflight, which this app never allows.
app.MapPost("/api/legacy/import", async (HttpRequest request, LegacyFormImporter importer) =>
{
	if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "text/html", StringComparison.OrdinalIgnoreCase))
	{
		return Results.BadRequest(new { error = "Send the converted form as text/html." });
	}
	using var reader = new StreamReader(request.Body);
	var result = importer.Import(await reader.ReadToEndAsync());
	return result.Pages == 0
		? Results.BadRequest(new { error = "No form pages found. Expected the HTML written by fact-pdf-tools 'demo emit-html'." })
		: Results.Ok(result);
}).RequireAuthorization(DesignerRoles.CanEdit);

app.MapGet("/api/legacy/fonts.css", (LegacyAssetStore assets) => Results.Text(assets.FontCss(), "text/css"));

// Form fields (imported PDF / legacy forms) => likely model paths, for one-click mapping in the designer. "labels" (same
// order as "fields", null where unknown) are the words printed next to each field, for fields with meaningless names.
app.MapPost("/api/mapping/suggest", (MappingRequest request) =>
{
	var fields = request.Fields ?? [];
	var paths = request.Paths ?? [];
	var labels = request.Labels ?? [];
	if (fields.Length > 2000 || paths.Length > 5000)
	{
		return Results.BadRequest(new { error = "At most 2000 fields and 5000 model paths." });
	}
	if (fields.Any(f => f is null || f.Length > 200) || paths.Any(p => p?.Path is null || p.Path.Length > 200))
	{
		return Results.BadRequest(new { error = "Field names and paths must be at most 200 characters." });
	}
	if (labels.Length > fields.Length || labels.Any(l => l is { Length: > 200 }))
	{
		return Results.BadRequest(new { error = "Send at most one label (of at most 200 characters) per field." });
	}
	return Results.Ok(FieldMatcher.Suggest(fields.Select((f, i) => (f, i < labels.Length ? labels[i] : null)), paths));
});

// PDF or Word (.docx) upload => designer HTML. The body is the raw file with its own content type (application/pdf or
// the .docx type); like text/html above, a cross-site form can't send those without a CORS preflight.
// The declared type must match the file's content.
app.MapPost("/api/import", async (HttpRequest request, PdfImporter pdfImporter, DocxImporter docxImporter) =>
{
	var declared = DocumentImport.ForContentType(request.ContentType);
	if (declared == DocumentKind.Unknown)
	{
		return Results.BadRequest(new { error = $"Send a PDF ({DocumentImport.PdfContentType}) or a Word document ({DocumentImport.DocxContentType})." });
	}
	// Word only: which typed placeholders become Data Fields (braces, chevrons, brackets, all, none).
	if (!DocxImportOptions.TryParsePlaceholders(request.Query["placeholders"], out var placeholders))
	{
		return Results.BadRequest(new { error = "placeholders must be a comma-separated list of braces, chevrons, brackets, all or none." });
	}
	// PDF only: a page range ("5" or "5-40") for documents longer than the import limit.
	if (!PdfImportOptions.TryParsePages(request.Query["pages"], out var pages))
	{
		return Results.BadRequest(new { error = "pages must be a page number or a range such as 1-50." });
	}
	if (request.ContentLength > DocumentImport.MaxUploadBytes)
	{
		return TooLarge();
	}

	var bytes = await ReadUploadAsync(request.Body, DocumentImport.MaxUploadBytes, request.HttpContext.RequestAborted);
	if (bytes is null) return TooLarge();
	if (bytes.Length == 0) return Results.BadRequest(new { error = "The file is empty." });

	var actual = DocumentImport.Detect(bytes);
	if (actual != declared)
	{
		return Results.BadRequest(new
		{
			error = DocumentImport.IsLegacyOfficeFile(bytes)
				? "Older .doc files can't be imported. Open the file in Word and save it as .docx."
				: declared == DocumentKind.Pdf ? "The file is not a PDF." : "The file is not a Word (.docx) document."
		});
	}

	try
	{
		var result = actual == DocumentKind.Pdf ? pdfImporter.Import(bytes, pages) : docxImporter.Import(bytes, new DocxImportOptions(placeholders));
		return result.Pages == 0 && result.Html.Length == 0
			? Results.BadRequest(new { error = "Nothing could be imported from the file." })
			: Results.Ok(result);
	}
	catch (PdfTooLongException ex)
	{
		// The designer asks for a page range and sends the file again.
		return Results.BadRequest(new { error = ex.Message, pageCount = ex.PageCount, maxPages = PdfImporter.MaxPages });
	}
	catch (DocumentImportException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}

	static IResult TooLarge() => Results.Json(
		new { error = $"The file is larger than {DocumentImport.MaxUploadBytes / (1024 * 1024)} MB." },
		statusCode: StatusCodes.Status413PayloadTooLarge);
}).RequireAuthorization(DesignerRoles.CanEdit);

static async Task<byte[]?> ReadUploadAsync(Stream body, long maxBytes, CancellationToken cancellationToken)
{
	using var buffer = new MemoryStream();
	var chunk = new byte[81920];
	int read;
	while ((read = await body.ReadAsync(chunk, cancellationToken)) > 0)
	{
		if (buffer.Length + read > maxBytes) return null;
		buffer.Write(chunk, 0, read);
	}
	return buffer.ToArray();
}

app.MapGet("/api/legacy/assets/{file}", (string file, LegacyAssetStore assets) =>
	assets.TryGetFile(file, out var path, out var contentType) ? Results.File(path, contentType) : Results.NotFound());

// Designer canvas preview of "docimage:" references in the data model. PDFs embed the images server-side instead.
app.MapGet("/api/document-images/{**name}", async (string name, DocumentImageResolver images) =>
	await images.GetImageAsync(name) is { } image ? Results.File(image.Bytes, image.ContentType) : Results.NotFound());

IResult InvalidName() => Results.BadRequest(new { error = "Invalid template name." });

// Language variants (?lang=es): each language has its own Draft -> Published -> Retired versions of the same name.
IResult UnknownLanguage(string? lang) => Results.BadRequest(new { error = DocumentLanguages.UnknownLanguage(lang) });

// Saves, discards and publishes of another language record it as the audit detail ("Spanish"); English has none.
static string? LanguageDetail(DocumentLanguage? language) => language is null || language == DocumentLanguages.English ? null : language.Name;

// The data paths a document prints (its own and those of the clauses it includes, in its language), without brand assets.
IReadOnlyList<string> BoundFields(string html, ClauseStore? clauses, DocumentLanguage language)
{
	var sources = new List<string> { html };
	if (clauses is not null) sources.AddRange(clauses.Used(html, language).Where(u => u.Version is not null).Select(u => u.Version!.Html));
	return sources.SelectMany(FieldUsage.References).Select(r => r.Path)
		.Where(p => p != "brand" && !p.StartsWith("brand.", StringComparison.Ordinal))
		.Distinct().Order(StringComparer.Ordinal).ToList();
}

// Publish (go-live or rollback), audited. With Security:RequireSecondPersonToPublish the person who saved a version
// can't publish it themselves.
async Task<IResult> PublishVersionAsync(string kind, string name, int version, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit, DocumentLanguage? language = null)
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var who = SecurityOptions.NameOf(user);
	var languageName = LanguageDetail(language);
	if (security.Enabled && security.RequireSecondPersonToPublish &&
		await audit.LastSavedByAsync(kind, name, version, languageName) is { } savedBy && string.Equals(savedBy, who, StringComparison.OrdinalIgnoreCase))
	{
		await audit.AppendAsync(who, "access.denied", kind, name, version, string.Join("; ", new[] { languageName, "publish own changes" }.OfType<string>()));
		return Results.Json(new { error = $"Someone else must publish v{version}: you saved it." }, statusCode: StatusCodes.Status403Forbidden);
	}
	var previous = (await store.GetPublishedAsync(name))?.Version;
	var published = await store.PublishAsync(name, version);
	if (published is null) return Results.NotFound();
	if (previous != version)
	{
		await audit.AppendAsync(who, previous > version ? "version.rolled-back" : "version.published", kind, name, version,
			string.Join("; ", new[] { languageName, previous is null ? null : $"v{previous} retired" }.OfType<string>()) is { Length: > 0 } detail ? detail : null);
	}
	return Results.Ok(published);
}

async Task<IResult> DiscardDraftAsync(string kind, string name, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit, DocumentLanguage? language = null)
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var draft = (await store.ListVersionsAsync(name)).LastOrDefault();
	if (!await store.DiscardDraftAsync(name)) return Results.NotFound();
	await audit.AppendAsync(SecurityOptions.NameOf(user), "draft.discarded", kind, name, draft?.Version, LanguageDetail(language));
	return Results.NoContent();
}

// Human review (review.html): each case's reference PDF beside the HTML render, approved or rejected by a person.
// ?source=ghostdraft (default): GhostDraft golden cases; the golden-snapshot tests honour the decision while the render
// is unchanged. ?source=import: PDF / Word imports from the import proof run that scored low.
IResult UnknownSource() => Results.BadRequest(new { error = "Unknown review source." });

app.MapGet("/api/review/cases", async (string? source, ReviewStores reviews) =>
	reviews.Get(source) is { } review ? Results.Ok(await review.ListAsync()) : UnknownSource());

// Add to Designer Library: the designer conversion (gd2designer JSON: components, css, sample model) of a GhostDraft
// case. The designer imports it and saves it as a draft template (designer: /?addToLibrary={case}).
app.MapGet("/api/review/cases/{name}/designer", (string name, ReviewStores reviews) =>
	!reviews.GhostDraft.HasCase(name) ? Results.NotFound(new { error = $"'{name}' is not a GhostDraft review case." })
	: reviews.Conversions.PathFor(name) is { } path ? Results.File(path, "application/json")
	: Results.NotFound(new { error = $"No designer conversion of '{name}' was found (tools/gd2designer.py output in output/ghostdraft/batch)." }));

app.MapGet("/api/review/cases/{name}/{artifact}", (string name, string artifact, string? source, ReviewStores reviews) =>
	reviews.Get(source) is not { } review ? UnknownSource()
	: review.Artifact(name, artifact) is { } file ? Results.File(file.Path, file.ContentType)
	: Results.NotFound());

app.MapPut("/api/review/cases/{name}", async (string name, string? source, ReviewRequest request, ReviewStores reviews) =>
	reviews.Get(source) is not { } review ? UnknownSource()
	: await review.DecideAsync(name, request) is { } decision ? Results.Ok(decision)
	: Results.NotFound()).RequireAuthorization(DesignerRoles.CanComment);

app.MapDelete("/api/review/cases/{name}", async (string name, string? source, ReviewStores reviews) =>
	reviews.Get(source) is not { } review ? UnknownSource()
	: await review.ClearAsync(name) ? Results.NoContent()
	: Results.NotFound()).RequireAuthorization(DesignerRoles.CanComment);

app.MapGet("/api/templates", async (TemplateStore store) => Results.Ok(await store.ListAsync()));

app.MapGet("/api/templates/{name}/versions", async (string name, string? lang, TemplateStore store) =>
	!TemplateStore.IsValidName(name) ? InvalidName()
	: DocumentLanguages.Find(lang) is null ? UnknownLanguage(lang)
	: Results.Ok(await store.Language(lang).ListVersionsAsync(name)));

app.MapGet("/api/templates/{name}/versions/{version:int}", async (string name, int version, string? lang, TemplateStore store) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is null) return UnknownLanguage(lang);
	var template = await store.Language(lang).GetAsync(name, version);
	return template is null ? Results.NotFound() : Results.Ok(template);
});

// Save = overwrite the current draft, or start a new draft version if the newest one is Published/Retired.
// ?lang=es saves the Spanish version: it needs the English one (whose data fields it shares) and its HTML is marked with
// its language, so it prints in Spanish wherever it is rendered.
app.MapPut("/api/templates/{name}/draft", async (string name, string? lang, SaveTemplateRequest request, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is not { } language) return UnknownLanguage(lang);
	if (request.Model is { } model && model.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
	{
		return Results.BadRequest(new { error = "The data model must be a JSON object." });
	}
	if (language != DocumentLanguages.English && !store.Exists(name))
	{
		return Results.Conflict(new { error = $"Save the English version of '{name}' first: the {language.Name} version uses its data fields." });
	}
	if (request.Details?.Problem() is { } detailsProblem) return Results.BadRequest(new { error = detailsProblem });
	var saved = await store.Language(lang).SaveDraftAsync(name, request.Project, DocumentLanguages.WithMarker(request.Html, language), request.Css, request.Model,
		request.Details?.Normalized());
	await audit.AppendAsync(SecurityOptions.NameOf(user), "draft.saved", "templates", name, saved.Version, LanguageDetail(language));
	return Results.Ok(saved);
}).RequireAuthorization(DesignerRoles.CanEdit);

app.MapDelete("/api/templates/{name}/draft", (string name, string? lang, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
	DocumentLanguages.Find(lang) is not { } language ? Task.FromResult(UnknownLanguage(lang))
	: DiscardDraftAsync("templates", name, store.Language(lang), user, audit, language)).RequireAuthorization(DesignerRoles.CanEdit);

// Publishing a Draft makes it live; publishing a Retired version is a rollback. The old Published version is Retired.
app.MapPost("/api/templates/{name}/versions/{version:int}/publish", (string name, int version, string? lang, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
	DocumentLanguages.Find(lang) is not { } language ? Task.FromResult(UnknownLanguage(lang))
	: PublishVersionAsync("templates", name, version, store.Language(lang), user, audit, language)).RequireAuthorization(DesignerRoles.CanPublish);

// Shared clauses: reusable wording/layout with the same Draft -> Published -> Retired versions as templates. Templates
// include them with {% include 'name' %} (the published version) or {% include 'name@3' %} (pinned).
app.MapGet("/api/clauses", async (ClauseStore clauses) => Results.Ok(await clauses.Versions.ListAsync()));

app.MapGet("/api/clauses/{name}/versions", async (string name, string? lang, ClauseStore clauses) =>
	!TemplateStore.IsValidName(name) ? InvalidName()
	: DocumentLanguages.Find(lang) is null ? UnknownLanguage(lang)
	: Results.Ok(await clauses.Versions.Language(lang).ListVersionsAsync(name)));

app.MapGet("/api/clauses/{name}/versions/{version:int}", async (string name, int version, string? lang, ClauseStore clauses) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is null) return UnknownLanguage(lang);
	var clause = await clauses.Versions.Language(lang).GetAsync(name, version);
	return clause is null ? Results.NotFound() : Results.Ok(clause);
});

// What a template's include renders (published, or ?version=N pinned): the designer shows it on the canvas. ?lang=es: what
// a Spanish template prints (the Spanish clause when it has one that can be used, else the English one; see "language").
app.MapGet("/api/clauses/{name}/content", (string name, int? version, string? lang, ClauseStore clauses) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is not { } language) return UnknownLanguage(lang);
	return clauses.ResolveIn(new ClauseReference(name, version), language) is { Version: { } clause } resolved
		? Results.Ok(new { name, clause.Version, clause.Status, clause.Html, clause.Css, language = resolved.Language.Code })
		: Results.NotFound(new { error = version is null ? $"Clause '{name}' has no published version." : $"Clause '{name}' has no version {version} that can be used." });
});

app.MapPut("/api/clauses/{name}/draft", async (string name, string? lang, SaveTemplateRequest request, ClauseStore clauses, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is not { } language) return UnknownLanguage(lang);
	if (request.Model is { } model && model.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
	{
		return Results.BadRequest(new { error = "The data model must be a JSON object." });
	}
	if (clauses.IncludesItself(name, request.Html))
	{
		return Results.BadRequest(new { error = $"Clause '{name}' can't include itself (directly or through another clause)." });
	}
	if (language != DocumentLanguages.English && !clauses.Versions.Exists(name))
	{
		return Results.Conflict(new { error = $"Save the English version of clause '{name}' first: the {language.Name} version uses its data fields." });
	}
	// A clause prints in the language of the template it is placed in: it carries no language marker of its own.
	var saved = await clauses.Versions.Language(lang).SaveDraftAsync(name, request.Project, DocumentLanguages.WithoutMarker(request.Html), request.Css, request.Model);
	await audit.AppendAsync(SecurityOptions.NameOf(user), "draft.saved", "clauses", name, saved.Version, LanguageDetail(language));
	return Results.Ok(saved);
}).RequireAuthorization(DesignerRoles.CanEdit);

app.MapDelete("/api/clauses/{name}/draft", (string name, string? lang, ClauseStore clauses, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
	DocumentLanguages.Find(lang) is not { } language ? Task.FromResult(UnknownLanguage(lang))
	: DiscardDraftAsync("clauses", name, clauses.Versions.Language(lang), user, audit, language)).RequireAuthorization(DesignerRoles.CanEdit);

app.MapPost("/api/clauses/{name}/versions/{version:int}/publish", (string name, int version, string? lang, ClauseStore clauses, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
	DocumentLanguages.Find(lang) is not { } language ? Task.FromResult(UnknownLanguage(lang))
	: PublishVersionAsync("clauses", name, version, clauses.Versions.Language(lang), user, audit, language)).RequireAuthorization(DesignerRoles.CanPublish);

// The languages of a template or clause: versions per language and, for each translation, how its data fields compare
// with the English version's (newest versions; fields of included clauses count too) and which included clauses still
// print in English because they have no usable translation.
app.MapGet("/api/{kind}/{name}/languages", async (string kind, string name, TemplateStore templates, ClauseStore clauses) =>
{
	if (kind is not ("templates" or "clauses")) return Results.NotFound();
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var store = kind == "clauses" ? clauses.Versions : templates;
	var english = await store.GetAsync(name);
	var englishFields = english is null ? [] : BoundFields(english.Html, clauses, DocumentLanguages.English);
	var result = new List<LanguageVariantInfo>();
	foreach (var language in DocumentLanguages.All)
	{
		var languageStore = store.Language(language.Code);
		var versions = await languageStore.ListVersionsAsync(name);
		IReadOnlyList<string>? missing = null, extra = null, englishClauses = null;
		if (language != DocumentLanguages.English && versions.Count > 0 && english is not null && await languageStore.GetAsync(name) is { } newest)
		{
			var fields = BoundFields(newest.Html, clauses, language);
			missing = englishFields.Except(fields).ToList();
			extra = fields.Except(englishFields).ToList();
			englishClauses = clauses.Used(newest.Html, language)
				.Where(u => u.Version is not null && u.Language != language).Select(u => u.Reference.Name).Distinct().ToList();
		}
		result.Add(new LanguageVariantInfo(language.Code, language.Name, language.Culture, language == DocumentLanguages.English,
			versions.LastOrDefault()?.Version, versions.FirstOrDefault(v => v.Status == TemplateStatus.Published)?.Version,
			missing, extra, englishClauses));
	}
	return Results.Ok(result);
});

// Which templates use a clause (any version that includes it, published or pinned): what a clause change affects.
app.MapGet("/api/clauses/{name}/usage", async (string name, TemplateStore templates) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var usage = new List<object>();
	foreach (var summary in await templates.ListAsync())
	{
		// translations include clauses too (language: null = English)
		foreach (var language in DocumentLanguages.All)
		{
			var store = templates.Language(language.Code);
			foreach (var info in await store.ListVersionsAsync(summary.Name))
			{
				var template = await store.GetAsync(summary.Name, info.Version);
				var references = ClauseStore.References(template!.Html).Where(r => r.Name == name).Select(r => r.Version).ToList();
				if (references.Count == 0) continue;
				usage.Add(new
				{
					template = summary.Name, info.Version, info.Status, pinned = references.OfType<int>().Distinct().ToList(), latest = references.Contains(null),
					language = language == DocumentLanguages.English ? null : language.Code
				});
			}
		}
	}
	return Results.Ok(usage);
});

// Field usage: which templates / clauses read a data path ("policy.number", "locations[].name", or a parent such as
// "policy"). Loop aliases resolve to their lists. The designer's unsaved canvas can be included as "html".
app.MapPost("/api/usage/fields", async (FieldUsageRequest request, FieldUsageSearch search) =>
{
	var query = request.Path?.Trim();
	if (!FieldUsage.IsValidQuery(query))
	{
		return Results.BadRequest(new { error = "Enter a data path such as policy.number or locations[].name." });
	}
	if (request.Kind is not (null or "templates" or "clauses"))
	{
		return Results.BadRequest(new { error = "Kind must be templates or clauses." });
	}
	var documents = await search.SearchAsync(query!, request.Kind, request.AllVersions);
	var canvas = request.Html is null ? null
		: FieldUsageSearch.Count(FieldUsage.References(request.Html).Where(r => FieldUsage.Matches(r, query!)));
	return Results.Ok(new { path = query, documents, canvas });
});

// Every path the templates and clauses use, with how many documents use it (search suggestions, unused-field checks).

// Calculated fields: the designer compiles an author's expression to Liquid here (one implementation, tested in C#),
// and previews it with test data through the same Liquid engine the documents use.
app.MapPost("/api/expressions/compile", (CompileExpressionRequest request) =>
{
	try
	{
		return Results.Ok(ExpressionCompiler.Compile(request.Expression, request.Lists));
	}
	catch (ExpressionException ex)
	{
		return Results.BadRequest(new { error = ex.Message, position = ex.Position });
	}
});

app.MapPost("/api/expressions/preview", async (PreviewExpressionRequest request, DocumentComposer composer) =>
{
	if (string.IsNullOrEmpty(request.Liquid) || request.Liquid.Length > 20_000)
	{
		return Results.BadRequest(new { error = "Nothing to preview." });
	}
	var data = request.Data is { ValueKind: JsonValueKind.Object } given ? given : await LoadSampleDataAsync();
	var (text, error) = await composer.RenderTextAsync(request.Liquid, data);
	return error is null ? Results.Ok(new { text }) : Results.BadRequest(new { error });
});

// Custom formats: what values look like with a format or mask (canvas samples, the format builder), or why it isn't valid.
app.MapGet("/api/formats/cultures", () => Results.Ok(ValueFormats.Cultures));

app.MapPost("/api/formats/preview", (FormatPreviewRequest request) =>
{
	var items = request.Items ?? [];
	if (items.Length > 200) return Results.BadRequest(new { error = "At most 200 values at a time." });
	return Results.Ok(items.Select(item =>
	{
		var problem = item.Mask is not null ? ValueFormats.MaskProblem(item.Mask)
			: ValueFormats.FormatProblem(item.Format) ?? ValueFormats.CultureProblem(item.Culture);
		if (problem is not null) return new FormatPreview(null, problem);
		var value = item.Value ?? default;
		if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return new FormatPreview(string.Empty, null);
		if (item.Mask is not null)
		{
			return new FormatPreview(ValueFormats.Mask(value.ValueKind == JsonValueKind.Number ? value.GetDecimal().ToString("0", System.Globalization.CultureInfo.InvariantCulture) : value.ToString(), item.Mask), null);
		}
		return value.ValueKind == JsonValueKind.Number
			? new FormatPreview(ValueFormats.FormatNumber(value.GetDecimal(), item.Format!, item.Culture), null)
			: new FormatPreview(ValueFormats.FormatText(value.ToString(), item.Format!, item.Culture), null);
	}).ToList());
});

// Spell check: the designer sends the words of the document text; the misspelled ones come back with suggestions.
app.MapPost("/api/spelling/check", (SpellCheckRequest request, SpellChecker spelling) =>
{
	var words = request.Words ?? [];
	if (words.Length > SpellChecker.MaxWords)
	{
		return Results.BadRequest(new { error = $"Send at most {SpellChecker.MaxWords} words at a time." });
	}
	return Results.Ok(spelling.Check(words.Where(SpellChecker.IsWord)));
});

app.MapGet("/api/spelling/words", (SpellChecker spelling) => Results.Ok(spelling.CustomWords()));

app.MapPost("/api/spelling/words", async (AddWordRequest request, SpellChecker spelling, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	var word = request.Word?.Trim();
	if (!SpellChecker.IsWord(word)) return Results.BadRequest(new { error = "Only single words (letters, ' and -) can be added." });
	if (await spelling.AddWordAsync(word!)) await audit.AppendAsync(SecurityOptions.NameOf(user), "dictionary.word-added", detail: word);
	return Results.Ok(new { word });
}).RequireAuthorization(DesignerRoles.CanEdit);

// Reviewer comments on template / clause elements (kept across versions; never printed).
IResult? InvalidCommentTarget(string kind, string name) =>
	!ScenarioStore.IsValidKind(kind) ? Results.NotFound() : !TemplateStore.IsValidName(name) ? InvalidName() : null;

// With sign-in on, comments are by the signed-in person whatever name the page sends.
string? CommentAuthor(System.Security.Claims.ClaimsPrincipal user, string? given) => security.Enabled ? SecurityOptions.NameOf(user) : given;

IResult? InvalidCommentText(string? author, string? text) =>
	string.IsNullOrWhiteSpace(author) || author.Trim().Length > CommentStore.MaxAuthorLength
		? Results.BadRequest(new { error = $"Enter your name (up to {CommentStore.MaxAuthorLength} characters)." })
	: string.IsNullOrWhiteSpace(text) || text.Trim().Length > CommentStore.MaxTextLength
		? Results.BadRequest(new { error = $"Write a comment of up to {CommentStore.MaxTextLength} characters." })
	: null;

app.MapGet("/api/{kind}/{name}/comments", async (string kind, string name, CommentStore comments) =>
	InvalidCommentTarget(kind, name) ?? Results.Ok(await comments.ListAsync(kind, name)));

app.MapPost("/api/{kind}/{name}/comments", async (string kind, string name, AddCommentRequest request, CommentStore comments, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	var author = CommentAuthor(user, request.Author);
	if ((InvalidCommentTarget(kind, name) ?? InvalidCommentText(author, request.Text)) is { } invalid) return invalid;
	if (request.Anchor is null || !System.Text.RegularExpressions.Regex.IsMatch(request.Anchor, "^[a-z0-9]{6,24}$"))
	{
		return Results.BadRequest(new { error = "Select an element to comment on." });
	}
	var element = (request.Element ?? string.Empty).Trim();
	if (element.Length > 120) element = element[..120];
	var path = request.Path ?? string.Empty;
	if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^(\d{1,4}(\.\d{1,4}){0,40})?$")) path = string.Empty;
	try
	{
		var comment = await comments.AddAsync(kind, name, request.Anchor, path, element, author!.Trim(), request.Text!.Trim());
		await audit.AppendAsync(author.Trim(), "comment.added", kind, name, detail: comment.Id);
		return Results.Ok(comment);
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
}).RequireAuthorization(DesignerRoles.CanComment);

app.MapPost("/api/{kind}/{name}/comments/{id}/replies", async (string kind, string name, string id, ReplyRequest request, CommentStore comments, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	var author = CommentAuthor(user, request.Author);
	if ((InvalidCommentTarget(kind, name) ?? InvalidCommentText(author, request.Text)) is { } invalid) return invalid;
	try
	{
		if (await comments.ReplyAsync(kind, name, id, author!.Trim(), request.Text!.Trim()) is not { } comment) return Results.NotFound();
		await audit.AppendAsync(author.Trim(), "comment.replied", kind, name, detail: id);
		return Results.Ok(comment);
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
}).RequireAuthorization(DesignerRoles.CanComment);

app.MapPost("/api/{kind}/{name}/comments/{id}/resolved", async (string kind, string name, string id, ResolveRequest request, CommentStore comments, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (InvalidCommentTarget(kind, name) is { } invalid) return invalid;
	var by = CommentAuthor(user, request.Author)?.Trim();
	if (by is { Length: > CommentStore.MaxAuthorLength }) by = by[..CommentStore.MaxAuthorLength];
	if (await comments.ResolveAsync(kind, name, id, request.Resolved, by) is not { } comment) return Results.NotFound();
	await audit.AppendAsync(SecurityOptions.NameOf(user), request.Resolved ? "comment.resolved" : "comment.reopened", kind, name, detail: id);
	return Results.Ok(comment);
}).RequireAuthorization(DesignerRoles.CanComment);

app.MapDelete("/api/{kind}/{name}/comments/{id}", async (string kind, string name, string id, CommentStore comments, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (InvalidCommentTarget(kind, name) is { } invalid) return invalid;
	if (!await comments.DeleteAsync(kind, name, id)) return Results.NotFound();
	await audit.AppendAsync(SecurityOptions.NameOf(user), "comment.deleted", kind, name, detail: id);
	return Results.NoContent();
}).RequireAuthorization(DesignerRoles.CanComment);

// Template gallery: every template and clause with its versions, test data and open comments.
app.MapGet("/api/gallery", async (string? kind, Gallery gallery) =>
	kind is not (null or "templates" or "clauses") ? Results.BadRequest(new { error = "Kind must be templates or clauses." })
	: Results.Ok(await gallery.ListAsync(kind)));

// A rendered page for gallery thumbnails: the version (default published, else newest) with its own example data.
// Composed HTML is sanitized (no scripts); the CSP and the gallery's sandboxed iframe make sure nothing runs or loads.
app.MapGet("/api/{kind}/{name}/preview.html", async (string kind, string name, int? version, HttpResponse response, Gallery gallery, DocumentComposer composer) =>
{
	if (!ScenarioStore.IsValidKind(kind)) return Results.NotFound();
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var template = await gallery.ResolveAsync(kind, name, version);
	if (template is null) return Results.NotFound();
	var data = template.Model is { ValueKind: JsonValueKind.Object } model ? model : await LoadSampleDataAsync();
	var composed = await composer.ComposeAsync(template.Html, template.Css, data);
	response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:";
	response.Headers.XContentTypeOptions = "nosniff";
	return Results.Content(composed.Html
		?? "<!DOCTYPE html><html><body><p style=\"font:14px sans-serif;color:#9b1c1c\">" + System.Net.WebUtility.HtmlEncode(composed.Error) + "</p></body></html>",
		"text/html");
});

// Duplicate: a version of a template / clause becomes draft v1 of a new one (with its test scenarios if asked).
app.MapPost("/api/{kind}/{name}/duplicate", async (string kind, string name, DuplicateRequest request, Gallery gallery, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!ScenarioStore.IsValidKind(kind)) return Results.NotFound();
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	var newName = request.NewName?.Trim() ?? string.Empty;
	if (!TemplateStore.IsValidName(newName))
	{
		return Results.BadRequest(new { error = "The new name may only contain letters, numbers, \"-\" and \"_\" (at most 64)." });
	}
	var (problem, source, copy, scenariosCopied, languagesCopied) = await gallery.DuplicateAsync(kind, name, newName, request.Version, request.IncludeScenarios);
	if (problem == DuplicateProblem.None)
	{
		await audit.AppendAsync(SecurityOptions.NameOf(user), "template.duplicated", kind, newName, copy!.Version,
			$"from {name} v{source!.Version}" + (languagesCopied.Count > 0 ? " with " + string.Join(", ", languagesCopied.Select(c => DocumentLanguages.Find(c)!.Name)) : ""));
	}
	return problem switch
	{
		DuplicateProblem.SourceNotFound => Results.NotFound(new { error = $"'{name}' has no {(request.Version is null ? "saved" : "v" + request.Version)} version to copy." }),
		DuplicateProblem.NameTaken => Results.Conflict(new { error = $"'{newName}' already exists. Choose another name." }),
		_ => Results.Ok(new { name = newName, copy!.Version, copy.Status, from = new { name, source!.Version, source.Status }, scenariosCopied, languagesCopied })
	};
}).RequireAuthorization(DesignerRoles.CanEdit);

// Custom blocks: selections saved from the designer to reuse in any template (copied in when dropped, not linked).
app.MapGet("/api/blocks", async (BlockStore blocks) => Results.Ok(await blocks.ListAsync()));

app.MapPut("/api/blocks/{name}", async (string name, SaveBlockRequest request, BlockStore blocks, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!TemplateStore.IsValidName(name)) return Results.BadRequest(new { error = "Invalid block name." });
	var label = request.Label?.Trim() ?? string.Empty;
	if (label.Length is 0 or > BlockStore.MaxLabelLength)
	{
		return Results.BadRequest(new { error = $"Give the block a name of up to {BlockStore.MaxLabelLength} characters." });
	}
	if (request.Category is { Length: > 40 })
	{
		return Results.BadRequest(new { error = "Category names are at most 40 characters." });
	}
	if (request.Components is not { ValueKind: JsonValueKind.Object or JsonValueKind.Array } components)
	{
		return Results.BadRequest(new { error = "The block has no content." });
	}
	if (components.GetRawText().Length + (request.Css?.Length ?? 0) > 1024 * 1024)
	{
		return Results.BadRequest(new { error = "The block is too large (max 1 MB)." });
	}
	try
	{
		var saved = await blocks.SaveAsync(name, label, request.Category, components, request.Css);
		await audit.AppendAsync(SecurityOptions.NameOf(user), "block.saved", detail: name);
		return Results.Ok(saved);
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
}).RequireAuthorization(DesignerRoles.CanEdit);

app.MapDelete("/api/blocks/{name}", async (string name, BlockStore blocks, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!TemplateStore.IsValidName(name)) return Results.BadRequest(new { error = "Invalid block name." });
	if (!await blocks.DeleteAsync(name)) return Results.NotFound();
	await audit.AppendAsync(SecurityOptions.NameOf(user), "block.deleted", detail: name);
	return Results.NoContent();
}).RequireAuthorization(DesignerRoles.CanEdit);

// Brand themes: color/font overrides of the brand stylesheet per brand or line of business, chosen per template.
// Changing a theme changes every template that uses it straight away, so saving and deleting need the Publisher role.
async Task<List<object>> TemplatesUsingThemeAsync(string theme, TemplateStore store)
{
	var users = new List<object>();
	foreach (var summary in await store.ListAsync())
	{
		var versions = new List<object>();
		foreach (var version in new[] { await store.GetPublishedAsync(summary.Name), await store.GetAsync(summary.Name) }.OfType<TemplateVersion>().DistinctBy(v => v.Version))
		{
			if (string.Equals(DocumentComposer.ThemeOf(version.Html), theme, StringComparison.Ordinal))
			{
				versions.Add(new { version.Version, status = version.Status.ToString() });
			}
		}
		if (versions.Count > 0) users.Add(new { template = summary.Name, versions });
	}
	return users;
}

app.MapGet("/api/themes", async (FaCT.DocDesigner.POC.Themes.ThemeStore themes) => Results.Ok(new
{
	standard = FaCT.DocDesigner.POC.Themes.ThemeStore.StandardTheme,
	tokens = themes.ColorTokens,
	systemFonts = FaCT.DocDesigner.POC.Themes.ThemeStore.SystemFonts,
	themes = await themes.ListAsync(),
	fonts = await themes.ListFontsAsync()
}));

app.MapGet("/api/themes/{name}", async (string name, FaCT.DocDesigner.POC.Themes.ThemeStore themes) =>
	await themes.GetAsync(name) is { } theme ? Results.Ok(theme) : Results.NotFound());

// Used by the designer canvas (and anyone who wants to see what a theme does to the brand stylesheet).
app.MapGet("/api/themes/{name}/theme.css", async (string name, FaCT.DocDesigner.POC.Themes.ThemeStore themes) =>
	await themes.CssAsync(name) is { } css ? Results.Text(css, "text/css") : Results.NotFound());

app.MapGet("/api/themes/{name}/usage", async (string name, TemplateStore store) =>
	TemplateStore.IsValidName(name) ? Results.Ok(await TemplatesUsingThemeAsync(name, store)) : InvalidName());

app.MapPut("/api/themes/{name}", async (string name, FaCT.DocDesigner.POC.Themes.ThemeInput input, FaCT.DocDesigner.POC.Themes.ThemeStore themes, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	var (theme, error) = await themes.SaveAsync(name, input, SecurityOptions.NameOf(user));
	if (theme is null) return Results.BadRequest(new { error });
	await audit.AppendAsync(SecurityOptions.NameOf(user), "theme.saved", detail: name);
	return Results.Ok(theme);
}).RequireAuthorization(DesignerRoles.CanPublish);

app.MapDelete("/api/themes/{name}", async (string name, FaCT.DocDesigner.POC.Themes.ThemeStore themes, TemplateStore store, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (!FaCT.DocDesigner.POC.Themes.ThemeStore.IsValidName(name)) return Results.BadRequest(new { error = "Invalid theme name." });
	if (await themes.GetAsync(name) is null) return Results.NotFound();
	var usedBy = await TemplatesUsingThemeAsync(name, store);
	if (usedBy.Count > 0)
	{
		return Results.Conflict(new { error = $"Templates still use this theme: choose another theme for them first.", usedBy });
	}
	await themes.DeleteAsync(name);
	await audit.AppendAsync(SecurityOptions.NameOf(user), "theme.deleted", detail: name);
	return Results.NoContent();
}).RequireAuthorization(DesignerRoles.CanPublish);

// Font upload: the raw file as the body (application/octet-stream or font/*), family / weight / style in the query.
app.MapPost("/api/themes/fonts", async (HttpRequest request, string? family, int? weight, string? style, FaCT.DocDesigner.POC.Themes.ThemeStore themes, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	var type = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
	if (type != "application/octet-stream" && !type.StartsWith("font/", StringComparison.Ordinal))
	{
		return Results.BadRequest(new { error = "Send the font file itself (application/octet-stream)." });
	}
	if (request.ContentLength > FaCT.DocDesigner.POC.Themes.ThemeStore.MaxFontBytes)
	{
		return Results.BadRequest(new { error = $"The font file must be at most {FaCT.DocDesigner.POC.Themes.ThemeStore.MaxFontBytes / 1024 / 1024} MB." });
	}
	using var buffer = new MemoryStream();
	var chunk = new byte[81920];
	int read;
	while ((read = await request.Body.ReadAsync(chunk)) > 0)
	{
		if (buffer.Length + read > FaCT.DocDesigner.POC.Themes.ThemeStore.MaxFontBytes)
		{
			return Results.BadRequest(new { error = $"The font file must be at most {FaCT.DocDesigner.POC.Themes.ThemeStore.MaxFontBytes / 1024 / 1024} MB." });
		}
		buffer.Write(chunk, 0, read);
	}
	var (font, error) = await themes.AddFontAsync(buffer.ToArray(), family, weight ?? 400, style, SecurityOptions.NameOf(user));
	if (font is null) return Results.BadRequest(new { error });
	await audit.AppendAsync(SecurityOptions.NameOf(user), "font.uploaded", detail: $"{font.Family} {font.Weight} {font.Style}");
	return Results.Ok(font);
}).RequireAuthorization(DesignerRoles.CanPublish);

app.MapDelete("/api/themes/fonts/{id}", async (string id, FaCT.DocDesigner.POC.Themes.ThemeStore themes, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (await themes.GetFontAsync(id) is not { } font) return Results.NotFound();
	var sameFamily = (await themes.ListFontsAsync()).Count(f => f.Family == font.Family);
	var usedBy = await themes.ThemesUsingFamilyAsync(font.Family);
	if (sameFamily == 1 && usedBy.Count > 0)
	{
		return Results.Conflict(new { error = $"Themes still use '{font.Family}': choose another font for them first.", usedBy });
	}
	await themes.DeleteFontAsync(id);
	await audit.AppendAsync(SecurityOptions.NameOf(user), "font.deleted", detail: $"{font.Family} {font.Weight} {font.Style}");
	return Results.NoContent();
}).RequireAuthorization(DesignerRoles.CanPublish);

// Every path the templates and clauses use, with how many documents use it (search suggestions, unused-field checks).
app.MapGet("/api/usage/fields", async (bool? allVersions, FieldUsageSearch search) => Results.Ok(await search.IndexAsync(allVersions ?? false)));

// What fact-docgen would do when a GenerateCustomDocumentMessage arrives:
// load the PUBLISHED template for the document kind, merge the message data, render the PDF.
// ?version=N renders a specific version (e.g. to proof a draft): a draft is stamped DRAFT so the proof can't pass for
// the real thing. ?watermark=SPECIMEN stamps any word instead (specimen copies for agents or filings).
// Body is optional; without one the version's own
// data model (example payload) is used, falling back to the sample data.
// ?lang=es renders the Spanish version. When Spanish has no published version the English one is rendered instead, and
// the X-Template-Language header says so ("en; requested es"), so a document always goes out.
app.MapPost("/api/templates/{name}/pdf", async (
	string name,
	int? version,
	string? watermark,
	string? lang,
	HttpRequest request,
	TemplateStore store,
	DocumentComposer composer,
	PdfRenderer pdf) =>
{
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is not { } language) return UnknownLanguage(lang);
	if (!string.IsNullOrWhiteSpace(watermark) && !DocumentComposer.IsValidStamp(watermark))
	{
		return Results.BadRequest(new { error = DocumentComposer.StampRule });
	}
	var languageStore = store.Language(lang);
	var template = version is null ? await languageStore.GetPublishedAsync(name) : await languageStore.GetAsync(name, version);
	var printed = language;
	if (template is null && version is null && language != DocumentLanguages.English && await store.GetPublishedAsync(name) is { } english)
	{
		template = english;
		printed = DocumentLanguages.English;
	}
	if (template is null)
	{
		return version is null
			? Results.Conflict(new { error = $"Template '{name}' has no published version." })
			: Results.NotFound();
	}

	JsonElement? data = request.HasJsonContentType() && request.ContentLength is not 0
		? await request.ReadFromJsonAsync<JsonElement>()
		: null;
	var payload = data is { ValueKind: JsonValueKind.Object } ? data.Value
		: template.Model is { ValueKind: JsonValueKind.Object } model ? model
		: await LoadSampleDataAsync();

	var stamp = !string.IsNullOrWhiteSpace(watermark) ? watermark.Trim()
		: template.Status == TemplateStatus.Draft ? "DRAFT"
		: null;
	var result = await RenderPdfAsync(template.Html, template.Css, payload, composer, pdf, stamp);
	request.HttpContext.Response.Headers["X-Template-Version"] = $"{template.Version} ({template.Status})";
	request.HttpContext.Response.Headers["X-Template-Language"] = printed == language ? printed.Code : $"{printed.Code}; requested {language.Code}";
	if (stamp is not null) request.HttpContext.Response.Headers["X-Watermark"] = stamp;
	return result;
});

// Test-data scenarios per template / clause: named payloads to preview the document with (saved straight away).
const int MaxScenarioBytes = 2 * 1024 * 1024;

IResult? InvalidScenarioTarget(string kind, string name, string? scenario = null) =>
	!ScenarioStore.IsValidKind(kind) ? Results.NotFound()
	: !TemplateStore.IsValidName(name) ? InvalidName()
	: scenario is not null && !ScenarioStore.IsValidScenarioName(scenario)
		? Results.BadRequest(new { error = "Scenario names may contain letters, numbers, spaces, \"-\" and \"_\" (at most 64)." })
	: null;

app.MapGet("/api/{kind}/{name}/scenarios", async (string kind, string name, ScenarioStore scenarios) =>
	InvalidScenarioTarget(kind, name) ?? Results.Ok(await scenarios.ListAsync(kind, name)));

app.MapPut("/api/{kind}/{name}/scenarios/{scenario}", async (string kind, string name, string scenario, SaveScenarioRequest request, ScenarioStore scenarios, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (InvalidScenarioTarget(kind, name, scenario) is { } invalid) return invalid;
	if (request.Data is not { ValueKind: JsonValueKind.Object } data)
	{
		return Results.BadRequest(new { error = "Scenario data must be a JSON object." });
	}
	if (data.GetRawText().Length > MaxScenarioBytes)
	{
		return Results.BadRequest(new { error = "Scenario data is too large (max 2 MB)." });
	}
	try
	{
		var saved = await scenarios.SaveAsync(kind, name, scenario, data);
		await audit.AppendAsync(SecurityOptions.NameOf(user), "scenario.saved", kind, name, detail: scenario);
		return Results.Ok(saved);
	}
	catch (InvalidOperationException ex)
	{
		return Results.BadRequest(new { error = ex.Message });
	}
}).RequireAuthorization(DesignerRoles.CanEdit);

app.MapDelete("/api/{kind}/{name}/scenarios/{scenario}", async (string kind, string name, string scenario, ScenarioStore scenarios, System.Security.Claims.ClaimsPrincipal user, AuditLog audit) =>
{
	if (InvalidScenarioTarget(kind, name, scenario) is { } invalid) return invalid;
	if (!await scenarios.DeleteAsync(kind, name, scenario)) return Results.NotFound();
	await audit.AppendAsync(SecurityOptions.NameOf(user), "scenario.deleted", kind, name, detail: scenario);
	return Results.NoContent();
}).RequireAuthorization(DesignerRoles.CanEdit);

// What publishing would change: the canvas (html/css) against the published version (or ?against=N), as HTML, CSS and
// printed-text diffs plus both PDFs, rendered with the same data (the request's, else the baseline's example data).
app.MapPost("/api/{kind}/{name}/diff", async (
	string kind,
	string name,
	int? against,
	string? lang,
	DiffRequest request,
	TemplateStore templates,
	ClauseStore clauses,
	DocumentComposer composer,
	PdfRenderer pdf) =>
{
	if (kind is not ("templates" or "clauses")) return Results.NotFound();
	if (!TemplateStore.IsValidName(name)) return InvalidName();
	if (DocumentLanguages.Find(lang) is null) return UnknownLanguage(lang);
	var store = (kind == "clauses" ? clauses.Versions : templates).Language(lang);
	var baseline = against is null ? await store.GetPublishedAsync(name) : await store.GetAsync(name, against);
	if (baseline is null)
	{
		return against is null ? Results.Ok(new DiffResult(null, null, null, null, null, null)) : Results.NotFound();
	}

	var data = request.Data is { ValueKind: JsonValueKind.Object } given ? given
		: baseline.Model is { ValueKind: JsonValueKind.Object } model ? model
		: await LoadSampleDataAsync();

	async Task<(DiffSide Side, byte[]? Bytes)> RenderSideAsync(string html, string? css)
	{
		var composed = await composer.ComposeAsync(html, css, data);
		if (composed.Error is not null) return (new DiffSide(composed.Error, 0, null), null);
		var bytes = await pdf.RenderAsync(composed.Html!);
		return (new DiffSide(null, VersionDiff.PageCount(bytes), Convert.ToBase64String(bytes)), bytes);
	}

	var before = await RenderSideAsync(baseline.Html, baseline.Css);
	var after = await RenderSideAsync(request.Html, request.Css);
	var text = before.Bytes is not null && after.Bytes is not null
		? VersionDiff.Lines(VersionDiff.PdfLines(before.Bytes), VersionDiff.PdfLines(after.Bytes))
		: null;
	return Results.Ok(new DiffResult(
		baseline.ToInfo(),
		VersionDiff.Lines(VersionDiff.HtmlLines(baseline.Html), VersionDiff.HtmlLines(request.Html)),
		VersionDiff.Lines(VersionDiff.CssLines(baseline.Css), VersionDiff.CssLines(request.Css ?? string.Empty)),
		text,
		before.Side,
		after.Side));
});

// Preview all: the canvas (html/css) rendered once per scenario. One bad scenario doesn't stop the others.
app.MapPost("/api/render/scenarios", async (RenderScenariosRequest request, DocumentComposer composer, PdfRenderer pdf) =>
{
	const int maxScenarios = 20;
	if (request.Scenarios is not { Count: > 0 and <= maxScenarios } list)
	{
		return Results.BadRequest(new { error = $"Send between 1 and {maxScenarios} scenarios." });
	}
	var results = new List<ScenarioRender>();
	foreach (var scenario in list)
	{
		if (scenario.Data is not { ValueKind: JsonValueKind.Object } data)
		{
			results.Add(new ScenarioRender(scenario.Name, "Scenario data must be a JSON object.", 0, null));
			continue;
		}
		var composed = await composer.ComposeAsync(request.Html, request.Css, data);
		if (composed.Error is not null)
		{
			results.Add(new ScenarioRender(scenario.Name, composed.Error, 0, null));
			continue;
		}
		var bytes = await pdf.RenderAsync(composed.Html!);
		using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
		results.Add(new ScenarioRender(scenario.Name, null, document.NumberOfPages, Convert.ToBase64String(bytes)));
	}
	return Results.Ok(results);
});

app.Run();

internal sealed record DiffRequest(string Html, string? Css, JsonElement? Data);

/// <summary>A language of a template or clause: its versions and, for a translation, how its fields compare with English.</summary>
internal sealed record LanguageVariantInfo(
	string Code,
	string Name,
	string Culture,
	bool IsEnglish,
	int? LatestVersion,
	int? PublishedVersion,
	IReadOnlyList<string>? MissingFields,
	IReadOnlyList<string>? ExtraFields,
	IReadOnlyList<string>? EnglishClauses);

internal sealed record FieldUsageRequest(string? Path, string? Html, string? Kind, bool AllVersions);

internal sealed record SaveBlockRequest(string? Label, string? Category, JsonElement? Components, string? Css);

internal sealed record CompileExpressionRequest(string? Expression, string[]? Lists);

internal sealed record SpellCheckRequest(string[]? Words);

internal sealed record AddWordRequest(string? Word);

internal sealed record AddCommentRequest(string? Anchor, string? Path, string? Element, string? Author, string? Text);

internal sealed record ReplyRequest(string? Author, string? Text);

internal sealed record ResolveRequest(bool Resolved, string? Author);

internal sealed record DuplicateRequest(string? NewName, int? Version, bool IncludeScenarios = true);

internal sealed record SignInRequest(string? Id);

internal sealed record PreviewExpressionRequest(string? Liquid, JsonElement? Data);

internal sealed record FormatPreviewItem(JsonElement? Value, string? Format, string? Culture, string? Mask);

internal sealed record FormatPreviewRequest(FormatPreviewItem[]? Items);

internal sealed record FormatPreview(string? Text, string? Error);

/// <summary>One side of a diff rendered to PDF (or why it couldn't be).</summary>
internal sealed record DiffSide(string? Error, int Pages, string? Pdf);

internal sealed record DiffResult(TemplateVersionInfo? Against, TextDiff? Html, TextDiff? Css, TextDiff? Text, DiffSide? Published, DiffSide? Current)
{
	public bool Identical => Html is { Identical: true } && Css is { Identical: true };
}

internal sealed record RenderRequest(string Html, string? Css, JsonElement? Data, string? Watermark = null);

internal sealed record SaveTemplateRequest(JsonElement Project, string Html, string Css, JsonElement? Model, TemplateDetails? Details = null);

internal sealed record SaveScenarioRequest(JsonElement? Data);

internal sealed record ScenarioData(string Name, JsonElement? Data);

internal sealed record RenderScenariosRequest(string Html, string? Css, List<ScenarioData>? Scenarios);

internal sealed record ScenarioRender(string Name, string? Error, int Pages, string? Pdf);

internal sealed record MappingRequest(string[]? Fields, MappingPath[]? Paths, string?[]? Labels = null);

// Visible to the integration tests (WebApplicationFactory).
public partial class Program;
