using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>
/// A job for the extension. Type "hosting" sets the cruising status; "readChats" reads unread chats
/// and, when ReplyMessages is set, replies with the next message of that sequence.
/// </summary>
public sealed record ExtensionJob(
    string Id,
    string Type,
    string TabMatch,
    int TimeoutMs,
    string TargetStatus = "",
    string StatusRowLabel = "",
    string[]? SkipUserIds = null,
    int MaxChats = 25,
    string[]? ReplyMessages = null,
    bool DryRun = false,
    int SendDelayMs = 2000);

public sealed record JobResult(string? Id, bool Ok, string? Step, string? Message, JsonElement? Data);

/// <summary>What the live reports say about a profile's Messages icon.</summary>
public enum DotState
{
    /// <summary>No recent report (extension not updated, tab loading or closed): check the profile directly.</summary>
    Unknown,
    NoDot,
    HasDot,
}

/// <summary>A live report from dotwatch.js, relayed by the extension.</summary>
public sealed record DotReport(string? Client, int TabId, bool HasButton, bool HasDot);

/// <summary>Outcome of a job, including any data the extension sent back.</summary>
public sealed record JobOutcome(bool Ok, string Message, JsonElement? Data)
{
    public RefreshOutcome ToRefreshOutcome() => new(Ok, Message);
}

/// <summary>
/// A tiny local web server (localhost only) that the helper extension talks to:
///   GET  /job?client=… → the job for the profile currently being processed (handed out once)
///   POST /result       → the outcome of that job
///   POST /status       → live "new messages" dot report from a site tab
/// It only runs while a run that uses the extension is active.
/// </summary>
public sealed class ExtensionBridge : IDisposable
{
    public const int Port = 47821;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener = new();
    private readonly object _lock = new();
    private ExtensionJob? _job;
    private bool _jobTaken;
    private TaskCompletionSource? _picked;
    private TaskCompletionSource<JobResult>? _result;
    private string? _jobProfileKey;

    // Which profile each copy of the extension belongs to (learned from jobs, kept between runs).
    private readonly Dictionary<string, string> _clients = LoadClients();
    // Latest dot report per (client, tab).
    private readonly Dictionary<(string Client, int TabId), (DotReport Report, DateTimeOffset At)> _reports = new();

    private static string ClientsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProfileRefresher", "extension-clients.json");

    private static Dictionary<string, string> LoadClients()
    {
        try
        {
            if (File.Exists(ClientsPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ClientsPath)) ?? new();
        }
        catch { }
        return new();
    }

