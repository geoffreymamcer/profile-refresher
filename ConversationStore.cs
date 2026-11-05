using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProfileRefresher;

// ---------- What the extension sends back for a "readChats" job ----------

public sealed class ReadChatsData
{
    public bool HadDot { get; set; }
    public List<ChatData> Chats { get; set; } = new();
    public int SkippedAds { get; set; }
    public List<string> SkippedReview { get; set; } = new();
    public List<ChatError> Errors { get; set; } = new();
    public bool ClosedList { get; set; }
}

public sealed class ChatData
{
    public string UserId { get; set; } = "";
    public string? RowId { get; set; }
    public string? Url { get; set; }
    public string? Name { get; set; }
    public string? ProfileLabel { get; set; }
    public List<MessageData> Messages { get; set; } = new();

    /// <summary>Set when the job asked for replies.</summary>
    public ReplyData? Reply { get; set; }
}

/// <summary>What happened when replying in one chat.</summary>
public sealed class ReplyData
{
    /// <summary>"sent", "dry_run", "finished" (sequence used up), "skipped" or "failed".</summary>
    public string Status { get; set; } = "";
    /// <summary>1-based position in the message sequence (the last one sent, when two went out).</summary>
    public int? Index { get; set; }
    /// <summary>Set when two messages went out together (second-to-last + last): the first of them.</summary>
    public int? FromIndex { get; set; }
    public string? Text { get; set; }
    public string? Reason { get; set; }
}

public sealed class MessageData
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";   // "them" or "us"
    public string Type { get; set; } = "";   // "text" or "photo"
    public string? Text { get; set; }
    public string? AltText { get; set; }
    public string? ImageUrl { get; set; }
}

public sealed class ChatError
{
    public string? UserId { get; set; }
    public string? Step { get; set; }
    public string? Message { get; set; }
}

// ---------- What is saved on disk: one file per decoy profile + user ----------

public sealed class Conversation
{
    public string UserId { get; set; } = "";
    public string? ProfileUrl { get; set; }
    public string? Name { get; set; }
    public string? ProfileLabel { get; set; }
    public string DecoyProfile { get; set; } = "";
    public string DecoyProfileDir { get; set; } = "";

    /// <summary>"active" or "needs_review" (10 text messages each way reached).</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>Text messages only; photos are not counted.</summary>
    public int ReceivedCount { get; set; }
    public int SentCount { get; set; }

    public List<StoredMessage> Messages { get; set; } = new();
}

public sealed class StoredMessage
{
    /// <summary>Position in the whole conversation (1 = first).</summary>
    public int Seq { get; set; }
    public string Id { get; set; } = "";
    public string From { get; set; } = "";

    /// <summary>Text messages: 1st, 2nd, 3rd… from this side. Photos: null.</summary>
    public int? N { get; set; }
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? AltText { get; set; }
    public string? ImageUrl { get; set; }

    /// <summary>When it was sent, decoded from the message ID.</summary>
    public DateTimeOffset? Time { get; set; }
}

public sealed record MergeResult(Conversation Conversation, int NewMessages, bool NewlyNeedsReview, bool IsNewContact);

/// <summary>
/// Saves conversations as JSON in Documents\ProfileRefresher\Conversations\&lt;decoy profile&gt;\&lt;userId&gt;.json.
/// Messages are de-duplicated by their message ID, so reading the same chat again only adds what's new.
/// </summary>
public static class ConversationStore
{
    public const int ReviewThreshold = 10;

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProfileRefresher", "Conversations");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ReadChatsData? ParseReadChats(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } el) return null;
        try { return el.Deserialize<ReadChatsData>(Json); }
        catch { return null; }
    }

    public static string FolderFor(BrowserProfile decoy)
    {
        string name = decoy.Identified ? decoy.ProfileDir : "Unidentified";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Root, name);
    }

    private static string FileFor(BrowserProfile decoy, string userId) =>
        Path.Combine(FolderFor(decoy), SafeId(userId) + ".json");

    private static string SafeId(string id)
    {
        var s = new string(id.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return s.Length == 0 ? "unknown" : s;
    }

    public static Conversation? Load(BrowserProfile decoy, string userId)
    {
        try
        {
            var path = FileFor(decoy, userId);
            return File.Exists(path) ? JsonSerializer.Deserialize<Conversation>(File.ReadAllText(path), Json) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>User IDs on this decoy profile marked "needs_review" (the bot no longer opens these chats).</summary>
    public static string[] ReviewUserIds(BrowserProfile decoy)
    {
        var ids = new List<string>();
        try
        {
            var dir = FolderFor(decoy);
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var c = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file), Json);
                    if (c is { Status: "needs_review" }) ids.Add(c.UserId);
                }
                catch { /* skip unreadable file */ }
            }
        }
        catch { }
        return ids.ToArray();
    }

    public static MergeResult Merge(BrowserProfile decoy, ChatData chat)
    {
        string userId = SafeId(chat.UserId.Length > 0 ? chat.UserId : chat.RowId ?? "");
        var now = DateTimeOffset.Now;
        var existing = Load(decoy, userId);
        bool isNew = existing is null;
        var conv = existing ?? new Conversation { UserId = userId, FirstSeen = now };

        conv.DecoyProfile = decoy.BaseName ?? decoy.DisplayName;
        conv.DecoyProfileDir = decoy.ProfileDir;
        conv.ProfileUrl = chat.Url ?? conv.ProfileUrl;
        conv.Name = chat.Name ?? conv.Name;
        conv.ProfileLabel = chat.ProfileLabel ?? conv.ProfileLabel;
        conv.LastUpdated = now;

        // Add only messages we haven't stored yet.
        var known = conv.Messages.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var m in chat.Messages)
        {
            if (string.IsNullOrEmpty(m.Id) || !known.Add(m.Id)) continue;
            conv.Messages.Add(new StoredMessage
            {
                Id = m.Id,
                From = m.From == "us" ? "us" : "them",
                Type = m.Type == "photo" ? "photo" : "text",
                Text = m.Text,
                AltText = m.AltText,
                ImageUrl = m.ImageUrl,
                Time = TimeFromId(m.Id),
            });
            added++;
        }

        // Chronological order (message IDs carry the send time); keep page order for ties.
        conv.Messages = conv.Messages
            .Select((m, i) => (m, i))
            .OrderBy(x => x.m.Time ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.m)
            .ToList();

        int received = 0, sent = 0;
        for (int i = 0; i < conv.Messages.Count; i++)
        {
            var m = conv.Messages[i];
            m.Seq = i + 1;
            if (m.Type == "text")
                m.N = m.From == "us" ? ++sent : ++received;
            else
                m.N = null; // photos don't count
        }
        conv.ReceivedCount = received;
        conv.SentCount = sent;

        bool newlyReview = false;
        if (conv.Status != "needs_review" && received >= ReviewThreshold && sent >= ReviewThreshold)
        {
            conv.Status = "needs_review";
            newlyReview = true;
        }

        Save(decoy, conv);
        return new MergeResult(conv, added, newlyReview, isNew);
    }

    private static void Save(BrowserProfile decoy, Conversation conv)
    {
        var path = FileFor(decoy, conv.UserId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(conv, Json));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>The first 8 hex characters of a message ID are its Unix send time (seconds).</summary>
    public static DateTimeOffset? TimeFromId(string id)
    {
        if (id.Length < 8) return null;
        if (!long.TryParse(id[..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long secs)) return null;
        if (secs < 1_000_000_000) return null; // not a timestamp
        try { return DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime(); }
        catch { return null; }
    }
}
