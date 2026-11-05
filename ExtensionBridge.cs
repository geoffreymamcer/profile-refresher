using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>A job for the extension. Type "hosting" sets the cruising status; "readChats" reads unread chats.</summary>
public sealed record ExtensionJob(
    string Id,
    string Type,
    string TabMatch,
    int TimeoutMs,
    string TargetStatus = "",
    string StatusRowLabel = "",
    string[]? SkipUserIds = null,
    int MaxChats = 25);

public sealed record JobResult(string? Id, bool Ok, string? Step, string? Message, JsonElement? Data);

/// <summary>Outcome of a job, including any data the extension sent back.</summary>
public sealed record JobOutcome(bool Ok, string Message, JsonElement? Data)
{
    public RefreshOutcome ToRefreshOutcome() => new(Ok, Message);
}

/// <summary>
/// A tiny local web server (localhost only) that the helper extension talks to:
///   GET  /job     → the job for the profile currently being processed (handed out once)
///   POST /result  → the outcome of that job
/// It only runs while a "Post I'm hosting" run is active.
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
            lock (_lock)
            {
                if (_job is not null && !_jobTaken)
                {
                    job = _job;
                    _jobTaken = true; // a second key press must not run the steps twice
                    _picked?.TrySetResult();
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
    public async Task<JobOutcome> RunJobAsync(ExtensionJob job, Func<Task<bool>> trigger, CancellationToken ct,
        TimeSpan? overallTimeout = null)
    {
        var picked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var result = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _job = job;
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
