using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ProfileRefresher;

/// <summary>A browser profile that has the helper extension, found in the browser's own files.</summary>
public sealed record ExtensionProfile(string ProfileDir, string Name, string Key, bool Enabled, string ExtensionPath);

/// <summary>
/// Finds the profiles (Chrome or Edge, default data folder) that have the helper extension loaded, and
/// opens them. The browser keeps each profile's extensions in its "Secure Preferences" / "Preferences"
/// file; an unpacked extension is listed with the folder it was loaded from, whose manifest.json we read.
/// </summary>
public static class ProfileLauncher
{
    /// <summary>Our extension's name, from the copy installed next to the app (or the default name).</summary>
    private static readonly string ExtensionName = ReadName(Path.Combine(AppContext.BaseDirectory, "extension"))
                                                   ?? "Profile Refresher Helper";

    public static List<ExtensionProfile> FindProfilesWithExtension(BrowserKind kind)
    {
        var found = new List<ExtensionProfile>();
        string dataDir = kind.DefaultUserDataDir;
        if (!Directory.Exists(dataDir)) return found;

        var names = ReadProfileNames(dataDir);
        var dirs = names.Keys.ToList();
        // Profiles missing from Local State's list but present on disk.
        foreach (var d in Directory.GetDirectories(dataDir))
        {
            string n = Path.GetFileName(d);
            if ((n == "Default" || n.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase)) &&
                !dirs.Contains(n, StringComparer.OrdinalIgnoreCase))
                dirs.Add(n);
        }

        foreach (var dir in dirs)
        {
            var hit = FindOurExtension(Path.Combine(dataDir, dir));
            if (hit is null) continue;
            string name = names.TryGetValue(dir, out var nm) && nm.Length > 0 ? nm : dir;
            string key = NormalizeDir(dataDir).ToLowerInvariant() + "|" + dir; // same key as BrowserScanner
            found.Add(new ExtensionProfile(dir, name, key, hit.Value.Enabled, hit.Value.Path));
        }

        found.Sort((a, b) => StrCmpLogicalW(a.Name, b.Name)); // prem2 before prem10
        return found;
    }

    /// <summary>Opens a window of this profile, with the given addresses (none: the browser's start pages).</summary>
    public static void Open(BrowserKind kind, ExtensionProfile profile, IEnumerable<string> urls)
    {
        string exe = FindBrowserExe(kind) ?? throw new FileNotFoundException($"{kind.Name} was not found on this PC.");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        psi.ArgumentList.Add($"--profile-directory={profile.ProfileDir}");
        foreach (var u in urls) psi.ArgumentList.Add(u);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>"site.com" or "https://site.com/x" from the Site tab setting, as an address to open; null if it isn't one.</summary>
    public static string? SiteUrl(string tabMatch)
    {
        string t = tabMatch.Trim();
        if (t.Length == 0 || t.Contains(' ')) return null;
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            t = "https://" + t.TrimStart('/');
        return Uri.TryCreate(t, UriKind.Absolute, out var u) && u.Host.Contains('.') ? u.ToString() : null;
    }

    // ---------- helpers ----------

    private static (bool Enabled, string Path)? FindOurExtension(string profilePath)
    {
        foreach (var file in new[] { "Secure Preferences", "Preferences" })
        {
            try
            {
                string f = Path.Combine(profilePath, file);
                if (!File.Exists(f)) continue;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) ||
                    !ext.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var e in settings.EnumerateObject())
                {
                    if (e.Value.ValueKind != JsonValueKind.Object ||
                        !e.Value.TryGetProperty("path", out var p) || p.GetString() is not { Length: > 0 } path ||
                        !System.IO.Path.IsPathRooted(path))
                        continue; // store extensions have a relative path; ours is loaded unpacked from a folder
                    if (!string.Equals(ReadName(path), ExtensionName, StringComparison.Ordinal)) continue;

                    bool enabled = !(e.Value.TryGetProperty("disable_reasons", out var dr) &&
                                     ((dr.ValueKind == JsonValueKind.Number && dr.GetInt32() != 0) ||
                                      (dr.ValueKind == JsonValueKind.Array && dr.GetArrayLength() > 0)));
                    if (e.Value.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number && st.GetInt32() == 0)
                        enabled = false;
                    return (enabled, path);
                }
            }
            catch
            {
                // Locked or damaged file: try the other one.
            }
        }
        return null;
    }

    private static string? ReadName(string extensionDir)
    {
        try
        {
            string m = Path.Combine(extensionDir, "manifest.json");
            if (!File.Exists(m)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(m));
            return doc.RootElement.GetProperty("name").GetString();
        }
        catch { return null; }
    }

    private static Dictionary<string, string> ReadProfileNames(string dataDir)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string f = Path.Combine(dataDir, "Local State");
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.TryGetProperty("profile", out var p) && p.TryGetProperty("info_cache", out var c))
                foreach (var e in c.EnumerateObject())
                    names[e.Name] = e.Value.TryGetProperty("name", out var n) ? n.GetString() ?? e.Name : e.Name;
        }
        catch { /* folder names are used instead */ }
        return names;
    }

    private static string? FindBrowserExe(BrowserKind kind)
    {
        string exeName = kind.ProcessName + ".exe";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName);
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { }
        }

        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] tries = kind.ProcessName == "msedge"
            ? new[] { Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"), Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe") }
            : new[] { Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"), Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                      Path.Combine(local, @"Google\Chrome\Application\chrome.exe") };
        return tries.FirstOrDefault(File.Exists);
    }

    private static string NormalizeDir(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\'); }
        catch { return path.Trim().TrimEnd('\\'); }
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);
}
