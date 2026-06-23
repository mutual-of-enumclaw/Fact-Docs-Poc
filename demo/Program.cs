using FapPdfTools.Population;
using FapPdfTools.Population.Maps;
using FapPdfTools.Server.Configuration;
using MoE.CommonDataModel;
using MoE.GhostDraftDataModel.SDK;

// Spire.PDF license (same key the server uses) so output has no evaluation watermark.
Spire.Pdf.License.LicenseProvider.SetLicenseKey("vz+UTK22G7SNfgEAxsAEfRc5e5LhTNX0Na451lUUQycsqDXEltzv7inyKYc6jipGqC7pNMi+pZC5AN2B2fzZWKndQZCntRVGZ3INztr/4K8NFL+SwPuKZYvOWhKWwAFpezAZ7h+akP7zD6f8v5IXe11ROfeBEaDmUmIuaS7u3+paLmUrJDNkAF4J9sZ37Hivv2weB3SZJRCvyjce5O3bRUbrIsSxZ6LWIRiroCGjPuJcdwkEPW8U/ljOmdEvg+B+h/CCyhcOs8YdCUHsHGlcovDMONLQ0iMWeCwN5WZieSHqb5UpDaXWrlLVoF3ZMnAxk1mFHqAFwFBs7/9sswaGIehVPfMqT6FHZyBI+y+RvQjjhZ4T52/CuPgXUzCbq54s9ISwb40Jf5o5UMhvTN86zMl0CMtQYSM/AzKtpW3YR7xsVO3tUUTINrxGlCJ71tqhR7osJZtFLpqKsUv6SJ8VBW37pobQu5OYVrmoJFMNcSBIwqfRavP8AtM4xgvK6Pp8O7GDrvha1GMb317ZiwcHgWtxCs3gfwCB71cTYf7r9cnYIeq/H7VjXF/ai5BQ0Ok8NtGUMQtxiGcHlCuNShu710wzJR7E4jI5wBaBUvg5h8Plm4sEOgiqmiIRW37b5NEYnwWVbsVzxAnilHI0BgpeuhWOv42zO/H+pRMEnsN3ISaFDyckpM4YwcIu3/eDnqSYIaNZIFLQVb0yFa6JYJ5otrAZKbNiNK7rZj7MxkRIOl52KND4CGzOLtR0cBMD4tAL8/uU1LscNgoe3NTQP7MOf0w0otrqzccwrGlz8Rl9P6jlx+WKX9przXk/5B8sFjEefW8Oi+jM2w1RmwDy/nDVwh+9NmXgW4jjiBE0lcM/lidfo35hFB9VYHfZjDhqKtVHdkwm7feKCGB8qWdJkzvKKE1eKhmwG9ZCi45PKdegUpZJtsMTPDMTaQKlWwwEM3tUupdvnBHQYIwXzeqFo6DlSwNSmvlR5i2//LA15MvtppCSKj8Bj4XPb1i6Mkp7yfkHERaUsPbljWEz2WAKXZTtyVQxWC7oTMLnQ04pVAMHekpckRtl/n3rwefKQww9lQFHxCSJ3dk/fLI1SuqZmhmi4IT1nbMBxXiekTbVKfcTZDDFZtbI6WMdFjlP5OUqF2v3kd61um8ulPL7h0VAEC6l1CMhi1jqdne671Oyziuek38KIMOutOc71KUnVet9w64hO2u4Xwa2tvvaXarcyX5elK5HJA3DVGHlZyrMyFLvarvN4QWlhUhUmiWB7eGlqifZ/0fSWMIocrsl3gEslwYpSx1mLuFtmybnKeDn44pAD3yX31+IpEDrMrNAbgYMh44mzPcbICZyfq2k8sYZYFd0s1S9A0xUE2xO+nb4tEul1oku+gWorUKnsvlSB2pT+JKdddfknzCsiUtjxCMy8k1Giukd+Ols33za0GKOGWnpPlF5qjpLW8BPLvbobE913THp7lY+g8PKFYVf1xNr7jKQELMtS3GAgbLNn5jSDeTcp5/Qfvz4zU8s/vtzvJo15M65EcVd09vVKtmV7j+ktka1BMyEln1cy4NB1N9t/2UpAVUsn+EgE4Ccb+gFwiCH1ifEjxj76nr1vaR4xMBe/Js/+8yqvfDPoUNuSS63aLlWrSgfQISFBiEZOg19MJtleL62LyeurYVEH0jkvi0TqLyZ2YW6k333rSbuGFQiGWDIqyN9baJfRnWazzCcoyFQ8gHXJHZU3fI3tW84eDVY5CnJ1DoUIqwJ1pIpIa8kTuxMR7M=");

// Usage (a trailing form key BOPDEC / CA2146 / CA2151 may be added to any of these):
//   (no args)                       -> populate the default form from a built-in sample policy
//   <policyNumber>                  -> fetch from the default environment (dev), then populate
//   <env|apiBaseUrl> <policyNumber> -> fetch from the named env (dev/dev3/tst/tst2/acc) or a full URL
const string DefaultEnvironment = "dev";
const string DefaultForm = "CA2146";

var argList = args.ToList();

