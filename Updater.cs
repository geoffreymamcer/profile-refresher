using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>A newer version published as a GitHub Release.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string SetupUrl,
    long Size, string? Sha256);

/// <summary>
/// Checks the repo's latest GitHub Release and installs it: downloads ProfileRefresher-Setup.exe,
/// checks its size and SHA-256, then runs it silently. The installer closes this app, replaces the
/// files (settings and chat logs are kept) and starts the app again.
/// </summary>
public static class Updater
{
    private const string LatestReleaseApi = "https://api.github.com/repos/geoffreymamcer/profile-refresher/releases/latest";
    private const string SetupAssetName = "ProfileRefresher-Setup.exe";

    // Only for the automated test on GitHub Actions: a local fake release (http://localhost only).
    private static readonly string? TestUrl = Environment.GetEnvironmentVariable("PROFILEREFRESHER_UPDATE_TEST_URL");

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"ProfileRefresher/{Normalize(CurrentVersion)}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    public static string CurrentText => $"v{CurrentVersion.ToString(3)}";

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (TestUrl is not null) return u.IsLoopback;
        return u.Scheme == Uri.UriSchemeHttps &&
               (u.Host == "github.com" || u.Host == "api.github.com" || u.Host.EndsWith(".githubusercontent.com"));
    }

    /// <summary>The newer release, or null if this is the latest (or there's no release yet).</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var res = await Http.GetAsync(TestUrl ?? LatestReleaseApi, ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release published yet
        res.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= CurrentVersion) return null;

        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                continue;
            string url = a.GetProperty("browser_download_url").GetString() ?? "";
            if (!IsAllowedUrl(url)) return null;

            string? sha = a.TryGetProperty("digest", out var dg) && dg.GetString() is { } s &&
                          s.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? s[7..].ToLowerInvariant()
                : null;
            return new UpdateInfo(version, tag,
                root.TryGetProperty("body", out var b) ? (b.GetString() ?? "").Trim() : "",
                root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                url, a.GetProperty("size").GetInt64(), sha);
        }
        return null; // release without an installer
    }

    /// <summary>Downloads and verifies the installer, then starts it. The caller must close the app right after.</summary>
    public static async Task DownloadAndStartAsync(UpdateInfo u, IProgress<double>? progress, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ProfileRefresher-Update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.GetFiles(dir, "*.exe"))
            try { File.Delete(old); } catch { /* in use: leave it */ }
        string file = Path.Combine(dir, $"ProfileRefresher-Setup-{u.Version.ToString(3)}.exe");

        using (var res = await Http.GetAsync(u.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                done += n;
                if (u.Size > 0) progress?.Report(Math.Min(1.0, (double)done / u.Size));
            }
        }

        var info = new FileInfo(file);
        if (u.Size > 0 && info.Length != u.Size)
            throw new InvalidDataException($"the download is incomplete ({info.Length:N0} of {u.Size:N0} bytes)");
        if (u.Sha256 is not null)
        {
            await using var f = File.OpenRead(file);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(f, ct)).ToLowerInvariant();
            if (hash != u.Sha256) throw new InvalidDataException("the download is damaged (checksum mismatch)");
        }

        // /RELAUNCH=1: the installer starts the app again when it's done (see installer/ProfileRefresher.iss).
        Process.Start(new ProcessStartInfo(file,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1") { UseShellExecute = true });
    }
}
