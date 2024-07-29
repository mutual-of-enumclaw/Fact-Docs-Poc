// See https://aka.ms/new-console-template for more information
using FaCT.Rating.POC;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args)
    .ConfigureServices((hostContext, services) =>
    {
        var settings = new Settings();
        hostContext.Configuration.Bind(settings);

        services.AddSingleton<Settings>();
    });

await builder.RunConsoleAsync();
