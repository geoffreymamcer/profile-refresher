using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>Who a notification is about: replying to it in Telegram sends to this person.</summary>
public sealed record ReplyTarget(string ProfileKey, string ProfileName, string UserId, string UserName);

/// <summary>One thing to send in a chat: type "text" (Text) or "photo" (Data = base64, Name, Mime).</summary>
public sealed record SendItem(string Type, string? Text = null, string? Data = null, string? Name = null, string? Mime = null);

/// <summary>A reply typed in Telegram, waiting to be sent on the site.</summary>
public sealed record ManualReply(long TelegramMessageId, ReplyTarget Target, IReadOnlyList<SendItem> Items)
{
    public string Describe()
    {
        int photos = Items.Count(i => i.Type == "photo");
        int texts = Items.Count - photos;
        var parts = new List<string>();
        if (texts > 0) parts.Add(texts == 1 ? "1 message" : $"{texts} messages");
        if (photos > 0) parts.Add(photos == 1 ? "1 photo" : $"{photos} photos");
        return string.Join(" + ", parts);
    }
}

/// <summary>
/// Remembers which Telegram message is about which chat, so a reply to it can be routed.
/// Kept on disk, so replying to an older notification still works after a restart.
/// </summary>
public static class TelegramThreads
{
    private const int Keep = 400;
    private static readonly object Lock = new();
    private static List<Entry>? _entries;

    private sealed record Entry(long MessageId, ReplyTarget Target);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ProfileRefresher", "telegram-threads.json");

    private static List<Entry> Entries()
    {
        if (_entries is not null) return _entries;
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath));
        }
        catch { }
        return _entries ??= new();
    }

    public static void Remember(long messageId, ReplyTarget target)
    {
        lock (Lock)
        {
            var list = Entries();
            list.RemoveAll(e => e.MessageId == messageId);
            list.Add(new Entry(messageId, target));
            if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(list));
            }
            catch { }
        }
    }

    public static ReplyTarget? Find(long messageId)
    {
        lock (Lock) return Entries().LastOrDefault(e => e.MessageId == messageId)?.Target;
    }
}

/// <summary>
/// Listens for your replies to the bot (long polling, so nothing is hosted on this PC). Only messages from
/// the configured chat ID are accepted. A reply to a notification becomes a <see cref="ManualReply"/>.
/// </summary>
public sealed class TelegramReplyListener
{
    private const long MaxPhotoBytes = 10 * 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly string _token;
    private readonly string _chatId;

    public TelegramReplyListener(string token, string chatId)
    {
        _token = token.Trim();
        _chatId = chatId.Trim();
    }

    /// <summary>A reply to send. Raised on a background thread.</summary>
    public event Action<ManualReply>? Received;
    /// <summary>Something worth showing in the app's log. Raised on a background thread.</summary>
    public event Action<string>? Log;

    private string Api => $"{TelegramNotifier.ApiBase}/bot{_token}";

    /// <summary>Polls until cancelled. Messages sent while the app wasn't listening are skipped (and you're told).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        long offset = 0;
        bool skippedBacklog = false;
        int failures = 0;

        while (!ct.IsCancellationRequested)
        {
            JsonDocument doc;
            try
            {
                int wait = skippedBacklog ? 25 : 0;
                using var res = await Http.GetAsync(
                    $"{Api}/getUpdates?offset={offset}&timeout={wait}&allowed_updates=%5B%22message%22%5D", ct);
                string json = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    // 409: something else is reading updates (e.g. "Get chat ID" was clicked); try again shortly.
                    if ((int)res.StatusCode != 409 && failures++ % 20 == 0)
                        Log?.Invoke("Telegram replies: " + TelegramNotifier.Describe(res.StatusCode, json));
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    continue;
                }
                doc = JsonDocument.Parse(json);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                if (failures++ % 20 == 0) Log?.Invoke("Telegram replies: can't reach Telegram, retrying (" + ex.Message + ")");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { return; }
                continue;
            }

            using (doc)
            {
                int ignored = 0;
                foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
                {
                    offset = Math.Max(offset, update.GetProperty("update_id").GetInt64() + 1);
                    if (!update.TryGetProperty("message", out var msg)) continue;
                    if (!FromOwner(msg)) continue;

                    if (!skippedBacklog) { ignored++; continue; }
                    try { await HandleAsync(msg, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { Log?.Invoke("Telegram replies: " + ex.Message); }
                }

                if (!skippedBacklog)
                {
                    skippedBacklog = true;
                    Log?.Invoke("Listening for your replies in Telegram.");
                    if (ignored > 0)
                        await SayAsync($"ℹ️ Ignored {ignored} message(s) sent while the app wasn't running. Reply again if needed.", null, ct);
                }
            }
        }
    }

