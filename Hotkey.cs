namespace ProfileRefresher;

/// <summary>A keyboard shortcut such as "Alt+Shift+H", sent to the foreground window.</summary>
public sealed class Hotkey
{
    private Hotkey(byte[] modifiers, byte key, string text)
    {
        Modifiers = modifiers;
        Key = key;
        Text = text;
    }

    public byte[] Modifiers { get; }
    public byte Key { get; }
    public string Text { get; }

    public static bool TryParse(string? text, out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        var mods = new List<byte>();
        foreach (var p in parts[..^1])
        {
            byte m = p.ToLowerInvariant() switch
            {
                "ctrl" or "control" => Native.VK_CONTROL,
                "alt" => Native.VK_MENU,
                "shift" => Native.VK_SHIFT,
                _ => 0,
            };
            if (m == 0 || mods.Contains(m)) return false;
            mods.Add(m);
        }
        if (!mods.Contains(Native.VK_CONTROL) && !mods.Contains(Native.VK_MENU)) return false;

        string k = parts[^1];
        byte key;
        if (k.Length == 1 && char.IsAsciiLetterOrDigit(k[0])) key = (byte)char.ToUpperInvariant(k[0]);
        else if (k is "," || k.Equals("Comma", StringComparison.OrdinalIgnoreCase)) key = 0xBC;
        else if (k is "." || k.Equals("Period", StringComparison.OrdinalIgnoreCase)) key = 0xBE;
        else return false;

        hotkey = new Hotkey(mods.ToArray(), key, text.Trim());
        return true;
    }

    public void Send()
    {
        foreach (var m in Modifiers)
        {
            Native.keybd_event(m, 0, 0, UIntPtr.Zero);
            Thread.Sleep(20);
        }
        Native.keybd_event(Key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        Native.keybd_event(Key, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        for (int i = Modifiers.Length - 1; i >= 0; i--)
        {
            Thread.Sleep(20);
            Native.keybd_event(Modifiers[i], 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
    }

    public override string ToString() => Text;
}
