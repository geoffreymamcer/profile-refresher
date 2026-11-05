using System.IO;
using System.Diagnostics;
using System.Management;
using System.Text.Json;

namespace ProfileRefresher;

public sealed record BrowserKind(string Name, string ProcessName, string DefaultUserDataDir)
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static readonly BrowserKind Chrome =
        new("Google Chrome", "chrome", Path.Combine(LocalAppData, "Google", "Chrome", "User Data"));

    public static readonly BrowserKind Edge =
        new("Microsoft Edge", "msedge", Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"));

    public override string ToString() => Name;
}

/// <summary>An open, normal browser window and the profile it belongs to. Windows are kept in z-order (front first).</summary>
public sealed record BrowserWindow(IntPtr Hwnd, string Title, string ProfileKey);

public sealed class BrowserProfile
{
    public required string Key { get; init; }
    public required string UserDataDir { get; init; }
    public required string ProfileDir { get; init; }
    public required string DisplayName { get; init; }
    public bool Identified { get; init; } = true;

    /// <summary>Profile name from the browser (without any suffix), used to match the taskbar button.</summary>
    public string? BaseName { get; init; }
    public string? AppUserModelId { get; init; }
    public string? TaskbarLabel { get; init; }

    /// <summary>0-based position of this profile's taskbar button, or null if not found.</summary>
    public int? TaskbarIndex { get; set; }

    public int WindowCount { get; set; }
    public bool IsSelected { get; set; } = true;

    /// <summary>When this profile was opened (local time), or null if it couldn't be determined.</summary>
    public DateTime? OpenedAt { get; set; }

    /// <summary>1-based position in the run order (set by the window when sorting).</summary>
    public int Position { get; set; }

    public string Header => $"{Position}.  {DisplayName}";

    public string OpenedText => OpenedAt is { } t
        ? "opened " + (t.Date == DateTime.Today ? t.ToString("h:mm:ss tt") : t.ToString("MMM d, h:mm tt"))
        : "opened: unknown";

    public string TaskbarText => TaskbarIndex is { } i ? $"taskbar #{i + 1}" : "taskbar: not found";

    public string Details =>
        (Identified ? ProfileDir : "profile could not be identified") +
        $"  ·  {TaskbarText}  ·  {OpenedText}  ·  {WindowCount} window{(WindowCount == 1 ? "" : "s")}  ·  {UserDataDir}";
}

public sealed record ScanResult(List<BrowserProfile> Profiles, List<BrowserWindow> Windows);

/// <summary>
/// Finds open Chrome/Edge windows and maps each to its (user-data dir, profile dir) pair.
/// Chromium tags every browser window with an AppUserModel relaunch command that contains
/// --profile-directory, which is the most reliable per-window profile marker on Windows.
/// </summary>
public sealed class BrowserScanner
{
    private readonly BrowserKind _kind;
    private Dictionary<int, string> _mainCommandLines = new();

    public BrowserScanner(BrowserKind kind) => _kind = kind;

    public BrowserKind Kind => _kind;

