namespace ProfileRefresher;

/// <summary>The work done for one profile (refresh a tab, set the cruising status, …).</summary>
public delegate Task<RefreshOutcome> ProfileAction(BrowserProfile profile, List<BrowserWindow> windows, CancellationToken ct);

/// <summary>What one loop (one pass through all profiles) does.</summary>
public sealed record LoopStep(string Name, string ActionLabel, ProfileAction Action);

public sealed class RunSettings
{
    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }

    /// <summary>Returns the step for a 1-based loop number (lets a run mix refresh and I'm hosting passes).</summary>
    public required Func<int, LoopStep> StepForLoop { get; init; }
    public required TimeSpan Delay { get; init; }
    public TimeSpan Jitter { get; init; } = TimeSpan.Zero;
    public required int Loops { get; init; }
}

/// <summary>
/// "Chat &amp; reply" schedule: chat passes until one reads no new chats, then one refresh pass, repeating.
/// An I'm hosting pass runs every <see cref="HostingEvery"/>, but only once the current pass has finished.
/// </summary>
public sealed class ContinuousSettings
{
    public required IReadOnlyList<BrowserProfile> Profiles { get; init; }
    public required LoopStep ChatStep { get; init; }
    public required LoopStep RefreshStep { get; init; }
    public required LoopStep HostingStep { get; init; }
    public required TimeSpan HostingEvery { get; init; }
    public required TimeSpan Delay { get; init; }
    public TimeSpan Jitter { get; init; } = TimeSpan.Zero;
}

/// <summary>Pause gate: awaiting it completes immediately unless paused.</summary>
public sealed class PauseController
{
    private TaskCompletionSource? _gate;

    public bool IsPaused => Volatile.Read(ref _gate) is not null;

    public void Pause() =>
        Interlocked.CompareExchange(ref _gate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    public void Resume() => Interlocked.Exchange(ref _gate, null)?.TrySetResult();

    public Task WaitIfPausedAsync(CancellationToken ct) =>
        Volatile.Read(ref _gate) is { } g ? g.Task.WaitAsync(ct) : Task.CompletedTask;
}

/// <summary>
/// Runs the sequence: for each loop, for each selected profile → run the action → wait.
/// Terminating cancels further actions only; browsers and profile data are never touched.
/// </summary>
public sealed class RefreshRunner
{
    private readonly BrowserScanner _scanner;

    public RefreshRunner(BrowserScanner scanner) => _scanner = scanner;

    public PauseController Pause { get; } = new();
    public event Action<string>? Log;
    public event Action<string>? Status;

    public int Succeeded { get; private set; }
    public int Failed { get; private set; }

    public async Task RunAsync(RunSettings s, CancellationToken ct)
    {
        int count = s.Profiles.Count;

        for (int loop = 1; loop <= s.Loops; loop++)
        {
            var loopStep = s.StepForLoop(loop);
            Log?.Invoke($"Loop {loop} of {s.Loops}: {loopStep.Name}");

            for (int i = 0; i < count; i++)
            {
                var profile = s.Profiles[i];
                string position = $"Loop {loop}/{s.Loops} ({loopStep.Name}) · Profile {i + 1}/{count}";

                await RunOneAsync(profile, loopStep, position, $"{loop}/{s.Loops} {loopStep.Name}", ct);

                bool isLast = loop == s.Loops && i == count - 1;
                if (!isLast)
                {
                    var wait = s.Delay;
                    if (s.Jitter > TimeSpan.Zero)
                        wait += TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * s.Jitter.TotalMilliseconds);
                    await DelayAsync(wait, position, ct);
                }
            }

            Log?.Invoke($"Loop {loop} of {s.Loops} ({loopStep.Name}) complete.");
        }
    }

