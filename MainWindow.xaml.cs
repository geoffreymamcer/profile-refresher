using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;

namespace ProfileRefresher;

public partial class MainWindow : Window
{
    private const int MaxLogLines = 500;

    private readonly ObservableCollection<BrowserProfile> _profiles = new();
    private readonly ObservableCollection<string> _log = new();
    private readonly AppSettings _settings = AppSettings.Load();

    private BrowserScanner? _scanner;
    private RefreshRunner? _runner;
    private CancellationTokenSource? _cts;
    private bool _detecting;

    public MainWindow()
    {
        InitializeComponent();

        CreditText.Text = "© " + Credits.Text;
        if (!Credits.IsIntact(CreditText.Text[2..]) || !Title.Contains("Jeymeyzer"))
        {
            MessageBox.Show("This copy of the app has been modified and will close.\n\n" + Credits.Text,
                "Browser Profile Refresher", MessageBoxButton.OK, MessageBoxImage.Error);
            Application.Current.Shutdown();
            return;
        }

        BrowserCombo.ItemsSource = new[] { BrowserKind.Chrome, BrowserKind.Edge };
        ProfileList.ItemsSource = _profiles;
        LogList.ItemsSource = _log;

        BrowserCombo.SelectedIndex = Math.Clamp(_settings.BrowserIndex, 0, 1);
        TaskCombo.SelectedIndex = Math.Clamp(_settings.TaskIndex, 0, 3);
        EveryBox.Text = _settings.RefreshesBeforeHosting.ToString(CultureInfo.CurrentCulture);
        OrderCombo.SelectedIndex = _settings.OrderMode switch { "opened" => 1, "name" => 2, _ => 0 };

        DelayBox.Text = _settings.DelaySeconds.ToString(CultureInfo.CurrentCulture);
        JitterBox.Text = _settings.JitterSeconds.ToString(CultureInfo.CurrentCulture);
        LoopsBox.Text = _settings.Loops.ToString(CultureInfo.CurrentCulture);

        TitleTabRadio.IsChecked = _settings.UseTitleFilter;
        ActiveTabRadio.IsChecked = !_settings.UseTitleFilter;
        TitleBox.Text = _settings.TitleFilter;
        KeyFallbackCheck.IsChecked = _settings.AllowKeyFallback;

        StatusBox.Text = _settings.TargetStatus;
        RowLabelBox.Text = _settings.StatusRowLabel;
        TabMatchBox.Text = _settings.SiteTabMatch;
        HotkeyBox.Text = _settings.Hotkey;
        StepTimeoutBox.Text = _settings.StepTimeoutSeconds.ToString(CultureInfo.CurrentCulture);

        AddLog("Browser Profile Refresher · " + Credits.Text);
        UpdateTaskPanels();
        TaskCombo.SelectionChanged += (_, _) => UpdateTaskPanels();
        EveryBox.TextChanged += (_, _) => UpdatePatternPreview();
        LoopsBox.TextChanged += (_, _) => UpdatePatternPreview();
        OrderCombo.SelectionChanged += async (_, _) =>
        {
            if (OrderMode == "taskbar") await ReadTaskbarPositionsAsync(_profiles.ToList());
            ApplyOrder(_profiles.ToList());
            AddLog("Run order: " + OrderMode switch
            {
                "taskbar" => "by taskbar position (left to right).",
                "opened" => "by time opened.",
                _ => "by profile name.",
            });
            LogOrder();
        };
        BrowserCombo.SelectionChanged += async (_, _) => await DetectAsync();
        Loaded += async (_, _) => await DetectAsync();
        SetRunning(false);
    }

    private BrowserKind SelectedKind => (BrowserKind)BrowserCombo.SelectedItem;
    private string OrderMode => OrderCombo.SelectedIndex switch { 1 => "opened", 2 => "name", _ => "taskbar" };

    /// <summary>Reads the taskbar and records each profile's button position.</summary>
    private async Task ReadTaskbarPositionsAsync(List<BrowserProfile> profiles)
    {
        var buttons = await Task.Run(() => TaskbarOrder.Read());
        foreach (var p in profiles)
        {
            int i = TaskbarOrder.IndexOf(buttons, p);
            p.TaskbarIndex = i >= 0 ? i : null;
        }

        int missing = profiles.Count(p => p.TaskbarIndex is null);
        if (buttons.Count == 0)
            AddLog("Couldn't read the taskbar; profiles will use name order.");
        else if (missing > 0)
            AddLog($"{missing} profile(s) not matched to a taskbar button; they'll go last.");
    }

    private void LogOrder()
    {
        foreach (var p in _profiles) AddLog($"   {p.Position}. {p.DisplayName} — {p.Details}");
    }

