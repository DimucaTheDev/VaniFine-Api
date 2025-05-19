using System.IO.Compression;
using System.Text.Json;

public class Program
{
    public const string VersionsJsonPath = "versions.json";
    public const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest.json";
    public const string MinVersion = "1.20";

    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);

        builder.Services.AddSingleton<VersionStore>();

        builder.Services.AddHostedService<VersionBackgroundService>();

        var app = builder.Build();

        app.MapGet("/map", (VersionStore store) =>
        {
            if (!store.IsReady)
                return Results.StatusCode(503);
            return Results.Json(store.Versions);
        });

        app.Run();
    }
}

public class VersionStore
{
    public Dictionary<string, int> Versions { get; private set; } = new();
    public bool IsReady { get; private set; } = false;

    public void Update(Dictionary<string, int> newData)
    {
        Versions = newData;
        IsReady = true;
    }
}

public class VersionBackgroundService(VersionStore store) : BackgroundService
{
    private static readonly HttpClient _http = new();
    private readonly TimeSpan _interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken);
                await RefreshAsync(stoppingToken);
            }
            catch (TaskCanceledException) { }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        Dictionary<string, int> existing = File.Exists(Program.VersionsJsonPath)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(await File.ReadAllTextAsync(Program.VersionsJsonPath, ct))
              ?? new Dictionary<string, int>()
            : new Dictionary<string, int>();

        using var manifestStream = await _http.GetStreamAsync(Program.ManifestUrl, ct);
        using var manifestDoc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);
        var allVersions = manifestDoc.RootElement.GetProperty("versions").EnumerateArray().ToList();

        var baseElem = allVersions.FirstOrDefault(e => e.GetProperty("id").GetString() == Program.MinVersion);
        if (baseElem.ValueKind == JsonValueKind.Undefined)
            throw new Exception($"Manifest doesn't contain base version {Program.MinVersion}");
        var threshold = DateTime.Parse(baseElem.GetProperty("releaseTime").GetString()!);

        var toProcess = allVersions
            .Where(e => DateTime.Parse(e.GetProperty("releaseTime").GetString()!) >= threshold)
            .Select(e => (id: e.GetProperty("id").GetString()!, url: e.GetProperty("url").GetString()!))
            .Where(x => !existing.ContainsKey(x.id))
            .ToList();

        if (toProcess.Count == 0)
        {
            store.Update(existing);
            Console.WriteLine($"[{DateTime.UtcNow:O}] no updates, {existing.Count} total records.");
            return;
        }

        foreach (var (id, url) in toProcess)
        {
            ct.ThrowIfCancellationRequested();

            await using var infoStream = await _http.GetStreamAsync(url, ct);
            using var infoDoc = await JsonDocument.ParseAsync(infoStream, cancellationToken: ct);

            var clientUrl = infoDoc.RootElement
                .GetProperty("downloads")
                .GetProperty("client")
                .GetProperty("url")
                .GetString()!;

            var tmpJar = Path.GetTempFileName();
            using (var resp = await _http.GetAsync(clientUrl, ct))
            using (var fs = File.Create(tmpJar))
                await resp.Content.CopyToAsync(fs, ct);

            using var zip = ZipFile.OpenRead(tmpJar);
            var entry = zip.GetEntry("version.json")
                        ?? throw new Exception($"JAR {id} missing version.json");
            int packVer;
            using (var es = entry.Open())
            using (var verJson = await JsonDocument.ParseAsync(es, cancellationToken: ct))
            {
                packVer = verJson.RootElement
                                .GetProperty("pack_version")
                                .GetProperty("resource")
                                .GetInt32();
            }
            File.Delete(tmpJar);

            existing[id] = packVer;
            Console.WriteLine($"[{DateTime.UtcNow:0}] Added {id} = {packVer}");
        }

        var outJson = JsonSerializer.Serialize(existing, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Program.VersionsJsonPath, outJson, ct);

        store.Update(existing);
        Console.WriteLine($"[{DateTime.UtcNow:O}] versions.json updated, {existing.Count} total records.");
    }

}