    private void SaveClients()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ClientsPath)!);
            File.WriteAllText(ClientsPath, JsonSerializer.Serialize(_clients));
        }
        catch { }
    }

    /// <summary>True once the extension in this profile has run a job, so its reports can be matched to it.</summary>
    public bool IsKnown(string profileKey)
    {
        lock (_lock) return _clients.ContainsValue(profileKey);
    }

    /// <summary>What the profile's site tab(s) reported within <paramref name="maxAge"/>.</summary>
    public DotState GetDotState(string profileKey, TimeSpan maxAge)
    {
        var now = DateTimeOffset.Now;
        bool anyButton = false;
        lock (_lock)
        {
            foreach (var ((client, _), (report, at)) in _reports)
            {
                if (now - at > maxAge) continue;
                if (!_clients.TryGetValue(client, out var key) || key != profileKey) continue;
                if (report.HasDot) return DotState.HasDot;
                if (report.HasButton) anyButton = true;
            }
        }
        return anyButton ? DotState.NoDot : DotState.Unknown;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }

            try { await HandleAsync(ctx); }
            catch { /* ignore a bad request */ }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";

        // Only the extension may talk to us, never an ordinary web page.
        string? origin = req.Headers["Origin"];
        if (origin is not null)
        {
            if (!origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 403;
                return;
            }
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST";
            res.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        }

        string path = (req.Url?.AbsolutePath ?? "").TrimEnd('/');

        if (req.HttpMethod == "OPTIONS")
        {
            res.StatusCode = 204;
            return;
        }

        if (req.HttpMethod == "GET" && path == "/job")
        {
            ExtensionJob? job = null;
            string? client = req.QueryString["client"];
            lock (_lock)
            {
                if (_job is not null && !_jobTaken)
                {
                    job = _job;
                    _jobTaken = true; // a second key press must not run the steps twice
                    _picked?.TrySetResult();

                    // The job only goes to the profile whose window is in front, so this client is that profile.
                    if (!string.IsNullOrEmpty(client) && _jobProfileKey is not null &&
                        (!_clients.TryGetValue(client, out var known) || known != _jobProfileKey))
                    {
                        foreach (var other in _clients.Where(kv => kv.Value == _jobProfileKey && kv.Key != client)
                                     .Select(kv => kv.Key).ToList())
                            _clients.Remove(other); // the profile's extension was reinstalled
                        _clients[client] = _jobProfileKey;
                        SaveClients();
                    }
                }
            }

            if (job is null)
            {
                res.StatusCode = 204;
                return;
            }
            await WriteJsonAsync(res, JsonSerializer.Serialize(job, Json));
            return;
        }

        if (req.HttpMethod == "POST" && path == "/status")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            DotReport? report = null;
            try { report = JsonSerializer.Deserialize<DotReport>(body, Json); } catch { }

            if (report is { Client: { Length: > 0 and <= 100 } c })
            {
                lock (_lock)
                {
                    if (_reports.Count > 500) _reports.Clear(); // never grows without bound
                    _reports[(c, report.TabId)] = (report, DateTimeOffset.Now);
                }
            }
            res.StatusCode = 204;
            return;
        }

        if (req.HttpMethod == "POST" && path == "/result")
        {
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            string body = await reader.ReadToEndAsync();
            JobResult? result = null;
            try { result = JsonSerializer.Deserialize<JobResult>(body, Json); } catch { }

            lock (_lock)
            {
                if (result is not null && _job is not null && result.Id == _job.Id)
                    _result?.TrySetResult(result);
            }
            res.StatusCode = 204;
            return;
        }

        res.StatusCode = 404;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        res.StatusCode = 200;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    /// <param name="trigger">Brings the profile's window forward and presses the shortcut. Returns false if the window couldn't be focused.</param>
    /// <param name="overallTimeout">How long the whole job may take once picked up (default: based on the step timeout).</param>
    /// <param name="profileKey">The profile the job is for; used to match its extension's live reports.</param>
    public async Task<JobOutcome> RunJobAsync(ExtensionJob job, Func<Task<bool>> trigger, CancellationToken ct,
        TimeSpan? overallTimeout = null, string? profileKey = null)
    {
        var picked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _job = job;
            _jobProfileKey = profileKey;
            _jobTaken = false;
            _picked = picked;
            _result = result;
        }

        try
        {
            bool focused = false;
            for (int attempt = 1; attempt <= 2 && !picked.Task.IsCompleted; attempt++)
            {
                if (await trigger()) focused = true;
                else continue;

                try { await picked.Task.WaitAsync(TimeSpan.FromSeconds(6), ct); }
                catch (TimeoutException) { }
            }

            if (!picked.Task.IsCompleted)
            {
                return new JobOutcome(false, focused
                    ? "the extension did not respond. Check it is loaded in this profile and its shortcut is set (see README)"
                    : "could not bring this profile's window to the front (skipped)", null);
            }

            var limit = overallTimeout ?? TimeSpan.FromMilliseconds(job.TimeoutMs * 10L + 15_000);
            JobResult r;
            try { r = await result.Task.WaitAsync(limit, ct); }
            catch (TimeoutException) { return new JobOutcome(false, "timed out waiting for the extension to finish", null); }

            return r.Ok
                ? new JobOutcome(true, r.Message ?? "done", r.Data)
                : new JobOutcome(false, $"failed at \"{r.Step}\": {r.Message}", r.Data);
        }
        finally
        {
            lock (_lock)
            {
                _job = null;
                _jobProfileKey = null;
                _picked = null;
                _result = null;
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_listener.IsListening) _listener.Stop();
            _listener.Close();
        }
        catch { }
    }
}
