using System.Security.Cryptography;
using System.Text;

namespace ProfileRefresher;

/// <summary>Author credit shown in the app, plus a simple check that it hasn't been edited out.</summary>
public static class Credits
{
    public const string Text = "Developed by Jeymeyzer. XYZ Chatter (Team Mel)";

    // SHA-256 of Text. If someone edits the credit, the app refuses to start.
    private const string Fingerprint = "23bfc03fdc1f95a44a8bfaa31be63ab2af6fb22ff7833616206a88a05197c78e";

    public static bool IsIntact(string shownText) =>
        shownText == Text &&
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))
            .Equals(Fingerprint, StringComparison.OrdinalIgnoreCase);
}