    /// <summary>Runs until cancelled. See <see cref="ContinuousSettings"/>.</summary>
    public async Task RunContinuousAsync(ContinuousSettings s, CancellationToken ct)
    {
        var nextHosting = DateTimeOffset.Now + s.HostingEvery;
        Log?.Invoke($"First I'm hosting pass at about {nextHosting:HH:mm}, then every {s.HostingEvery.TotalMinutes:0.##} min.");

        for (int cycle = 1; ; cycle++)
        {
            // Keep doing chat passes while they still find new chats (replies can bring new messages).
            for (int round = 1; ; round++)
            {
                bool found = await RunPassAsync(s, s.ChatStep, $"Cycle {cycle} · chats {round}", ct);
                nextHosting = await HostingIfDueAsync(s, nextHosting, ct);
                if (!found) break;
                Log?.Invoke("New chats were read in this pass; checking all profiles again.");
            }

            Log?.Invoke("No new chats on any profile: refresh pass.");
            await RunPassAsync(s, s.RefreshStep, $"Cycle {cycle} · refresh", ct);
            nextHosting = await HostingIfDueAsync(s, nextHosting, ct);
        }
    }

    /// <summary>Runs the I'm hosting pass if it's due. Returns when the next one is due.</summary>
    private async Task<DateTimeOffset> HostingIfDueAsync(ContinuousSettings s, DateTimeOffset due, CancellationToken ct)
    {
        if (DateTimeOffset.Now < due) return due;

        var started = DateTimeOffset.Now;
        Log?.Invoke($"I'm hosting is due (was due {due:HH:mm}).");
        await RunPassAsync(s, s.HostingStep, "I'm hosting", ct);
        var next = started + s.HostingEvery;
        Log?.Invoke($"Next I'm hosting pass at about {next:HH:mm}.");
        return next;
    }

    /// <summary>One pass through every profile. Returns true if any profile reported work done.</summary>
    private async Task<bool> RunPassAsync(ContinuousSettings s, LoopStep step, string label, CancellationToken ct)
    {
        bool any = false;
        int count = s.Profiles.Count;
        for (int i = 0; i < count; i++)
        {
            string position = $"{label} · Profile {i + 1}/{count}";
            var outcome = await RunOneAsync(s.Profiles[i], step, position, label, ct);
            any |= outcome.DidWork;

            var wait = s.Delay;
            if (s.Jitter > TimeSpan.Zero)
                wait += TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * s.Jitter.TotalMilliseconds);
            await DelayAsync(wait, position, ct);
        }
        return any;
    }

    /// <summary>Runs one step for one profile and logs the outcome.</summary>
    private async Task<RefreshOutcome> RunOneAsync(BrowserProfile profile, LoopStep step, string position,
        string logTag, CancellationToken ct)
    {
        await WaitWhilePausedAsync(position, ct);
        ct.ThrowIfCancellationRequested();

        Status?.Invoke($"{position} · {step.ActionLabel} {profile.DisplayName}…");

        // Re-scan windows each time so closed/moved windows are handled.
        var windows = await Task.Run(() => _scanner.Scan(readCommandLines: false).Windows
            .Where(w => w.ProfileKey == profile.Key)
            .ToList(), CancellationToken.None);

        RefreshOutcome outcome;
        try
        {
            outcome = await step.Action(profile, windows, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome = new RefreshOutcome(false, "error: " + ex.Message);
        }

        if (outcome.Ok) Succeeded++; else Failed++;
        Log?.Invoke($"[{logTag}] {profile.DisplayName}: {outcome.Message}");
        return outcome;
    }

    private async Task WaitWhilePausedAsync(string position, CancellationToken ct)
    {
        if (!Pause.IsPaused) return;
        Status?.Invoke($"{position} · paused");
        await Pause.WaitIfPausedAsync(ct);
    }

    /// <summary>Countdown that freezes while paused and stops instantly on terminate.</summary>
    private async Task DelayAsync(TimeSpan delay, string position, CancellationToken ct)
    {
        var tick = TimeSpan.FromMilliseconds(250);
        var remaining = delay;
        int lastShown = -1;

        while (remaining > TimeSpan.Zero)
        {
            if (Pause.IsPaused)
            {
                await WaitWhilePausedAsync(position, ct);
                lastShown = -1;
            }

            int secs = (int)Math.Ceiling(remaining.TotalSeconds);
            if (secs != lastShown)
            {
                Status?.Invoke($"{position} · next profile in {secs}s");
                lastShown = secs;
            }

            var step = remaining < tick ? remaining : tick;
            await Task.Delay(step, ct);
            remaining -= step;
        }
    }
}
