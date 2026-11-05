using System.Windows.Automation;

namespace ProfileRefresher;

public enum TargetMode { ActiveTab, TitleContains }

public sealed record TabTarget(TargetMode Mode, string Text);

public sealed record RefreshOutcome(bool Ok, string Message);

/// <summary>
/// Refreshes one tab in a browser window through UI Automation by invoking the toolbar's
/// Reload button. No focus change, no extension and no remote-debugging port are needed.
/// Web page content is never walked, which keeps each refresh cheap.
/// </summary>
public static class TabRefresher
{
    private const int MaxNodes = 4000;
    private const int MaxDepth = 30;

    public static RefreshOutcome Refresh(IReadOnlyList<BrowserWindow> windows, TabTarget target, bool allowKeyFallback)
    {
        if (windows.Count == 0) return new(false, "no open window for this profile (skipped)");

        if (target.Mode == TargetMode.ActiveTab)
            return RefreshWindow(windows[0], null, allowKeyFallback, out _);

        foreach (var w in windows)
        {
            var outcome = RefreshWindow(w, target.Text, allowKeyFallback, out bool tabFound);
            if (tabFound) return outcome;
        }
        return new(false, $"no tab whose title contains \"{target.Text}\" (skipped)");
    }

    private static RefreshOutcome RefreshWindow(BrowserWindow w, string? titleFilter, bool allowKeyFallback, out bool tabFound)
    {
        tabFound = titleFilter is null;
        if (!Native.IsWindow(w.Hwnd)) return new(false, "window was closed (skipped)");

        AutomationElement root;
        try { root = AutomationElement.FromHandle(w.Hwnd); }
        catch (Exception ex) { return new(false, "cannot access window: " + ex.Message); }

        // Chromium builds its accessibility tree lazily on first query, so allow one retry.
        var (reload, tabs) = Walk(root, titleFilter is not null);
        if (reload is null || (titleFilter is not null && tabs.Count == 0))
        {
            Thread.Sleep(700);
            (reload, tabs) = Walk(root, titleFilter is not null);
        }

        string tabLabel = "active tab";
        if (titleFilter is not null)
        {
            AutomationElement? tab = null;
            foreach (var t in tabs)
            {
                string name = SafeName(t);
                if (name.Contains(titleFilter, StringComparison.OrdinalIgnoreCase)) { tab = t; tabLabel = $"tab \"{Shorten(name)}\""; break; }
            }
            if (tab is null) return new(false, "tab not found");
            tabFound = true;

            if (!SelectTab(tab)) return new(false, $"could not switch to {tabLabel}");
            Thread.Sleep(350);
        }

        if (reload is not null)
        {
            // While a page loads, the same button is "Stop". Wait for it to turn back into "Reload".
            for (int i = 0; i < 20 && IsStopState(reload); i++) Thread.Sleep(500);

            if (!IsStopState(reload) &&
                reload.TryGetCurrentPattern(InvokePattern.Pattern, out var p))
            {
                try
                {
                    ((InvokePattern)p).Invoke();
                    return new(true, $"refreshed {tabLabel}");
                }
                catch (Exception ex) when (!allowKeyFallback)
                {
                    return new(false, "reload button failed: " + ex.Message);
                }
                catch
                {
                    // fall through to key fallback
                }
            }
        }

        if (allowKeyFallback && PressF5(w.Hwnd))
            return new(true, $"refreshed {tabLabel} (F5 fallback)");

        return new(false, reload is null
            ? "reload button not found (enable the F5 fallback option)"
            : "page still loading; refresh skipped");
    }

    private static (AutomationElement? Reload, List<AutomationElement> Tabs) Walk(AutomationElement root, bool wantTabs)
    {
        AutomationElement? reload = null;
        var tabs = new List<AutomationElement>();
        var walker = TreeWalker.ControlViewWalker;
        var queue = new Queue<(AutomationElement El, int Depth)>();
        queue.Enqueue((root, 0));
        int visited = 0;

        while (queue.Count > 0 && visited < MaxNodes)
        {
            var (el, depth) = queue.Dequeue();
            visited++;

            ControlType type;
            string cls, name;
            try
            {
                type = el.Current.ControlType;
                cls = el.Current.ClassName ?? "";
                name = el.Current.Name ?? "";
            }
            catch { continue; }

            if (type == ControlType.Document) continue; // never descend into web content

            if (reload is null && type == ControlType.Button && IsReloadButton(cls, name)) reload = el;
            else if (wantTabs && type == ControlType.TabItem) tabs.Add(el);

            if (reload is not null && !wantTabs) break;
            if (depth >= MaxDepth) continue;

            try
            {
                var child = walker.GetFirstChild(el);
                while (child is not null)
                {
                    queue.Enqueue((child, depth + 1));
                    child = walker.GetNextSibling(child);
                }
            }
            catch { /* element vanished */ }
        }

        return (reload, tabs);
    }

    private static bool IsReloadButton(string cls, string name) =>
        cls == "ReloadButton" ||
        name.StartsWith("Reload", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Refresh", StringComparison.OrdinalIgnoreCase);

    private static bool IsStopState(AutomationElement reload) =>
        SafeName(reload).StartsWith("Stop", StringComparison.OrdinalIgnoreCase);

    private static bool SelectTab(AutomationElement tab)
    {
        try
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp))
            {
                ((SelectionItemPattern)sp).Select();
                return true;
            }
            if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
            {
                ((InvokePattern)ip).Invoke();
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Last resort: briefly focus the window, press F5, then give focus back.</summary>
    private static bool PressF5(IntPtr hwnd)
    {
        var previous = Native.GetForegroundWindow();
        if (!Native.FocusWindow(hwnd)) return false;

        Native.PressKey(Native.VK_F5);
        Thread.Sleep(120);

        if (previous != IntPtr.Zero && previous != hwnd) Native.SetForegroundWindow(previous);
        return true;
    }

    private static string SafeName(AutomationElement el)
    {
        try { return el.Current.Name ?? ""; } catch { return ""; }
    }

    private static string Shorten(string s) => s.Length <= 50 ? s : s[..49] + "…";
}
