using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ProfileRefresher;

/// <summary>
/// Sends phone notifications through a Telegram bot (Bot API over HTTPS). Nothing is hosted on this PC:
/// the app calls Telegram's servers, and the Telegram app on the phone shows the notification.
/// </summary>
public static class TelegramNotifier
{
    /// <summary>Overridable for tests.</summary>
    public static string ApiBase { get; set; } = "https://api.telegram.org";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Sends an HTML-formatted message. Returns null on success, otherwise what went wrong.</summary>
    public static async Task<string?> SendAsync(string token, string chatId, string html, CancellationToken ct = default)
        => (await SendWithIdAsync(token, chatId, html, null, ct)).Error;

    /// <summary>Sends an HTML-formatted message, optionally as a reply. Returns the new message's ID, or what went wrong.</summary>
    public static async Task<(long? MessageId, string? Error)> SendWithIdAsync(string token, string chatId, string html,
        long? replyTo = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, "no bot token");
        if (string.IsNullOrWhiteSpace(chatId)) return (null, "no chat ID");

        var payload = new Dictionary<string, object>
        {
            ["chat_id"] = chatId.Trim(),
            ["text"] = html,
            ["parse_mode"] = "HTML",
            ["disable_web_page_preview"] = true,
        };
        if (replyTo is { } r) payload["reply_parameters"] = new { message_id = r, allow_sending_without_reply = true };

        try
        {
            using var res = await Http.PostAsync($"{ApiBase}/bot{token.Trim()}/sendMessage",
                new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
            string json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, Describe(res.StatusCode, json));

            long? id = null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                id = doc.RootElement.GetProperty("result").GetProperty("message_id").GetInt64();
            }
            catch { }
            return (id, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Telegram did not answer in time (internet down?)");
        }
        catch (HttpRequestException ex)
        {
            return (null, "could not reach Telegram: " + ex.Message);
        }
    }

    /// <summary>
    /// Finds the chat ID of the person who most recently messaged the bot (send it any message first).
    /// Returns (chatId, name, null) or (null, null, error).
    /// </summary>
    public static async Task<(string? ChatId, string? Name, string? Error)> FindChatIdAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return (null, null, "enter the bot token first");
        try
        {
            using var res = await Http.GetAsync($"{ApiBase}/bot{token.Trim()}/getUpdates", ct);
            string json = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (null, null, Describe(res.StatusCode, json));

            using var doc = JsonDocument.Parse(json);
            string? id = null, name = null;
            foreach (var update in doc.RootElement.GetProperty("result").EnumerateArray())
            {
                if (!update.TryGetProperty("message", out var msg)) continue;
                var chat = msg.GetProperty("chat");
                id = chat.GetProperty("id").GetRawText();
                name = chat.TryGetProperty("first_name", out var fn) ? fn.GetString()
                     : chat.TryGetProperty("title", out var t) ? t.GetString() : null;
            }
            return id is null
                ? (null, null, "no messages yet: open your bot in Telegram, press Start (or send it any message), then try again")
                : (id, name, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, null, "could not reach Telegram: " + ex.Message);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, null, "unexpected answer from Telegram");
        }
    }

    /// <summary>The notification text for a reply after the last message of the sequence.</summary>
    public static string FormatReply(string profileName, string from, ReplyAlert alert, bool includeText)
    {
        var sb = new StringBuilder();
        sb.Append("🔔 <b>New reply after your last message</b>\n");
        sb.Append("Chrome profile: <b>").Append(Escape(profileName)).Append("</b>\n");
        sb.Append("From: ").Append(Escape(from)).Append(" · ").Append(alert.At.ToString("HH:mm"));
        if (includeText && !string.IsNullOrWhiteSpace(alert.Message))
        {
            string text = alert.Message!.Length > 500 ? alert.Message[..499] + "…" : alert.Message;
            sb.Append("\n\n“").Append(Escape(text)).Append('”');
        }
        return sb.ToString();
    }

    public static string Escape(string s) => WebUtility.HtmlEncode(s);

    internal static string Describe(HttpStatusCode code, string json)
    {
        string? desc = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("description", out var d)) desc = d.GetString();
        }
        catch { }

        return code switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => "the bot token is wrong",
            HttpStatusCode.BadRequest when desc?.Contains("chat not found", StringComparison.OrdinalIgnoreCase) == true =>
                "chat not found: check the chat ID, and press Start in your bot first",
            HttpStatusCode.Forbidden => "the bot can't message you: open the bot in Telegram and press Start",
            _ => $"Telegram error {(int)code}" + (desc is null ? "" : $": {desc}"),
        };
    }

    // ---- token storage: encrypted for the current Windows user, so settings.json doesn't hold it in plain text ----

    public static string Protect(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null,
                DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null,
                DataProtectionScope.CurrentUser));
        }
        catch { return ""; }
    }
}
