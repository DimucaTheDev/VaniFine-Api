namespace VaniFine.WebApi;

internal static class Program
{
    public static string VersionsJsonPath =>
        Environment.GetEnvironmentVariable("VERSIONS_JSON_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "data", "versions.json");

    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    public const string MinVersion = "1.20";

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddSingleton<VersionStore>();
        builder.Services.AddHostedService<VersionBackgroundService>();

        var app = builder.Build();

        app.Logger.LogInformation("VaniFine Web API");
        
        app.MapGet("/map", (VersionStore store) =>
        {
            if (!store.IsReady)
                return Results.StatusCode(503);
            return Results.Json(store.Versions);
        });

        await app.RunAsync();
    }
}