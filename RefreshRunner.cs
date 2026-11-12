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

    /// <summary>Wait after each profile in I'm hosting passes (plus up to <see cref="Jitter"/>).</summary>
    public required TimeSpan Delay { get; init; }
    public TimeSpan Jitter { get; init; } = TimeSpan.Zero;

    /// <summary>Wait after each profile opened in a chat pass.</summary>
    public TimeSpan ChatDelay { get; init; } = TimeSpan.FromSeconds(0.5);
    /// <summary>Wait after each profile in a refresh pass.</summary>
    public TimeSpan RefreshDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Live "new messages" state per profile, from the extension's reports. When set, chat passes
    /// only open profiles that have new messages (or haven't reported); the rest are skipped.
    /// </summary>
    public Func<BrowserProfile, DotState>? DotState { get; init; }
    /// <summary>Wait once at the start so the live reports can arrive.</summary>
    public TimeSpan StartupWait { get; init; } = TimeSpan.Zero;

    /// <summary>Runs after every profile, before the delay (used to send replies typed in Telegram).</summary>
    public Func<CancellationToken, Task>? AfterEachProfile { get; init; }
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

    // Profiles whose last chat pass left unread chats (or failed), with how many times in a row.
    // Their orange dot is already gone, so without this they'd wait until the next new message.
    private readonly Dictionary<string, int> _recheck = new();
    private const int MaxRechecks = 3;

    /// <summary>
    /// Have the next chat pass open this profile even though its orange dot is gone. Used after a phone
    /// reply: opening the chat list to send it clears the dot, so other new chats there would be missed.
    /// </summary>
    public void RequestChatCheck(string profileKey) => _recheck[profileKey] = 0;
    private LoopStep? _chatStep;

    public RefreshRunner(BrowserScanner scanner) => _scanner = scanner;

    public PauseController Pause { get; } = new();
    public event Action<string>? Log;
    public event Action<string>? Status;
    /// <summary>Someone replied after the whole message sequence was sent.</summary>
    public event Action<BrowserProfile, ReplyAlert>? Alert;

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
        _chatStep = s.ChatStep;
        _recheck.Clear();
        var nextHosting = DateTimeOffset.Now + s.HostingEvery;
        Log?.Invoke($"First I'm hosting pass at about {nextHosting:HH:mm}, then every {s.HostingEvery.TotalMinutes:0.##} min.");

        if (s.StartupWait > TimeSpan.Zero)
            await DelayAsync(s.StartupWait, "Waiting for the profiles to report new messages", ct);

        for (int cycle = 1; ; cycle++)
        {
            // Keep doing chat passes while they still find new chats (replies can bring new messages).
            for (int round = 1; ; round++)
            {
                var targets = ChatTargets(s);
                bool found = targets.Count > 0 &&
                             await RunPassAsync(targets, s.ChatStep, $"Cycle {cycle} · chats {round}", s.ChatDelay,
                                 TimeSpan.Zero, ct, s.AfterEachProfile);
                nextHosting = await HostingIfDueAsync(s, nextHosting, ct);
                if (!found) break;
                Log?.Invoke("New chats were read in this pass; checking again.");
            }

            Log?.Invoke("No new chats on any profile: refresh pass.");
            await RunPassAsync(s.Profiles, s.RefreshStep, $"Cycle {cycle} · refresh", s.RefreshDelay, TimeSpan.Zero, ct,
                s.AfterEachProfile);
            nextHosting = await HostingIfDueAsync(s, nextHosting, ct);
        }
    }

    /// <summary>A report older than this is not trusted (the tab may be closed, frozen or loading).</summary>
    private static readonly TimeSpan ReportMaxAge = TimeSpan.FromSeconds(75);

    private string _lastUnreported = "";

    /// <summary>Profiles a chat pass should open: those with new messages, and those without a live report.</summary>
    private List<BrowserProfile> ChatTargets(ContinuousSettings s)
    {
        if (s.DotState is null) return s.Profiles.ToList();

        var targets = new List<BrowserProfile>();
        var withDot = new List<string>();
        var unreported = new List<string>();
        foreach (var p in s.Profiles)
        {
            switch (s.DotState(p))
            {
                case DotState.HasDot:
                    targets.Add(p);
                    withDot.Add(p.DisplayName);
                    break;
                case DotState.Unknown:
                    targets.Add(p);
                    unreported.Add(p.DisplayName);
                    break;
            }
        }

        // Profiles that still had unread chats after their last pass (no dot any more).
        var again = new List<string>();
        foreach (var p in s.Profiles)
        {
            if (targets.Contains(p) || !_recheck.TryGetValue(p.Key, out int n)) continue;
            if (n <= MaxRechecks)
            {
                targets.Add(p);
                again.Add(p.DisplayName);
            }
        }

        if (withDot.Count > 0) Log?.Invoke($"Scan: new messages on {string.Join(", ", withDot)}.");
        if (again.Count > 0) Log?.Invoke($"Checking again for unread chats left on {string.Join(", ", again)}.");

        string un = string.Join(", ", unreported);
        if (un != _lastUnreported)
        {
            _lastUnreported = un;
            if (unreported.Count > 0)
                Log?.Invoke($"No live report from {un}: checking these one by one. (Reload the extension and the " +
                            "site tab there; a profile starts reporting after its first chat check.)");
            else
                Log?.Invoke("All profiles report new messages live.");
        }
        return targets;
    }

    /// <summary>Runs the I'm hosting pass if it's due. Returns when the next one is due.</summary>
    private async Task<DateTimeOffset> HostingIfDueAsync(ContinuousSettings s, DateTimeOffset due, CancellationToken ct)
    {
        if (DateTimeOffset.Now < due) return due;

        var started = DateTimeOffset.Now;
        Log?.Invoke($"I'm hosting is due (was due {due:HH:mm}).");
        await RunPassAsync(s.Profiles, s.HostingStep, "I'm hosting", s.Delay, s.Jitter, ct, s.AfterEachProfile);
        var next = started + s.HostingEvery;
        Log?.Invoke($"Next I'm hosting pass at about {next:HH:mm}.");
        return next;
    }

    /// <summary>One pass through the given profiles. Returns true if any profile reported work done.</summary>
    private async Task<bool> RunPassAsync(IReadOnlyList<BrowserProfile> profiles, LoopStep step, string label,
        TimeSpan delay, TimeSpan jitter, CancellationToken ct, Func<CancellationToken, Task>? afterEach = null)
    {
        bool any = false;
        int count = profiles.Count;
        for (int i = 0; i < count; i++)
        {
            string position = $"{label} · Profile {i + 1}/{count}";
            var outcome = await RunOneAsync(profiles[i], step, position, label, ct);
            any |= outcome.DidWork || (outcome.RecheckChats && _recheck.GetValueOrDefault(profiles[i].Key) <= MaxRechecks);
            if (afterEach is not null) await afterEach(ct);

            var wait = delay;
            if (jitter > TimeSpan.Zero)
                wait += TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * jitter.TotalMilliseconds);
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
        if (ReferenceEquals(step, _chatStep))
        {
            if (outcome.RecheckChats) _recheck[profile.Key] = _recheck.GetValueOrDefault(profile.Key) + 1;
            else _recheck.Remove(profile.Key);
        }
        Log?.Invoke($"[{logTag}] {profile.DisplayName}: {outcome.Message}");
        if (outcome.Alerts is { } alerts)
            foreach (var a in alerts) Alert?.Invoke(profile, a);
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