// A trailing argument naming a known form selects which form to populate.
string formKey = DefaultForm;
if (argList.Count > 0 && IsKnownForm(argList[^1]))
{
	formKey = argList[^1];
	argList.RemoveAt(argList.Count - 1);
}

CDMPolicyView policy;

if (argList.Count >= 1)
{
	var envOrUrl = argList.Count >= 2 ? argList[0] : DefaultEnvironment;
	var policyNumber = argList.Count >= 2 ? argList[1] : argList[0];
	var apiBaseUrl = ResolveApiBaseUrl(envOrUrl);

	Console.WriteLine($"Fetching policy '{policyNumber}' from {apiBaseUrl} ...");
	using var apiClient = new CommercialApiPolicyClient(apiBaseUrl);
	policy = await apiClient.GetPolicyAsync(policyNumber);
	Console.WriteLine($"Fetched policy '{policy.Number}'.");
}
else
{
	Console.WriteLine("No policy number supplied; using built-in sample policy.");
	policy = BuildSamplePolicy();
}

var options = new FormFileOptions
{
	FormDatPath = @"C:\EDrive\FORM.DAT",
	FormsDirectory = @"C:\EDrive\moec0\Mstrres\MOEC0\Forms",
	DdtDirectory = @"C:\EDrive\moec0\Mstrres\MOEC0\Ddtlib",
	FxrPath = @"C:\EDrive\moec0\Mstrres\MOEC0\DEFLIB\REL103.FXR"
};

var map = SelectMap(formKey);
var populator = new CdmFormPopulator(options);

Console.WriteLine($"=== Form {formKey}: resolved field values from CDMPolicyView ===");
foreach (var kv in map.BuildValues(policy))
	Console.WriteLine($"  {kv.Key,-16} = {kv.Value}");

var bytes = await populator.PopulateAsync(map, policy, flatten: true);

var outDir = @"C:\src\fact-pdf-tools\output";
Directory.CreateDirectory(outDir);
var outPath = Path.Combine(outDir, $"{formKey}_populated.pdf");
File.WriteAllBytes(outPath, bytes);

Console.WriteLine();
Console.WriteLine($"Wrote {bytes.Length:N0} bytes -> {outPath}");

static bool IsKnownForm(string key)
	=> key.ToUpperInvariant() is "BOPDEC" or "CA2146" or "CA2151";

static IFormFieldMap SelectMap(string formKey) => formKey.ToUpperInvariant() switch
{
	"BOPDEC" => new BopDecPageFieldMap(),
	// MoE form CA 21 46 is the Split Underinsured Motorists Coverage Limits
	// endorsement (the ISO CA 21 51 counterpart MoE maintains).
	"CA2146" or "CA2151" => new Ca2146FieldMap(),
	_ => throw new ArgumentException($"Unknown form '{formKey}'.")
};

// Resolve a known FaCT environment name to its Commercial API URL, or pass a full URL through.
static string ResolveApiBaseUrl(string envOrUrl)
{
	if (envOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
		|| envOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
	{
		return envOrUrl;
	}

	return envOrUrl.ToLowerInvariant() switch
	{
		"dev" => "https://pointmoeapps-dev1.mutualofenumclaw.net/commercialapi",
		"dev3" => "https://pointmoeapps-dev3.mutualofenumclaw.net/commercialapi",
		"tst" => "https://pointmoeapps-tst1.mutualofenumclaw.net/commercialapi",
		"tst2" => "https://pointmoeapps-tst2.mutualofenumclaw.net/commercialapi",
		"acc" => "https://pointmoeapps-acc.mutualofenumclaw.net/commercialapi",
		_ => throw new ArgumentException(
			$"Unknown environment '{envOrUrl}'. Use dev, dev3, tst, tst2, acc, or a full URL.")
	};
}

// --- Built-in sample policy (stands in for a Commercial API GetPolicy result) ---
static CDMPolicyView BuildSamplePolicy() => new()
{
	Number = "BOP-2026-000123",
	EffectiveDate = new DateTime(2026, 7, 1),
	ExpirationDate = new DateTime(2027, 7, 1),
	Premium = 4827.00m,
	State = "WA",
	Parties = new List<Party>
	{
		new InsuredParty
		{
			FullName = "Cascade Hardware & Supply LLC",
			BusinessEntity = "LLC",
			Address = new List<Address>
			{
				new Address
				{
					AddressLine = "1420 Industrial Way",
					ExtendedAddressLine = "Suite 200",
					City = "Enumclaw",
					State = "WA",
					ZipCode = "98022"
				}
			}
		},
		new AgencyParty
		{
			FullName = "Mountain View Insurance Agency",
			AgencyCode = "AG-4471",
			Phone = "(360) 825-1100",
			Address = new List<Address>
			{
				new Address
				{
					AddressLine = "55 Cole Street",
					City = "Enumclaw",
					State = "WA",
					ZipCode = "98022"
				}
			}
		}
	},
	Lines = new List<Line>
	{
		new CommercialAutoLine
		{
			Coverages = new List<Coverage>
			{
				new CommercialAutoUNCoverage
				{
					State = "WA",
					LimitType = "SPLIT",
					BodilyInjuryOnly = false,
					PerPersonLimit = 25000,
					PerAccidentLimit = 50000
				},
				new CommercialAutoUNPDCoverage
				{
					State = "WA",
					Limit = 10000
				}
			}
		}
	}
};