    /// <summary>Sorts profiles into the chosen run order and numbers them.</summary>
    private void ApplyOrder(IEnumerable<BrowserProfile> profiles)
    {
        var ordered = OrderMode switch
        {
            "taskbar" => BrowserScanner.SortByTaskbar(profiles),
            "opened" => BrowserScanner.SortByOpened(profiles),
            _ => BrowserScanner.SortByName(profiles),
        };
        for (int i = 0; i < ordered.Count; i++) ordered[i].Position = i + 1;

        _profiles.Clear();
        foreach (var p in ordered) _profiles.Add(p);
    }
    private bool IsRefreshTask => TaskCombo.SelectedIndex == 0;
    private bool IsStatusTask => TaskCombo.SelectedIndex == 1;
    private bool IsCombinedTask => TaskCombo.SelectedIndex == 2;
    private bool IsReadTask => TaskCombo.SelectedIndex == 3;

    private static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateTaskPanels()
    {
        RefreshPanel.Visibility = Show(IsRefreshTask || IsCombinedTask);
        StatusPanel.Visibility = Show(IsStatusTask || IsCombinedTask);
        CombinedPanel.Visibility = Show(IsCombinedTask);
        ReadPanel.Visibility = Show(IsReadTask);
        ExtensionPanel.Visibility = Show(IsStatusTask || IsCombinedTask || IsReadTask);
        LoopsHint.Text = IsCombinedTask ? "total passes (refresh + I'm hosting), then stop" : "complete passes, then stop";
        UpdatePatternPreview();
    }

    private void UpdatePatternPreview()
    {
        if (PatternText is null) return;
        bool okEvery = int.TryParse(EveryBox.Text.Trim(), out int every) && every >= 1;
        bool okLoops = int.TryParse(LoopsBox.Text.Trim(), out int loops) && loops >= 1;
        PatternText.Text = okEvery && okLoops ? "Order: " + PatternPreview(every, loops) : "";
    }

    /// <summary>E.g. "R R R R R H R …" for every = 5.</summary>
    private static string PatternPreview(int every, int loops)
    {
        const int shown = 14;
        var parts = Enumerable.Range(1, Math.Min(loops, shown))
            .Select(i => i % (every + 1) == 0 ? "H" : "R");
        string text = string.Join(" ", parts) + (loops > shown ? " …" : "");
        int hosting = loops / (every + 1);
        return $"{text}   ({loops - hosting} refresh, {hosting} I'm hosting)";
    }

    // ---------- Detection ----------

    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        if (_cts is not null || _detecting) return;
        _detecting = true;
        DetectButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        TestButton.IsEnabled = false;

