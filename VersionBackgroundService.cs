using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace VaniFine.WebApi;

public class VersionBackgroundService(VersionStore store) : BackgroundService
{
    private static readonly HttpClient Http = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                Console.WriteLine($"[{DateTime.UtcNow:O}] Operation canceled");
                break;
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[{DateTime.UtcNow:O}] Error during refresh: {ex.Message}");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        Console.WriteLine($"[{DateTime.UtcNow:O}] Refreshing...");

        var existing = await LoadExistingVersionsAsync(ct);
        var toProcess = await GetVersionsToProcessAsync(existing, ct);

        if (toProcess.Count == 0)
        {
            store.Update(existing);
            Console.WriteLine($"[{DateTime.UtcNow:O}] No updates, {existing.Count} total records.");
            return;
        }

        foreach (var (id, url) in toProcess)
        {
            ct.ThrowIfCancellationRequested();

            var packVer = await FetchPackVersionAsync(id, url, ct);
            existing[id] = packVer;

            Console.WriteLine($"[{DateTime.UtcNow:O}] Added {id} = {packVer}");
        }

        var outJson = JsonSerializer.Serialize(existing, JsonOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(Program.VersionsJsonPath)!);
        await File.WriteAllTextAsync(Program.VersionsJsonPath, outJson, ct);

        store.Update(existing);
        Console.WriteLine($"[{DateTime.UtcNow:O}] versions.json updated, {existing.Count} total records.");
    }

    private static async Task<Dictionary<string, int>> LoadExistingVersionsAsync(CancellationToken ct)
    {
        if (!File.Exists(Program.VersionsJsonPath))
        {
            return [];
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Program.VersionsJsonPath)!);
        var json = await File.ReadAllTextAsync(Program.VersionsJsonPath, ct);
        return JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? [];
    }

    private static async Task<List<(string Id, string Url)>>
        GetVersionsToProcessAsync(IReadOnlyDictionary<string, int> existing, CancellationToken ct)
    {
        await using var manifestStream = await Http.GetStreamAsync(Program.ManifestUrl, ct);
        using var manifestDoc = await JsonDocument.ParseAsync(manifestStream, cancellationToken: ct);

        var allVersions = manifestDoc.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .ToList();

        var baseElem = allVersions.FirstOrDefault(e => e.GetProperty("id").GetString() == Program.MinVersion);
        if (baseElem.ValueKind == JsonValueKind.Undefined)
        {
            throw new InvalidOperationException($"Manifest doesn't contain base version {Program.MinVersion}");
        }

        var threshold = ParseReleaseTime(baseElem);

        return allVersions
            .Where(e => ParseReleaseTime(e) >= threshold)
            .Select(e => (
                Id: e.GetProperty("id").GetString()!,
                Url: e.GetProperty("url").GetString()!
            ))
            .Where(x => !existing.ContainsKey(x.Id))
            .ToList();
    }

    private static async Task<int> FetchPackVersionAsync(string id, string url, CancellationToken ct)
    {
        await using var infoStream = await Http.GetStreamAsync(url, ct);
        using var infoDoc = await JsonDocument.ParseAsync(infoStream, cancellationToken: ct);

        var clientUrl = infoDoc.RootElement
            .GetProperty("downloads")
            .GetProperty("client")
            .GetProperty("url")
            .GetString()!;

        var tmpJar = Path.GetTempFileName();
        try
        {
            using (var resp = await Http.GetAsync(clientUrl, ct))
            await using (var fs = File.Create(tmpJar))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            await using var zip = await ZipFile.OpenReadAsync(tmpJar, ct);
            var entry = zip.GetEntry("version.json")
                        ?? throw new InvalidOperationException($"JAR {id} missing version.json");

            await using var es = await entry.OpenAsync(ct);
            using var verJson = await JsonDocument.ParseAsync(es, cancellationToken: ct);

            var packVersionElement = verJson.RootElement.GetProperty("pack_version");
            return
                packVersionElement.TryGetProperty("resource", out var deprecatedValue) ? deprecatedValue.GetInt32() :
                packVersionElement.TryGetProperty("resource_major", out var majorValue) ? majorValue.GetInt32() :
                throw new InvalidOperationException("Incorrect version.json format or not supported");
        }
        finally
        {
            if (File.Exists(tmpJar))
            {
                File.Delete(tmpJar);
            }
        }
    }

    private static DateTime ParseReleaseTime(JsonElement element)
    {
        var timeStr = element.GetProperty("releaseTime").GetString()!;
        return DateTime.Parse(timeStr, CultureInfo.InvariantCulture);
    }
}