    private bool FromOwner(JsonElement msg) =>
        msg.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var id) && id.GetRawText() == _chatId;

    private async Task HandleAsync(JsonElement msg, CancellationToken ct)
    {
        long messageId = msg.GetProperty("message_id").GetInt64();
        string? text = msg.TryGetProperty("text", out var t) ? t.GetString() : null;
        string? caption = msg.TryGetProperty("caption", out var c) ? c.GetString() : null;

        if (text is not null && text.StartsWith('/'))
        {
            await SayAsync("👋 When someone replies after your last message, I'll notify you here.\n\n" +
                           "To answer them, <b>reply</b> to that notification (swipe it, or long-press → Reply) and " +
                           "type your message or send a photo. It goes out after the profile the app is working on.",
                messageId, ct);
            return;
        }

        ReplyTarget? target = null;
        if (msg.TryGetProperty("reply_to_message", out var replyTo))
            target = TelegramThreads.Find(replyTo.GetProperty("message_id").GetInt64());
        if (target is null)
        {
            await SayAsync("↩️ Reply to a notification (swipe it, or long-press → Reply) so I know who it's for.", messageId, ct);
            return;
        }

        var items = new List<SendItem>();
        string? photoId = null, mime = null, name = null;
        long size = 0;
        if (msg.TryGetProperty("photo", out var sizes) && sizes.GetArrayLength() > 0)
        {
            var biggest = sizes[sizes.GetArrayLength() - 1];
            photoId = biggest.GetProperty("file_id").GetString();
            size = biggest.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0;
            mime = "image/jpeg";
            name = "photo.jpg";
        }
        else if (msg.TryGetProperty("document", out var docEl) &&
                 docEl.TryGetProperty("mime_type", out var mt) && (mt.GetString() ?? "").StartsWith("image/"))
        {
            photoId = docEl.GetProperty("file_id").GetString();
            size = docEl.TryGetProperty("file_size", out var fs) ? fs.GetInt64() : 0;
            mime = mt.GetString();
            name = docEl.TryGetProperty("file_name", out var fn) ? fn.GetString() : "photo";
        }

        if (photoId is not null)
        {
            if (size > MaxPhotoBytes)
            {
                await SayAsync("❌ That photo is too big (over 10 MB).", messageId, ct);
                return;
            }
            var (data, error) = await DownloadAsync(photoId, ct);
            if (data is null)
            {
                await SayAsync("❌ Couldn't get the photo from Telegram: " + error, messageId, ct);
                return;
            }
            items.Add(new SendItem("photo", Data: Convert.ToBase64String(data), Name: name, Mime: mime));
        }

        string? words = text ?? caption;
        if (!string.IsNullOrWhiteSpace(words)) items.Add(new SendItem("text", Text: words.Trim()));

        if (items.Count == 0)
        {
            await SayAsync("❌ Only text and photos can be sent.", messageId, ct);
            return;
        }

        var reply = new ManualReply(messageId, target, items);
        Received?.Invoke(reply);
        await SayAsync($"⏳ Sending {reply.Describe()} to <b>{TelegramNotifier.Escape(target.UserName)}</b> " +
                       $"({TelegramNotifier.Escape(target.ProfileName)}) after the current profile…", messageId, ct, target);
    }

    private async Task<(byte[]? Data, string? Error)> DownloadAsync(string fileId, CancellationToken ct)
    {
        try
        {
            using var res = await Http.GetAsync($"{Api}/getFile?file_id={Uri.EscapeDataString(fileId)}", ct);
            string json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, TelegramNotifier.Describe(res.StatusCode, json));
            using var doc = JsonDocument.Parse(json);
            string? path = doc.RootElement.GetProperty("result").GetProperty("file_path").GetString();
            if (string.IsNullOrEmpty(path)) return (null, "no file path");

            var bytes = await Http.GetByteArrayAsync($"{TelegramNotifier.ApiBase}/file/bot{_token}/{path}", ct);
            return bytes.Length > MaxPhotoBytes ? (null, "photo is over 10 MB") : (bytes, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Tells you how a reply went, as a reply to your message. You can reply to that answer too.</summary>
    public Task ConfirmAsync(ManualReply reply, string html, CancellationToken ct = default) =>
        SayAsync(html, reply.TelegramMessageId, ct, reply.Target);

    private async Task SayAsync(string html, long? replyTo, CancellationToken ct, ReplyTarget? target = null)
    {
        var (id, error) = await TelegramNotifier.SendWithIdAsync(_token, _chatId, html, replyTo, ct);
        if (error is not null) Log?.Invoke("Telegram: " + error);
        else if (id is { } m && target is not null) TelegramThreads.Remember(m, target);
    }
}
