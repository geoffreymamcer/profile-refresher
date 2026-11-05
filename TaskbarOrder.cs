using System.Windows.Automation;

namespace ProfileRefresher;

/// <summary>
/// Reads the Windows taskbar's app buttons in their on-screen order (left to right, or top to bottom
/// for a vertical taskbar) and matches each Chrome/Edge profile to its button.
/// Each profile gets its own taskbar button because each has its own AppUserModelID.
/// </summary>
public static class TaskbarOrder
{
    public sealed record Button(string Name, string AutomationId, double X, double Y);

    public static List<Button> Read()
    {
        var buttons = new List<Button>();
        try
        {
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return buttons;

            var root = AutomationElement.FromHandle(tray);
            var all = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            var candidates = new List<(Button B, bool IsAppButton)>();
            foreach (AutomationElement el in all)
            {
                try
                {
                    string cls = el.Current.ClassName ?? "";
                    string id = el.Current.AutomationId ?? "";
                    string name = el.Current.Name ?? "";
                    var r = el.Current.BoundingRectangle;
                    if (r.IsEmpty || r.Width <= 0) continue;

                    bool isApp = cls.Contains("TaskListButton", StringComparison.OrdinalIgnoreCase) ||
                                 id.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase);
                    candidates.Add((new Button(name, id, r.Left, r.Top), isApp));
                }
                catch { /* button vanished */ }
            }

            // Windows 11 marks app buttons clearly; older layouts don't, so fall back to all buttons.
            buttons = candidates.Any(c => c.IsAppButton)
                ? candidates.Where(c => c.IsAppButton).Select(c => c.B).ToList()
                : candidates.Select(c => c.B).ToList();
        }
        catch
        {
            return new List<Button>();
        }

        if (buttons.Count == 0) return buttons;
        double xSpread = buttons.Max(b => b.X) - buttons.Min(b => b.X);
        double ySpread = buttons.Max(b => b.Y) - buttons.Min(b => b.Y);
        return xSpread >= ySpread
            ? buttons.OrderBy(b => b.X).ToList()
            : buttons.OrderBy(b => b.Y).ToList();
    }

    /// <summary>Position (0 = left-most) of the profile's taskbar button, or -1 if not found.</summary>
    public static int IndexOf(List<Button> buttons, BrowserProfile p)
    {
        // 1. Exact app ID (Windows 11 exposes it as "Appid: <id>").
        if (!string.IsNullOrEmpty(p.AppUserModelId))
        {
            int i = buttons.FindIndex(b =>
                StripAppIdPrefix(b.AutomationId).Equals(p.AppUserModelId, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }

        // 2. The button's label, e.g. "prem123 - Chrome - 1 running window".
        foreach (var label in new[] { p.TaskbarLabel, p.BaseName })
        {
            if (string.IsNullOrWhiteSpace(label)) continue;
            int i = buttons.FindIndex(b =>
                b.Name.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                b.Name.StartsWith(label + " -", StringComparison.OrdinalIgnoreCase) ||
                b.Name.StartsWith(label + " –", StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
        }
        return -1;
    }

    private static string StripAppIdPrefix(string id)
    {
        id = id.Trim();
        return id.StartsWith("Appid:", StringComparison.OrdinalIgnoreCase) ? id[6..].Trim() : id;
    }
}
