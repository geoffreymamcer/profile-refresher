using System.IO;

namespace ProfileRefresher;

/// <summary>
/// Works out when a profile was OPENED (not created).
///
/// Each time a Chromium profile opens, the browser starts a new session file in
/// "&lt;profile&gt;\Sessions\Session_&lt;timestamp&gt;", where the timestamp is microseconds since 1601 (UTC).
/// Files from earlier browser runs are kept, and Chrome can start a fresh file later in a long session,
/// so the open time is the EARLIEST session file created since the browser process started.
/// </summary>
public static class ProfileOpenTime
{
    private static readonly TimeSpan StartMargin = TimeSpan.FromMinutes(2);

    /// <returns>Local time the profile was opened, or null if unknown.</returns>
    public static DateTime? Get(string userDataDir, string profileDir, DateTime? browserStartUtc)
    {
        var times = new List<DateTime>();
        try
        {
            var sessions = Path.Combine(userDataDir, profileDir, "Sessions");
            if (Directory.Exists(sessions))
            {
                foreach (var file in Directory.EnumerateFiles(sessions, "Session_*"))
                    if (TryGetTime(file, out var t)) times.Add(t);
            }
            else
            {
                // Older browser versions: one "Current Session" file, recreated on open.
                var legacy = Path.Combine(userDataDir, profileDir, "Current Session");
                if (File.Exists(legacy)) times.Add(File.GetCreationTimeUtc(legacy));
            }
        }
        catch
        {
            return null;
        }

        if (times.Count == 0) return null;

        DateTime chosen = times.Max();
        if (browserStartUtc is { } start)
        {
            var sinceStart = times.Where(t => t >= start - StartMargin).ToList();
            if (sinceStart.Count > 0) chosen = sinceStart.Min();
        }
        return chosen.ToLocalTime();
    }

    private static bool TryGetTime(string file, out DateTime utc)
    {
        utc = default;
        string name = Path.GetFileName(file);
        int us = name.IndexOf('_');
        if (us >= 0 && long.TryParse(name[(us + 1)..], out long micros) && micros > 0)
        {
            try
            {
                utc = DateTime.FromFileTimeUtc(checked(micros * 10));
                return true;
            }
            catch { /* not a timestamp; fall back to the file's creation time */ }
        }

        try
        {
            utc = File.GetCreationTimeUtc(file);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
