using System.ComponentModel;

namespace ProfileRefresher;

/// <summary>One message box in the "Messages to send" list.</summary>
public sealed class ReplyMessageItem : INotifyPropertyChanged
{
    private int _number;
    private string _text = "";

    public ReplyMessageItem(string text = "") => _text = text;

    /// <summary>Position in the sequence, shown as "1.", "2.", …</summary>
    public int Number
    {
        get => _number;
        set
        {
            if (_number == value) return;
            _number = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Number)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public string Label => $"{Number}.";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