        var kind = SelectedKind;
        StatusText.Text = $"Detecting open {kind.Name} profiles…";
        try
        {
            var scanner = new BrowserScanner(kind);
            var result = await Task.Run(() => scanner.Scan(readCommandLines: true));
            _scanner = scanner;

            await ReadTaskbarPositionsAsync(result.Profiles);
            ApplyOrder(result.Profiles);

            StatusText.Text = result.Profiles.Count == 0
                ? $"No open {kind.Name} windows found. Open the browser, then click Detect profiles."
                : $"Found {result.Profiles.Count} open profile(s) in {result.Windows.Count} window(s).";
            AddLog($"Detect ({kind.Name}): {result.Profiles.Count} profile(s), {result.Windows.Count} window(s).");
            LogOrder();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Detection failed.";
            AddLog("Detection error: " + ex.Message);
        }
        finally
        {
            _detecting = false;
            DetectButton.IsEnabled = true;
            StartButton.IsEnabled = true;
            TestButton.IsEnabled = true;
        }
    }

    // ---------- Run control ----------

    private async void Start_Click(object sender, RoutedEventArgs e) => await StartRunAsync(testOnly: false);

    private async void Test_Click(object sender, RoutedEventArgs e) => await StartRunAsync(testOnly: true);

    private async Task StartRunAsync(bool testOnly)
    {
        if (_cts is not null || _scanner is null) return;
        if (!TryReadCommon(out var delay, out var jitter, out var loops)) return;

        // The taskbar may have been rearranged since Detect: read it again so the run matches what you see.
        if (OrderMode == "taskbar" && !testOnly)
        {
            await ReadTaskbarPositionsAsync(_profiles.ToList());
            ApplyOrder(_profiles.ToList());
        }

        var selected = _profiles.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "Tick at least one detected profile.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (testOnly)
        {
            selected = selected.Take(1).ToList();
            loops = 1;
        }

        bool wantsRefresh = IsRefreshTask || IsCombinedTask;
        bool wantsHosting = IsStatusTask || IsCombinedTask;
        bool wantsRead = IsReadTask;
        bool wantsExtension = wantsHosting || wantsRead;

        int every = 0;
        if (IsCombinedTask)
        {
            if (!TryReadEvery(out every)) return;
            if (testOnly)
            {
                every = 1;  // test = one refresh pass, then one I'm hosting pass
                loops = 2;
            }
        }

        // Read and check every option before starting anything.
        TabTarget target = new(TargetMode.ActiveTab, "");
        if (wantsRefresh && !TryReadTarget(out target)) return;

        string status = "", rowLabel = "";
        if (wantsHosting && !TryReadStatusFields(out status, out rowLabel)) return;

        string tabMatch = "";
        Hotkey? hotkey = null;
        int stepMs = 10_000;
        if (wantsExtension && !TryReadExtensionOptions(out tabMatch, out hotkey, out stepMs)) return;

        LoopStep? refreshStep = null;
        LoopStep? hostingStep = null;
        LoopStep? readStep = null;
        ExtensionBridge? bridge = null;

        if (wantsRefresh)
        {
            bool fallback = KeyFallbackCheck.IsChecked == true;
            var t = target;
            refreshStep = new LoopStep(
                "Refresh",
                "refreshing",
                (profile, windows, ct) => Task.Run(() => TabRefresher.Refresh(windows, t, fallback)));
        }

        if (wantsExtension)
        {
            bridge = new ExtensionBridge();
            try
            {
                bridge.Start();
            }
            catch (HttpListenerException ex)
            {
                bridge.Dispose();
                MessageBox.Show(this,
                    $"Could not start the local connection on port {ExtensionBridge.Port}: {ex.Message}\n\n" +
                    "Is another copy of the app already running?", Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        var activeBridge = bridge;
        var hk = hotkey;

        // Brings the profile's window to the front and presses the extension shortcut.
        Func<Task<bool>> TriggerFor(IntPtr hwnd) => () => Task.Run(() =>
        {
            // Never press the shortcut unless this profile's window is really in front,
            // otherwise another profile's extension would pick up the job.
            if (!Native.FocusWindow(hwnd)) return false;
            Thread.Sleep(300);
            if (Native.GetForegroundWindow() != hwnd) return false;
            hk!.Send();
            return true;
        });

        if (wantsHosting)
        {
            hostingStep = new LoopStep("I'm hosting", "updating", async (profile, windows, ct) =>
            {
                if (windows.Count == 0) return new RefreshOutcome(false, "no open window for this profile (skipped)");
                var job = new ExtensionJob(Guid.NewGuid().ToString("N"), "hosting", tabMatch, stepMs, status, rowLabel);
                var outcome = await activeBridge!.RunJobAsync(job, TriggerFor(windows[0].Hwnd), ct);
                return outcome.ToRefreshOutcome();
            });
        }

        if (wantsRead)
        {
            readStep = new LoopStep("Read chats", "checking messages on", (profile, windows, ct) =>
                ReadChatsAsync(profile, windows, activeBridge!, tabMatch, stepMs, TriggerFor, ct));
        }

        Func<int, LoopStep> stepForLoop;
        string description;
        if (IsCombinedTask)
        {
            var r = refreshStep!;
            var h = hostingStep!;
            int cycle = every + 1;
            stepForLoop = loop => loop % cycle == 0 ? h : r;
            description = $"{every} refresh pass(es), then 1 I'm hosting pass, repeating ({PatternPreview(every, loops)})";
        }
        else if (IsStatusTask)
        {
            var h = hostingStep!;
            stepForLoop = _ => h;
            description = $"set status to \"{status}\" and save";
        }
        else if (IsReadTask)
        {
            var rd = readStep!;
            stepForLoop = _ => rd;
            description = "check for new messages and read unread chats (nothing is sent)";
        }
        else
        {
            var r = refreshStep!;
            stepForLoop = _ => r;
            description = target.Mode == TargetMode.TitleContains
                ? $"refresh tab whose title contains \"{target.Text}\""
                : "refresh active tab";
        }

        SaveSettings();

        var settings = new RunSettings
        {
            Profiles = selected,
            StepForLoop = stepForLoop,
            Delay = delay,
            Jitter = jitter,
            Loops = loops,
        };

        _runner = new RefreshRunner(_scanner);
        _runner.Log += AddLog;
        _runner.Status += s => StatusText.Text = s;
        _cts = new CancellationTokenSource();
        SetRunning(true);

        AddLog($"{(testOnly ? "Test" : "Started")}: {description}; {selected.Count} profile(s) × {loops} loop(s), " +
               $"{delay.TotalSeconds:0.##}s delay" + (jitter > TimeSpan.Zero ? $" + up to {jitter.TotalSeconds:0.##}s random." : "."));

        string final;
        try
        {
            await _runner.RunAsync(settings, _cts.Token);
            final = testOnly ? "Test finished." : $"Finished all {loops} loop(s).";
        }
        catch (OperationCanceledException)
        {
            final = "Terminated. Nothing further is scheduled.";
        }
        catch (Exception ex)
        {
            final = "Stopped because of an error: " + ex.Message;
        }
        finally
        {
            bridge?.Dispose();
        }

        final += $"  ({_runner.Succeeded} succeeded, {_runner.Failed} failed/skipped)";
        AddLog(final);
        StatusText.Text = final;

        _cts.Dispose();
        _cts = null;
        _runner = null;
        SetRunning(false);
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        if (_runner.Pause.IsPaused)
        {
            _runner.Pause.Resume();
            PauseButton.Content = "Pause";
            AddLog("Resumed.");
        }
        else
        {
            _runner.Pause.Pause();
            PauseButton.Content = "Resume";
            StatusText.Text = "Paused. Will pause before the next profile.";
            AddLog("Paused.");
        }
    }

    private void Terminate_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        TerminateButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        StatusText.Text = "Terminating…";
        _cts.Cancel();
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        TestButton.IsEnabled = !running;
        PauseButton.IsEnabled = running;
        TerminateButton.IsEnabled = running;
        PauseButton.Content = "Pause";

        BrowserCombo.IsEnabled = !running;
        TaskCombo.IsEnabled = !running;
        DetectButton.IsEnabled = !running;
        ProfileList.IsEnabled = !running;
        OrderCombo.IsEnabled = !running;
        OptionsBox.IsEnabled = !running;
    }

    // ---------- Option parsing ----------

    private bool TryReadCommon(out TimeSpan delay, out TimeSpan jitter, out int loops)
    {
        delay = jitter = default;
        loops = 0;

        if (!TryParseDouble(DelayBox.Text, out double secs) || secs < 0.5 || secs > 86_400)
            return Fail("Delay must be a number of seconds between 0.5 and 86400.", DelayBox);

        if (!TryParseDouble(JitterBox.Text, out double jit) || jit < 0 || jit > 3600)
            return Fail("Random extra delay must be between 0 and 3600 seconds.", JitterBox);

        if (!int.TryParse(LoopsBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out loops) ||
            loops < 1 || loops > 100_000)
            return Fail("Number of loops must be a whole number between 1 and 100000.", LoopsBox);

        delay = TimeSpan.FromSeconds(secs);
        jitter = TimeSpan.FromSeconds(jit);
        return true;
    }

    private bool TryReadEvery(out int every)
    {
        if (!int.TryParse(EveryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out every) ||
            every < 1 || every > 10_000)
            return Fail("Refresh passes before each I'm hosting pass must be a whole number of 1 or more.", EveryBox);
        return true;
    }

    private bool TryReadTarget(out TabTarget target)
    {
        target = new TabTarget(TargetMode.ActiveTab, "");
        if (TitleTabRadio.IsChecked != true) return true;

        var text = TitleBox.Text.Trim();
        if (text.Length == 0) return Fail("Enter the text the tab title should contain.", TitleBox);
        target = new TabTarget(TargetMode.TitleContains, text);
        return true;
    }

    private bool TryReadStatusFields(out string status, out string rowLabel)
    {
        status = StatusBox.Text.Trim();
        rowLabel = RowLabelBox.Text.Trim();
        if (status.Length == 0) return Fail("Enter the status to set, e.g. I'm hosting.", StatusBox);
        if (rowLabel.Length == 0) return Fail("Enter the label shown next to the status field, e.g. Hosting Status.", RowLabelBox);
        return true;
    }

    private bool TryReadExtensionOptions(out string tabMatch, out Hotkey? hotkey, out int stepMs)
    {
        tabMatch = TabMatchBox.Text.Trim();
        hotkey = null;
        stepMs = 0;

        if (!Hotkey.TryParse(HotkeyBox.Text, out var hk) || hk is null)
            return Fail("Shortcut must look like Alt+Shift+H or Ctrl+Shift+Y (Ctrl or Alt, plus a letter or digit).", HotkeyBox);
        hotkey = hk;

        if (!TryParseDouble(StepTimeoutBox.Text, out double secs) || secs < 3 || secs > 60)
            return Fail("Wait per step must be between 3 and 60 seconds.", StepTimeoutBox);
        stepMs = (int)(secs * 1000);
        return true;
    }

    private bool Fail(string message, Control field)
    {
        MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        field.Focus();
        return false;
    }

    private static bool TryParseDouble(string s, out double value) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // ---------- Read chats (Stage 1: read only) ----------

    private static async Task<RefreshOutcome> ReadChatsAsync(BrowserProfile profile, List<BrowserWindow> windows,
        ExtensionBridge bridge, string tabMatch, int stepMs, Func<IntPtr, Func<Task<bool>>> triggerFor,
        CancellationToken ct)
    {
        if (windows.Count == 0) return new RefreshOutcome(false, "no open window for this profile (skipped)");

        var job = new ExtensionJob(Guid.NewGuid().ToString("N"), "readChats", tabMatch, stepMs,
            SkipUserIds: ConversationStore.ReviewUserIds(profile), MaxChats: 25);
        var outcome = await bridge.RunJobAsync(job, triggerFor(windows[0].Hwnd), ct, TimeSpan.FromMinutes(8));

        var data = ConversationStore.ParseReadChats(outcome.Data);
        if (data is null) return outcome.ToRefreshOutcome();
        if (outcome.Ok && !data.HadDot && data.Chats.Count == 0) return new RefreshOutcome(true, "no new messages");

        var lines = new List<string>();
        foreach (var chat in data.Chats)
        {
            try
            {
                var r = ConversationStore.Merge(profile, chat);
                var c = r.Conversation;
                lines.Add($"• user {c.UserId}{(r.IsNewContact ? " (new contact)" : "")}: " +
                          $"+{r.NewMessages} new, {c.ReceivedCount} received / {c.SentCount} sent (text only)" +
                          (r.NewlyNeedsReview ? "  → NEEDS REVIEW (10/10 reached)" :
                           c.Status == "needs_review" ? "  → needs review" : ""));
            }
            catch (Exception ex)
            {
                lines.Add($"• user {chat.UserId}: could not save ({ex.Message})");
            }
        }
        foreach (var e in data.Errors)
            lines.Add($"• problem at \"{e.Step}\"{(string.IsNullOrEmpty(e.UserId) ? "" : $" (user {e.UserId})")}: {e.Message}");

        string summary = $"{data.Chats.Count} chat(s) read";
        if (data.SkippedReview.Count > 0) summary += $", {data.SkippedReview.Count} needs-review chat(s) skipped";
        if (data.SkippedAds > 0) summary += ", ad skipped";
        if (!data.ClosedList) summary += ", WARNING: chat list may still be open";
        if (!outcome.Ok && data.Errors.Count == 0) summary += $" ({outcome.Message})";

        string message = lines.Count == 0 ? summary : summary + "\n      " + string.Join("\n      ", lines);
        return new RefreshOutcome(outcome.Ok, message);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConversationStore.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                "\"" + ConversationStore.Root + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddLog("Could not open the chat logs folder: " + ex.Message);
        }
    }

    // ---------- Misc ----------

    private void AddLog(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.InvokeAsync(() => AddLog(line));
            return;
        }
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > MaxLogLines) _log.RemoveAt(0);
        LogList.ScrollIntoView(_log[^1]);
    }

    private void SaveSettings()
    {
        _settings.BrowserIndex = BrowserCombo.SelectedIndex;
        _settings.TaskIndex = TaskCombo.SelectedIndex;
        _settings.OrderMode = OrderMode;
        if (int.TryParse(EveryBox.Text.Trim(), out var ev) && ev >= 1) _settings.RefreshesBeforeHosting = ev;

        if (TryParseDouble(DelayBox.Text, out var d)) _settings.DelaySeconds = d;
        if (TryParseDouble(JitterBox.Text, out var j)) _settings.JitterSeconds = j;
        if (int.TryParse(LoopsBox.Text.Trim(), out var l)) _settings.Loops = l;

        _settings.UseTitleFilter = TitleTabRadio.IsChecked == true;
        _settings.TitleFilter = TitleBox.Text.Trim();
        _settings.AllowKeyFallback = KeyFallbackCheck.IsChecked == true;

        _settings.TargetStatus = StatusBox.Text.Trim();
        _settings.StatusRowLabel = RowLabelBox.Text.Trim();
        _settings.SiteTabMatch = TabMatchBox.Text.Trim();
        _settings.Hotkey = HotkeyBox.Text.Trim();
        if (TryParseDouble(StepTimeoutBox.Text, out var st)) _settings.StepTimeoutSeconds = st;

        _settings.Save();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _cts?.Cancel(); // stops scheduling only; browsers are left untouched
        SaveSettings();
    }
}
