# Browser Profile Refresher (Windows 11)

**Developed by Jeymeyzer. XYZ Chatter (Team Mel)**

A small WPF (.NET 10) utility that works through your open Chrome or Edge profiles one at a time.
It has two tasks:

- **Refresh one tab** refreshes one tab in each profile.
- **Post I'm hosting in the global chat** opens *Your Cruiser Profile → Edit*, sets the cruising
  status, clicks Done, clicks Save and closes the profile. Saving makes the site post the
  "I'm hosting" message. This task needs the helper extension (see below).

## Build

1. Install the **.NET 10 SDK** (x64): https://dotnet.microsoft.com/download/dotnet/10.0
2. Double-click `build.bat`.
3. Run `publish\ProfileRefresher.exe`.

## One-time setup for "Post I'm hosting": the helper extension

The `extension` folder is a small Chrome extension. It clicks the site's buttons by their
`data-testid`, so it doesn't break when windows move or pages load slowly.

**Keep the folder in one fixed place**, for example `C:\Tools\ProfileRefresherExtension`.
Every profile loads it from there.

In **each** profile:

1. Open `chrome://extensions` (Edge: `edge://extensions`).
2. Turn on **Developer mode**.
3. Click **Load unpacked** and choose the extension folder.
4. Open `chrome://extensions/shortcuts` and check that **Profile Refresher Helper → Run the job** is
   set to **Alt+Shift+H**. Chrome usually sets it automatically, but leaves it blank if another
   extension already uses that key. If you choose a different key, enter the same one in the app.

If the site changes and I send you an updated `steps.js`, replace the file in the folder and restart
Chrome. All profiles pick up the change.

## Use "Post I'm hosting"

1. Make sure the site is open in every profile.
2. Pick the browser and click **Detect profiles**. Tick the profiles to include.
3. Choose the task **Post I'm hosting in the global chat**.
4. Check the options:
   - **Status to set**: exactly as shown in the picker, e.g. `I'm hosting`.
   - **Status field label**: the text next to the status dropdown (default `Hosting Status`).
   - **Site tab**: part of the site's address, such as the domain. The extension then finds the
     right tab even if it isn't the active one. Leave it empty to use the active tab.
   - **Delay / random extra delay**: time between profiles.
   - **Loops**: usually `1` for this task.
5. Click **Test 1 profile** first and watch it. When it works, click **Start**.

For each profile, the app brings that profile's window to the front and presses the shortcut. The
extension runs the steps and reports back, and the log shows the result or the exact step that
failed. A failed profile is skipped and the run continues. If the status is already set, the app
skips the picker and still clicks Save.

**Don't use the mouse or keyboard during the run.** The app needs to bring each window to the front.

## Use "Refresh + I'm hosting combined"

Mixes both tasks in one run. Set **Refresh passes, then** to how many refresh passes come before each
I'm hosting pass. With `5` the order is:

```
Refresh, Refresh, Refresh, Refresh, Refresh, I'm hosting, Refresh, …
```

