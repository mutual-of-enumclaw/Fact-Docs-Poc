using FapPdfTools.Population;
using FapPdfTools.Population.Maps;
using FapPdfTools.Server.Configuration;
using FapPdfTools.Server.Infrastructure;

Spire.Pdf.License.LicenseProvider.SetLicenseKey("vz+UTK22G7SNfgEAxsAEfRc5e5LhTNX0Na451lUUQycsqDXEltzv7inyKYc6jipGqC7pNMi+pZC5AN2B2fzZWKndQZCntRVGZ3INztr/4K8NFL+SwPuKZYvOWhKWwAFpezAZ7h+akP7zD6f8v5IXe11ROfeBEaDmUmIuaS7u3+paLmUrJDNkAF4J9sZ37Hivv2weB3SZJRCvyjce5O3bRUbrIsSxZ6LWIRiroCGjPuJcdwkEPW8U/ljOmdEvg+B+h/CCyhcOs8YdCUHsHGlcovDMONLQ0iMWeCwN5WZieSHqb5UpDaXWrlLVoF3ZMnAxk1mFHqAFwFBs7/9sswaGIehVPfMqT6FHZyBI+y+RvQjjhZ4T52/CuPgXUzCbq54s9ISwb40Jf5o5UMhvTN86zMl0CMtQYSM/AzKtpW3YR7xsVO3tUUTINrxGlCJ71tqhR7osJZtFLpqKsUv6SJ8VBW37pobQu5OYVrmoJFMNcSBIwqfRavP8AtM4xgvK6Pp8O7GDrvha1GMb317ZiwcHgWtxCs3gfwCB71cTYf7r9cnYIeq/H7VjXF/ai5BQ0Ok8NtGUMQtxiGcHlCuNShu710wzJR7E4jI5wBaBUvg5h8Plm4sEOgiqmiIRW37b5NEYnwWVbsVzxAnilHI0BgpeuhWOv42zO/H+pRMEnsN3ISaFDyckpM4YwcIu3/eDnqSYIaNZIFLQVb0yFa6JYJ5otrAZKbNiNK7rZj7MxkRIOl52KND4CGzOLtR0cBMD4tAL8/uU1LscNgoe3NTQP7MOf0w0otrqzccwrGlz8Rl9P6jlx+WKX9przXk/5B8sFjEefW8Oi+jM2w1RmwDy/nDVwh+9NmXgW4jjiBE0lcM/lidfo35hFB9VYHfZjDhqKtVHdkwm7feKCGB8qWdJkzvKKE1eKhmwG9ZCi45PKdegUpZJtsMTPDMTaQKlWwwEM3tUupdvnBHQYIwXzeqFo6DlSwNSmvlR5i2//LA15MvtppCSKj8Bj4XPb1i6Mkp7yfkHERaUsPbljWEz2WAKXZTtyVQxWC7oTMLnQ04pVAMHekpckRtl/n3rwefKQww9lQFHxCSJ3dk/fLI1SuqZmhmi4IT1nbMBxXiekTbVKfcTZDDFZtbI6WMdFjlP5OUqF2v3kd61um8ulPL7h0VAEC6l1CMhi1jqdne671Oyziuek38KIMOutOc71KUnVet9w64hO2u4Xwa2tvvaXarcyX5elK5HJA3DVGHlZyrMyFLvarvN4QWlhUhUmiWB7eGlqifZ/0fSWMIocrsl3gEslwYpSx1mLuFtmybnKeDn44pAD3yX31+IpEDrMrNAbgYMh44mzPcbICZyfq2k8sYZYFd0s1S9A0xUE2xO+nb4tEul1oku+gWorUKnsvlSB2pT+JKdddfknzCsiUtjxCMy8k1Giukd+Ols33za0GKOGWnpPlF5qjpLW8BPLvbobE913THp7lY+g8PKFYVf1xNr7jKQELMtS3GAgbLNn5jSDeTcp5/Qfvz4zU8s/vtzvJo15M65EcVd09vVKtmV7j+ktka1BMyEln1cy4NB1N9t/2UpAVUsn+EgE4Ccb+gFwiCH1ifEjxj76nr1vaR4xMBe/Js/+8yqvfDPoUNuSS63aLlWrSgfQISFBiEZOg19MJtleL62LyeurYVEH0jkvi0TqLyZ2YW6k333rSbuGFQiGWDIqyN9baJfRnWazzCcoyFQ8gHXJHZU3fI3tW84eDVY5CnJ1DoUIqwJ1pIpIa8kTuxMR7M=");

var builder = WebApplication.CreateBuilder(args);

// Configuration
builder.Services.Configure<FormFileOptions>(builder.Configuration.GetSection(FormFileOptions.SectionName));
builder.Services.Configure<CommercialApiOptions>(builder.Configuration.GetSection(CommercialApiOptions.SectionName));

// Core services
builder.Services.AddSingleton<FormFileClient>();
builder.Services.AddSingleton<FxrFontLibrary>();
builder.Services.AddSingleton<FapToPdfGenerator>();

// Authored-form & scenario stores
builder.Services.AddSingleton<FormDefinitionStore>();
builder.Services.AddSingleton<ScenarioStore>();

// Pre-built fillable PDF template library (loaded at request time; no FAP parsing)
builder.Services.AddSingleton<TemplateStore>();

// Field-map registry (add more IFormFieldMap implementations here as they are written)
builder.Services.AddSingleton<IFormFieldMap, Ca2146FieldMap>();
builder.Services.AddSingleton<IFormFieldMap, BopDecPageFieldMap>();
builder.Services.AddSingleton<IFormFieldMap, Mcs90aFieldMap>();
builder.Services.AddSingleton<IFormFieldMap, Eb2410FieldMap>();
builder.Services.AddSingleton<IFormFieldMap, GenericHeaderFieldMap>(); // fallback for forms w/o a specific map
builder.Services.AddSingleton<FormFieldMapRegistry>();

builder.Services
    .AddControllers()
    // Form population must tolerate partial policy data. The CDM model carries DataAnnotations
    // (Required/Range) that would otherwise make [ApiController] auto-reject an incomplete
    // CDMPolicyView with a 400 before the action runs. Controllers do their own validation
    // (422/404/400) where it matters.
    .ConfigureApiBehaviorOptions(options => options.SuppressModelStateInvalidFilter = true);
builder.Services.AddOpenApi();

// CORS for React dev server
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                  "http://localhost:5173", "http://localhost:5174", "http://localhost:3000",
                  // fact-commercial-web dev server (vue.config.js devServer.port)
                  "http://localhost:4200", "https://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("X-Field-Count", "X-Field-Names");
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();