    /// <param name="readCommandLines">True on an explicit Detect (uses WMI once); false for the cheap re-scans during a run.</param>
    public ScanResult Scan(bool readCommandLines)
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(_kind.ProcessName))
        {
            pids.Add(p.Id);
            p.Dispose();
        }

        if (pids.Count == 0) return new ScanResult(new(), new());
        if (readCommandLines) _mainCommandLines = ReadMainCommandLines(_kind.ProcessName + ".exe");

        // 1. Collect candidate top-level browser windows in z-order.
        var raw = new List<(IntPtr Hwnd, int Pid, string Title)>();
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            if (Native.GetClassName(h) != "Chrome_WidgetWin_1") return true;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains((int)pid)) return true;
            string title = Native.GetTitle(h);
            if (title.Length == 0 || title.StartsWith("DevTools", StringComparison.OrdinalIgnoreCase)) return true;
            raw.Add((h, (int)pid, title));
            return true;
        }, IntPtr.Zero);

        // 2. Resolve profile for each window.
        var states = new Dictionary<string, LocalState>(StringComparer.OrdinalIgnoreCase);
        LocalState StateFor(string dir)
        {
            if (!states.TryGetValue(dir, out var s)) states[dir] = s = LocalState.Load(dir);
            return s;
        }

        var profiles = new Dictionary<string, BrowserProfile>(StringComparer.OrdinalIgnoreCase);
        var windows = new List<BrowserWindow>();

        var processStarts = new Dictionary<int, DateTime?>();
        DateTime? ProcessStartUtc(int pid)
        {
            if (processStarts.TryGetValue(pid, out var t)) return t;
            try
            {
                using var proc = Process.GetProcessById(pid);
                t = proc.StartTime.ToUniversalTime();
            }
            catch { t = null; }
            return processStarts[pid] = t;
        }

        foreach (var (hwnd, pid, title) in raw)
        {
            var (appId, relaunch, taskbarLabel) = Native.GetAppUserModelInfo(hwnd);

            // Installed web apps (PWAs) are not regular browser windows.
            if (relaunch is not null &&
                (relaunch.Contains("--app-id=", StringComparison.OrdinalIgnoreCase) ||
                 relaunch.Contains("--app=", StringComparison.OrdinalIgnoreCase)))
                continue;

            _mainCommandLines.TryGetValue(pid, out var mainCmd);
            string dataDir = NormalizeDir(
                CmdArg.Get(relaunch, "--user-data-dir") ??
                CmdArg.Get(mainCmd, "--user-data-dir") ??
                _kind.DefaultUserDataDir);

            var state = StateFor(dataDir);
            string? profileDir = CmdArg.Get(relaunch, "--profile-directory");
            bool identified = true;

            if (profileDir is null)
            {
                if (state.LastActive.Count == 1) profileDir = state.LastActive[0];
                else if (state.LastActive.Count == 0) profileDir = CmdArg.Get(mainCmd, "--profile-directory") ?? "Default";
                else
                {
                    // Edge puts the profile name in the title: "Page - Work - Microsoft Edge".
                    profileDir = state.Names
                        .Where(kv => title.Contains($" - {kv.Value} - ", StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .FirstOrDefault();
                    if (profileDir is null)
                    {
                        identified = false;
                        profileDir = "window-group:" + (appId ?? hwnd.ToString());
                    }
                }
            }

            string key = dataDir.ToLowerInvariant() + "|" + profileDir;
            if (!profiles.TryGetValue(key, out var profile))
            {
                string name = identified
                    ? (state.Names.TryGetValue(profileDir, out var n) && n.Length > 0 ? n : profileDir)
                    : $"Unidentified ({Shorten(title, 40)})";
                if (!string.Equals(dataDir, NormalizeDir(_kind.DefaultUserDataDir), StringComparison.OrdinalIgnoreCase))
                    name += "  [custom data dir]";

                string? baseName = identified && state.Names.TryGetValue(profileDir, out var bn) && bn.Length > 0 ? bn : null;
                string? label = taskbarLabel is { Length: > 0 } && !taskbarLabel.StartsWith('@') ? taskbarLabel : null;

                profile = new BrowserProfile
                {
                    Key = key,
                    UserDataDir = dataDir,
                    ProfileDir = profileDir,
                    DisplayName = name,
                    BaseName = baseName,
                    Identified = identified,
                    AppUserModelId = appId,
                    TaskbarLabel = label,
                    OpenedAt = readCommandLines && identified
                        ? ProfileOpenTime.Get(dataDir, profileDir, ProcessStartUtc(pid))
                        : null,
                };
                profiles[key] = profile;
            }

            profile.WindowCount++;
            windows.Add(new BrowserWindow(hwnd, title, key));
        }

        return new ScanResult(SortByName(profiles.Values), windows);
    }

    public static List<BrowserProfile> SortByName(IEnumerable<BrowserProfile> profiles) => profiles
        .OrderBy(p => p.UserDataDir, StringComparer.OrdinalIgnoreCase)
        .ThenBy(p => p.ProfileDir == "Default" ? 0 : 1)
        .ThenBy(p => p.ProfileDir.Length)
        .ThenBy(p => p.ProfileDir, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>Taskbar left to right; profiles not found on the taskbar go last, in name order.</summary>
    public static List<BrowserProfile> SortByTaskbar(IEnumerable<BrowserProfile> profiles) => SortByName(profiles)
        .Select((p, i) => (p, i))
        .OrderBy(x => x.p.TaskbarIndex ?? int.MaxValue)
        .ThenBy(x => x.i)
        .Select(x => x.p)
        .ToList();

    /// <summary>Earliest opened first; profiles with an unknown time go last, in name order.</summary>
    public static List<BrowserProfile> SortByOpened(IEnumerable<BrowserProfile> profiles)
    {
        var byName = SortByName(profiles);
        return byName
            .Select((p, i) => (p, i))
            .OrderBy(x => x.p.OpenedAt is null ? 1 : 0)
            .ThenBy(x => x.p.OpenedAt ?? DateTime.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.p)
            .ToList();
    }

    private static Dictionary<int, string> ReadMainCommandLines(string exeName)
    {
        var result = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = '{exeName}'");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    if (mo["CommandLine"] is not string cl || cl.Contains("--type=", StringComparison.Ordinal)) continue;
                    result[Convert.ToInt32(mo["ProcessId"])] = cl;
                }
            }
        }
        catch
        {
            // WMI unavailable: fall back to the default user-data dir.
        }
        return result;
    }

    private static string NormalizeDir(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))).TrimEnd('\\'); }
        catch { return path.Trim().TrimEnd('\\'); }
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private sealed class LocalState
    {
        public Dictionary<string, string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> LastActive { get; } = new();

        public static LocalState Load(string userDataDir)
        {
            var state = new LocalState();
            try
            {
                var path = Path.Combine(userDataDir, "Local State");
                if (!File.Exists(path)) return state;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(fs);
                if (!doc.RootElement.TryGetProperty("profile", out var profile)) return state;

                if (profile.TryGetProperty("info_cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                    foreach (var entry in cache.EnumerateObject())
                        if (entry.Value.ValueKind == JsonValueKind.Object &&
                            entry.Value.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                            state.Names[entry.Name] = n.GetString() ?? entry.Name;

                if (profile.TryGetProperty("last_active_profiles", out var active) && active.ValueKind == JsonValueKind.Array)
                    foreach (var a in active.EnumerateArray())
                        if (a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } dir)
                            state.LastActive.Add(dir);
            }
            catch
            {
                // Unreadable/locked Local State: names fall back to folder names.
            }
            return state;
        }
    }
}

internal static class CmdArg
{
    /// <summary>Reads --name=value from a Windows command line, handling "--name=a b" and --name="a b".</summary>
    public static string? Get(string? cmd, string name)
    {
        if (string.IsNullOrEmpty(cmd)) return null;
        int i = cmd.IndexOf(name + "=", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;

        int start = i + name.Length + 1;
        if (start >= cmd.Length) return null;

        bool tokenQuoted = i > 0 && cmd[i - 1] == '"';
        int end;
        if (tokenQuoted)
        {
            end = cmd.IndexOf('"', start);
        }
        else if (cmd[start] == '"')
        {
            start++;
            end = cmd.IndexOf('"', start);
        }
        else
        {
            end = start;
            while (end < cmd.Length && !char.IsWhiteSpace(cmd[end]) && cmd[end] != '"') end++;
        }
        if (end < 0) end = cmd.Length;

        var value = cmd[start..end].Trim();
        return value.Length == 0 ? null : value;
    }
}