Each "pass" goes through every ticked profile once, in the run order. **Number of loops** is the total
number of passes, counting both kinds, so 12 gives 10 refresh passes and 2 I'm hosting passes. The app
shows the order under the setting (R = refresh, H = I'm hosting) before you start. Both tasks' options
apply. **Test 1 profile** runs one refresh pass, then one I'm hosting pass, on the first ticked profile.

## Use "Check & read new chats (no replies)" (Stage 1)

Read-only. Nothing is ever sent. For each profile, in run order:

1. Checks the **left Messages icon** for the orange dot, without resizing the window. The global-chat
   icon on the right is ignored.
2. If there's a dot: maximizes the window, opens the chat list, and opens every unread conversation
   (`NEW` / number badge) one by one. **The ad row ("Sniffies Plus" / "One-Day Deal…") is never
   clicked**, and four separate checks guard against it.
3. In each chat, scrolls up until the whole history is loaded, reads every message, then goes Back, Back
   to the list.
4. Closes the list (clicks Messages again) and restores the window to its small size.

Each conversation is saved to
`Documents\ProfileRefresher\Conversations\<profile folder>\<userId>.json` (the **Open chat logs folder**
button opens it). Every message is stored once, by its message ID, with:
`seq` (position in the chat), `from` (`them`/`us`), `n` (1st, 2nd… text from that side), `type`
(`text`/`photo`), `text`, `altText` (if the hidden label differs, e.g. edited), `imageUrl` (photos), and
`time` (decoded from the message ID). Photos are logged but **not counted**.

When a conversation reaches **10 received and 10 sent** text messages, its file gets
`"status": "needs_review"`, and later runs skip that chat without opening it.

Opening a chat marks it as read on the site, the same as when you open it yourself.

## Use "Chat & reply + refresh + I'm hosting"

Replies in unread chats with your own messages, and keeps the refresh and I'm hosting passes going.
It runs until you press **Terminate**; the loop count is not used.

**Messages to send (in order).** Type each message in its own box. **+ Add message** adds a box and
**✕** removes one. The list is a sequence, shared by all profiles: in each chat, the first reply is
message 1, the next reply in that chat is message 2, and so on. The position is worked out from how many
text messages we have already sent in that chat, so a message you send by hand also moves it along.

For each unread chat it:

1. Opens and reads the chat (same as "Check & read new chats").
2. Replies only if the **last message is theirs**. If all messages of the sequence have been sent, it
   doesn't reply.
3. Types the next message, waits **Wait before sending** (default 2 s), presses Send, and checks the
   message appears in the chat. The chat log is saved with the reply included.
4. If that message was the **second-to-last** of the list, it sends the **last** one right after it,
   without waiting for another reply (each still waits **Wait before sending**). With a list of 2,
   both go out together in the first reply.

**The schedule:**

1. **Scan.** Each profile's site tab reports live whether the orange dot is on the Messages icon
   (`extension/dotwatch.js`). A **chat pass** opens only the profiles that have the dot, waiting
   **Delay after each chat check** (default 0.5 s) after each. Profiles with no dot are not touched.
2. Chat passes repeat while a pass still read at least one chat (a reply may bring a new message).
3. When no profile has new chats, one **refresh pass** runs over all profiles, waiting
   **Delay after each refresh** (default 5 s) after each, then it goes back to step 1.
4. Every **I'm hosting every** minutes (default 20; the first one 20 minutes after Start) an I'm hosting
   pass runs. If it falls due in the middle of a pass, the current pass finishes first.

Chats that are `needs_review` (10/10) stay unread and skipped, so a dot that only has those doesn't
keep the chat passes going.

**Profiles without a live report** are checked one by one, the old way, and the log names them. A
profile starts reporting once (a) the updated extension is loaded and the site tab was reloaded after
that, and (b) the app has run one job in it (it learns which profile the extension belongs to; this
is remembered in `%AppData%\ProfileRefresher\extension-clients.json`). A report older than 75 s is
not trusted, so a closed or frozen tab falls back to the direct check.

**Notification when someone replies after your last message.** When a chat already got the whole
sequence and they write again, a small window opens in the bottom-right corner (with a sound) showing
the **Chrome profile**, who replied and their message. **Open profile** brings that profile's window
to the front; entries stay until you dismiss them. The log shows the same with `!! NEW REPLY`.

**Phone notifications (Telegram).** The same reply alert can go to your phone through a Telegram bot.
Nothing is hosted on this PC; the app sends the message to Telegram over the internet.

1. In Telegram, open **@BotFather**, send `/newbot`, pick a name and a username ending in `bot`.
   It replies with a **token** like `123456789:AA...`. Paste it into **Bot token**.
2. Open your new bot in Telegram and press **Start** (or send it any message).
3. Click **Get chat ID** in the app; it fills in your chat ID.
4. Tick **Telegram: notify my phone…**, click **Send test**, and check your phone.

Untick **Include their message text** to send only the profile name and who replied. The token is
stored encrypted for your Windows user. If a notification can't be sent (no internet, wrong token),
the log says so and the run carries on.

**Dry run** (on by default): types each message, waits, then clears it without pressing Send. Use it
with **Test 1 profile** (one chat pass on the first ticked profile) to watch it work, then untick it.

The log shows what happened in each chat, e.g. `→ sent #2`, `→ no reply: all messages already sent`,
`→ no reply: last message is ours; waiting for their reply` or `→ reply #1 FAILED: …`.

## Use "Refresh one tab"

Choose the tab (the active tab, or the first tab whose title contains some text), set the delay and
loops, and click **Start**. The app presses the window's own Reload button through Windows
accessibility, without moving focus. The F5 fallback is optional.

## Run order

**Run order → By taskbar position (left to right)** (the default) follows the order of the Chrome/Edge
buttons on your taskbar. Each profile has its own taskbar button. To change the order, drag the buttons
on the taskbar. The app reads the taskbar again when you click **Start**, so the run always matches
what you see. Profiles it can't match to a button go last. If the taskbar is vertical, it reads top to
bottom.

**By time opened** processes profiles in the order you **opened** them, earliest
first, so a profile opened at 7:01 runs before one opened at 7:02. Each profile's open time and its
position (1, 2, 3…) show in the list after **Detect profiles**.

How it knows: every time a profile opens, Chrome/Edge starts a new session file in that profile's
`Sessions` folder, named with the exact time. The app uses the earliest one since the browser started.
If you close a profile and open it again, it keeps its first open time for that browser session.
Profiles whose time can't be read show "opened: unknown" and go last.

**By profile name** uses the previous fixed order.

## Controls

**Pause/Resume** holds the countdown between profiles. **Terminate** cancels everything that remains.
Neither one closes the browser or changes profile data. A profile that's already in progress
finishes its current steps in the page.

## Troubleshooting

| Log message | Fix |
|---|---|
| `the extension did not respond` | The extension isn't loaded in that profile, or its shortcut isn't set (step 4 above). |
| `could not bring this profile's window to the front` | Another app is holding focus. Close full-screen apps and try again. |
| `couldn't find the "Hosting Status" field` | Change **Status field label** to the exact text shown next to the dropdown. |
| `could not select "…" in the picker` | Check **Status to set** matches the option text exactly. |
| `no tab whose address or title contains …` | Fix **Site tab**, or leave it empty and make the site the active tab. |
| Port 47821 error at start | Another copy of the app is already running. |

Settings are saved in `%APPDATA%\ProfileRefresher\settings.json`.
