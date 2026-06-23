using System.Text.Json;
using MoE.GhostDraftDataModel.SDK;

namespace FapPdfTools.Population;

/// <summary>
/// Fetches a policy from the Commercial API in its Common Data Model shape and
/// deserializes it into a <see cref="CDMPolicyView"/> ready for form population.
/// Wraps <c>GET {baseUrl}/api/policy/{policyNumber}</c>.
/// </summary>
public sealed class CommercialApiPolicyClient : IDisposable
{
	// Mirrors the API's DefaultJsonSerializerOptions (camelCase, case-insensitive).
	// Party subtypes (InsuredParty/AgencyParty) carry [JsonPolymorphic] $type
	// discriminators, so they round-trip without extra configuration.
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true
	};

	private readonly HttpClient _http;
	private readonly bool _ownsClient;

	/// <summary>Create a client that owns its own <see cref="HttpClient"/>.</summary>
	/// <param name="baseUrl">API root, e.g. <c>https://commercialcoresttst.azurewebsites.net</c>.</param>
	public CommercialApiPolicyClient(string baseUrl)
	{
		if (string.IsNullOrWhiteSpace(baseUrl))
			throw new ArgumentException("Base URL is required.", nameof(baseUrl));

		var normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
		_http = new HttpClient
		{
			BaseAddress = new Uri(normalized, UriKind.Absolute),
			Timeout = TimeSpan.FromMinutes(2)
		};
		_ownsClient = true;
	}

	/// <summary>Create a client over a caller-supplied <see cref="HttpClient"/> (DI/IHttpClientFactory).</summary>
	public CommercialApiPolicyClient(HttpClient http)
	{
		_http = http ?? throw new ArgumentNullException(nameof(http));
		_ownsClient = false;
	}

	/// <summary>Fetch a single policy/quote by number and return it as a CDM view.</summary>
	public async Task<CDMPolicyView> GetPolicyAsync(string policyNumber, CancellationToken ct = default)
	{
		if (string.IsNullOrWhiteSpace(policyNumber))
			throw new ArgumentException("Policy number is required.", nameof(policyNumber));

		using var response = await _http.GetAsync(
			$"api/policy/{Uri.EscapeDataString(policyNumber)}", ct);
		response.EnsureSuccessStatusCode();

		var json = await response.Content.ReadAsStringAsync(ct);
		return JsonSerializer.Deserialize<CDMPolicyView>(json, JsonOptions)
			?? throw new InvalidOperationException($"Policy '{policyNumber}' returned no data.");
	}

	public void Dispose()
	{
		if (_ownsClient)
			_http.Dispose();
	}
}
