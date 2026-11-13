// Chat steps: read unread chats and, when the app sends a message list, reply with the next one.
// Developed by Jeymeyzer. XYZ Chatter (Team Mel)
//
// Each function is injected into the page on its own, so everything it needs is defined inside it.
//
// Elements used (from the site's HTML):
//   [data-testid="chatButtonIcon"]                Messages button (class "active" while the list is open)
//   [data-testid="chatListNewMsgDot"]             orange dot = new private messages
//   chat-list-vertical-item[id=<userId>]          one real conversation row
//   [data-testid="messageNewBadge"] / [data-testid="messageNewBadgeCount"]   unread markers (real chats only)
//   app-chat-list-ad / [data-testid="sniffiesChatRow"] / [aria-label="View Advertisement"] / .conversation-marker.ad   the ad (never clicked)
//   [id^="message-"] inside #chat-profile-messages-container   one message (text or photo)
//   .sent_message / .received_message             who sent it
//   [data-testid="inputV2TextArea"] in [data-testid="chatInputComposer"]
//                                                 message box (present while a chat is open; Nov 2026)
//   [data-testid="composerSend"]                  send (paper plane; data-send="disabled" while empty)
//   [data-testid="chatTextArea"], [data-testid="sendButton"]   the same, before Nov 2026
//   [data-testid="headerGoBackButton"]            Back

/** Quick check: is there a new-message dot? (No clicks.) */
export function checkDot() {
  const btn = document.querySelector('[data-testid="chatButtonIcon"]');
  return {
    hasButton: !!btn,
    hasDot: !!(btn && btn.querySelector('[data-testid="chatListNewMsgDot"]')),
    listOpen: !!(btn && btn.classList.contains("active")),
  };
}

/**
 * Opens the chat list, reads every unread real conversation, then closes the list.
 * If job.replyMessages is set, replies in each chat with the next message of the sequence:
 * message N+1 when we have already sent N text messages in that chat.
 */
export async function readChats(job) {
  const T = Math.max(3000, job.timeoutMs || 10000);
  // Multitask mode: the window may be hidden or covered, where Chrome slows page timers a lot;
  // the extension's background keeps time instead.
  const sleep = (ms) =>
    job.quiet
      ? chrome.runtime.sendMessage({ type: "sleep", ms }).catch(() => new Promise((r) => setTimeout(r, ms)))
      : new Promise((r) => setTimeout(r, ms));
  const visible = (el) =>
    !!el && el.isConnected && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const all = (sel) => Array.from(document.querySelectorAll(sel)).filter(visible);

  const waitFor = async (fn, ms = T) => {
    const end = Date.now() + ms;
    for (;;) {
      let v = null;
      try { v = fn(); } catch { /* element vanished mid-check */ }
      if (v) return v;
      if (Date.now() >= end) return null;
      await sleep(150);
    }
  };

  const click = (el) => {
    el.scrollIntoView({ block: "center", inline: "center" });
    const r = el.getBoundingClientRect();
    const base = {
      bubbles: true, cancelable: true, composed: true, view: window, button: 0,
      clientX: r.left + r.width / 2, clientY: r.top + r.height / 2,
    };
    const ptr = { ...base, pointerId: 1, pointerType: "mouse", isPrimary: true };
    el.dispatchEvent(new PointerEvent("pointerdown", ptr));
    el.dispatchEvent(new MouseEvent("mousedown", base));
    el.dispatchEvent(new PointerEvent("pointerup", ptr));
    el.dispatchEvent(new MouseEvent("mouseup", base));
    el.dispatchEvent(new MouseEvent("click", base));
  };

  const norm = (s) => (s || "").replace(/[‘’`´]/g, "'").replace(/\s+/g, " ").trim().toLowerCase();
  const skip = new Set((job.skipUserIds || []).map((s) => String(s).toLowerCase()));
  const replies = (job.replyMessages || []).map((s) => String(s)).filter((s) => s.trim().length > 0);
  const dryRun = !!job.dryRun;
  const sendDelay = Math.max(0, job.sendDelayMs ?? 2000);
  // Manual reply from the phone: { userId, items: [{ type: "text", text } | { type: "photo", data, name, mime }] }.
  const sendTo = job.sendTo && job.sendTo.userId ? job.sendTo : null;
  const maxChats = Math.max(1, job.maxChats || 25);
  const result = { hadDot: false, chats: [], skippedAds: 0, skippedReview: [], errors: [], closedList: false, unreadLeft: 0,
    timings: {} }; // ms per stage, summed over chats, with counts: { back: { ms, n }, ... }
  const timed = async (stage, fn) => {
    const t0 = Date.now();
    try { return await fn(); }
    finally {
      const t = (result.timings[stage] ||= { ms: 0, n: 0 });
      t.ms += Date.now() - t0;
      t.n++;
    }
  };
  let step = "Start";

  // ---- never touch the ad: four independent checks ----
  const isAd = (el) =>
    !!el &&
    (!!el.closest('app-chat-list-ad, app-chat-list-ad-runtime, [data-testid="sniffiesChatRow"], [aria-label="View Advertisement"]') ||
     !!el.querySelector('[aria-label="View Advertisement"], .conversation-marker.ad, [data-testid="sniffiesChatRow"]'));

  // A badge only counts if it is actually shown: read chats can keep hidden or empty badge elements.
  const shown = (el) => !!el && visible(el) && (el.textContent || "").trim().length > 0;
  const isUnread = (row) => {
    const isNew = row.querySelector('[data-testid="messageNewBadge"]');
    const count = row.querySelector('[data-testid="messageNewBadgeCount"]');
    const n = count ? parseInt((count.textContent || "").trim(), 10) : 0;
    return shown(isNew) || (shown(count) && n > 0);
  };

  // The list's "Unread" filter chip (aria-pressed="true" when on).
  const unreadChip = () => all('[data-testid="filterUnreadBtn"]')[0] || null;
  const chipOn = () => unreadChip()?.getAttribute("aria-pressed") === "true";

  const chatButton = () => all('[data-testid="chatButtonIcon"]')[0] || null;
  // The message box: the new composer (Nov 2026) or the old one.
  const MESSAGE_BOX =
    '[data-testid="chatInputComposer"] textarea, [data-testid="inputV2TextArea"], [data-testid="chatTextArea"]';
  const chatOpen = () => !!all(MESSAGE_BOX)[0];
  const listVisible = () =>
    all("chat-list-vertical-item").length > 0 || !!all('[data-testid="chatListTab-RECENTS"]')[0];

  const msgEls = () =>
    Array.from(document.querySelectorAll('#chat-profile-messages-container [id^="message-"]'));
  const atTopOfHistory = () =>
    Array.from(document.querySelectorAll(".top-of-chat"))
      .some((e) => /beginning of your chat history/i.test(e.textContent || ""));

  /** Scrolls up until the whole history is loaded, then waits for it to settle. */
  const loadFullHistory = async () => {
    await waitFor(() => msgEls().length > 0 || atTopOfHistory(), T);
    const panel = document.getElementById("chat-profile-messages-panel");
    for (let i = 0; i < 15 && panel && !atTopOfHistory(); i++) {
      const before = msgEls().length;
      panel.scrollTop = 0;
      // Older messages appear within a moment if there are any.
      await waitFor(() => msgEls().length > before || atTopOfHistory(), 700);
      if (i >= 1 && msgEls().length === before) break;
    }
    let last = -1;
    for (let i = 0; i < 10; i++) {
      const c = msgEls().length;
      if (c === last) break;
      last = c;
      await sleep(250);
    }
  };

  const readOpenChat = (rowId) => {
    const m = location.pathname.match(/\/profile\/([a-f0-9]{24})/i);
    const userId = (m ? m[1] : rowId || "").toLowerCase();
    const messages = msgEls().map((el) => {
      const id = el.id.replace(/^message-/, "");
      const cls = el.classList;
      const from = cls.contains("sent_message") ? "us"
        : cls.contains("received_message") ? "them"
        : el.closest(".chat-text-message-stack--sent") ? "us" : "them";

      const isPhoto = cls.contains("photo") || cls.contains("chat-photo-message") || !!el.closest("chat-photo-message");
      if (isPhoto) {
        const img = el.querySelector("img");
        return { id, from, type: "photo", text: null, altText: null, imageUrl: img ? img.src : null };
      }

      const clone = el.cloneNode(true);
      clone.querySelectorAll(".chat-message-reactions").forEach((x) => x.remove());
      const text = (clone.textContent || "").replace(/\s+/g, " ").trim();
      const aria = (el.getAttribute("aria-label") || "").replace(/^(Received|Sent) Message:\s*/i, "").trim();
      return { id, from, type: "text", text, altText: aria && aria !== text ? aria : null, imageUrl: null };
    });

    const label = (sel) => {
      const e = document.querySelector(sel);
      return e ? (e.textContent || "").replace(/\s+/g, " ").trim() : null;
    };
    return {
      userId,
      rowId: rowId || null,
      url: location.href,
      name: label('[data-testid="cruiserNameLabel"]'),
      profileLabel: label('[data-testid="cruiserProfileLabel"]'),
      messages,
    };
  };

  // ---- replying ----
  const textArea = () => all(MESSAGE_BOX)[0] || null;
  const sendButton = () => all('[data-testid="composerSend"], [data-testid="sendButton"]')[0] || null;
  const isDisabled = (b) =>
    b.disabled || b.getAttribute("aria-disabled") === "true" || b.getAttribute("data-send") === "disabled";
  const isOurs = (el) => el.classList.contains("sent_message") || !!el.closest(".chat-text-message-stack--sent");

  // Angular only notices a value it was told about, so set it the way typing does.
  const setText = (ta, value) => {
    ta.focus();
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
    setter.call(ta, value);
    ta.dispatchEvent(new Event("input", { bubbles: true }));
    ta.dispatchEvent(new Event("change", { bubbles: true }));
  };

  /** Types `text` (message number `index`), waits, and sends it. Returns what happened. */
  const sendOne = async (text, index) => {
    const ta = await waitFor(textArea, 4000);
    if (!ta) return { status: "failed", index, text, reason: "message box not found" };
    if (ta.value.trim().length > 0) {
      return { status: "failed", index, text, reason: "message box already has text in it; not sending" };
    }

    setText(ta, text);
    await sleep(sendDelay);
    // The site may have replaced the box while we waited: our text would then be in a detached copy.
    if (!ta.isConnected || textArea() !== ta) {
      return { status: "failed", index, text, reason: "the chat reloaded while typing; message not sent" };
    }
    if (ta.value !== text) return { status: "failed", index, text, reason: "the typed message was changed or cleared" };

    if (dryRun) {
      setText(ta, "");
      return { status: "dry_run", index, text };
    }

    // The new Send button stays disabled until the site has noticed the text.
    const btn = await waitFor(() => { const b = sendButton(); return b && !isDisabled(b) ? b : null; }, 3000);
    if (!btn) {
      setText(ta, "");
      return { status: "failed", index, text, reason: sendButton() ? "send button stayed disabled" : "send button not found" };
    }

    const before = new Set(msgEls().map((el) => el.id));
    const wanted = norm(text);
    click(btn);
    const confirmed = await waitFor(() =>
      msgEls().find((el) => !before.has(el.id) && isOurs(el) && norm(el.textContent).includes(wanted)), T);
    if (confirmed) return { status: "sent", index, text };

    // Not shown in the chat yet. The same box, now empty, means the site took it.
    // (A different, empty box means the chat was redrawn and nothing was sent.)
    const box = textArea();
    if (box === ta && box.isConnected && box.value.trim().length === 0) {
      return { status: "sent", index, text, reason: "sent, but not yet shown in the chat" };
    }
    if (box) setText(box, ""); // don't leave it behind as a draft
    return { status: "failed", index, text, reason: "message did not send (still in the message box)" };
  };

  // ---- photos ----
  const photoButton = () =>
    all('[aria-label="Select a Photo From Your Device"], [data-testid="composerShare"]')[0] || null;
  const fileInputs = () => Array.from(document.querySelectorAll('input[type="file"]'))
    .filter((i) => !i.disabled && (!i.accept || /image|\*/i.test(i.accept)));
  const isPhotoEl = (el) => el.classList.contains("photo") || el.classList.contains("chat-photo-message") ||
    !!el.closest("chat-photo-message") || !!el.querySelector("img");

  /** Puts the photo into the site's photo input (no file dialog), then sends it. */
  const sendPhoto = async (item) => {
    const text = "[photo]";
    if (dryRun) return { status: "dry_run", text };

    let input = fileInputs()[0];
    if (!input) {
      // The input may only exist after pressing "+". A script click can't open the file dialog.
      const plus = photoButton();
      if (!plus) return { status: "failed", text, reason: "photo button (+) not found" };
      click(plus);
      input = await waitFor(() => fileInputs()[0], 3000);
      if (!input) return { status: "failed", text, reason: "photo upload box not found after pressing +; the site's photo flow is different" };
    }

    let file;
    try {
      const bin = atob(item.data || "");
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      file = new File([bytes], item.name || "photo.jpg", { type: item.mime || "image/jpeg" });
    } catch {
      return { status: "failed", text, reason: "photo data is damaged" };
    }

    const before = new Set(msgEls().map((el) => el.id));
    const newPhoto = () => msgEls().find((el) => !before.has(el.id) && isOurs(el) && isPhotoEl(el));

    const dt = new DataTransfer();
    dt.items.add(file);
    input.files = dt.files;
    input.dispatchEvent(new Event("input", { bubbles: true }));
    input.dispatchEvent(new Event("change", { bubbles: true }));

    // Some sites send right away; others show a preview that still needs Send.
    if (await waitFor(newPhoto, 2500)) return { status: "sent", text };
    await sleep(sendDelay);
    const btn = await waitFor(() => { const b = sendButton(); return b && !isDisabled(b) ? b : null; }, 3000);
    if (btn) click(btn);
    if (await waitFor(newPhoto, Math.max(T, 15000))) return { status: "sent", text };
    return { status: "failed", text, reason: "the photo did not appear in the chat (it may still be uploading, or the site needs another step)" };
  };

  /**
   * Sends the next message of the sequence in the open chat. Returns what happened.
   * The second-to-last message is followed straight away by the last one (no waiting for a reply).
   */
  const sendReply = async (chat) => {
    const last = chat.messages[chat.messages.length - 1];
    if (!last) return { status: "skipped", reason: "no messages found in the chat" };
    if (last.from !== "them") return { status: "skipped", reason: "last message is ours; waiting for their reply" };

    const ours = chat.messages.filter((m) => m.from === "us" && m.type === "text").length;
    if (ours >= replies.length) {
      return { status: "finished", reason: `all ${replies.length} message(s) of the sequence already sent` };
    }

    const first = ours + 1;
    const r = await sendOne(replies[ours], first);
    const pairWithLast = replies.length >= 2 && first === replies.length - 1;
    if (!pairWithLast || (r.status !== "sent" && r.status !== "dry_run")) return r;

    // Second-to-last went out: send the last one right after it.
    await sleep(500);
    const r2 = await sendOne(replies[first], first + 1);
    if (r2.status === "failed") {
      r2.reason += ` (#${first} was ${r.status === "dry_run" ? "typed" : "sent"})`;
    }
    return { ...r2, fromIndex: first };
  };

  /** Manual reply: open this person's chat from Recents and send the items. Fills result.sent / result.errors. */
  const sendToChat = async () => {
    const want = String(sendTo.userId).toLowerCase();
    result.sent = [];

    step = "Find chat";
    const findRow = () => all("chat-list-vertical-item").find((r) => !isAd(r) && rowKey(r) === want) || null;
    let row = findRow();
    for (let i = 0; i < 10 && !row; i++) {
      // Scroll the list down to load older chats.
      const any = all("chat-list-vertical-item")[0];
      let box = any ? any.parentElement : null;
      while (box && box !== document.body && box.scrollHeight <= box.clientHeight + 5) box = box.parentElement;
      if (!box || box === document.body) break;
      const top = box.scrollTop;
      box.scrollTop = top + box.clientHeight;
      box.dispatchEvent(new Event("scroll", { bubbles: true }));
      await sleep(900);
      row = findRow();
      if (!row && box.scrollTop === top) break; // end of the list
    }
    if (!row) {
      result.errors.push({ userId: want, step, message: "this person's chat is not in the Recents list" });
      return;
    }

    const target = row.querySelector("td.conversation-content-container") ||
      row.querySelector('[aria-label="Select Chat"]') || row;
    if (isAd(row) || isAd(target)) {
      result.errors.push({ userId: want, step, message: "refusing to open an ad row" });
      return;
    }

    step = "Open chat";
    click(target);
    if (!(await waitFor(chatOpen))) {
      result.errors.push({ userId: want, step, message: "chat did not open" });
      if (!listVisible()) await backToList();
      return;
    }
    // Wait for the conversation to finish loading, as automatic replies do. The site redraws the
    // chat (and its message box) when it loads; typing before that lands in a box that gets thrown away.
    await loadFullHistory();
    await sleep(500);

    step = "Send";
    for (const item of sendTo.items || []) {
      const r = item.type === "photo" ? await sendPhoto(item) : await sendOne(String(item.text || ""), null);
      result.sent.push({ type: item.type === "photo" ? "photo" : "text", status: r.status, reason: r.reason || null });
      if (r.status === "failed") {
        result.errors.push({ userId: want, step, message: r.reason });
        break;
      }
      await sleep(600);
    }

    await sleep(300);
    result.chats.push(readOpenChat(row.id)); // saved to the chat log

    step = "Back to chat list";
    if (!(await timed("back", backToList))) result.errors.push({ userId: want, step, message: "could not get back to the chat list" });
  };

  /** Back, Back → chat list (as on the site). */
  // Moves on as soon as each screen changes, instead of fixed pauses.
  const backToList = async () => {
    const back = () => all('[data-testid="headerGoBackButton"]')[0];
    const b1 = await waitFor(back, 4000);
    if (!b1) return false;
    const path1 = location.pathname;
    click(b1);
    // First Back leaves the chat (the address changes or the message box goes away).
    await waitFor(() => location.pathname !== path1 || !chatOpen() || listVisible(), 2000);
    if (listVisible() && !chatOpen()) return true;

    // Second Back: from the profile to the list.
    const b2 = await waitFor(back, 1500);
    if (b2) click(b2);
    return !!(await waitFor(() => listVisible() && !chatOpen(), 5000));
  };

  try {
    result.hadDot = !!document.querySelector('[data-testid="chatButtonIcon"] [data-testid="chatListNewMsgDot"]');

    step = "Open chat list";
    const btn = await waitFor(chatButton);
    if (!btn) return { ok: false, step, message: "Messages button not found (page not loaded?)", data: result };
    if (!btn.classList.contains("active")) click(btn);
    if (!(await waitFor(listVisible))) return { ok: false, step, message: "chat list did not open", data: result };

    const recents = all('[data-testid="chatListTab-RECENTS"]')[0];
    if (recents && !recents.classList.contains("active-tab")) {
      click(recents);
      await sleep(600);
    }
    // Wait for the rows to actually render (a fixed pause was sometimes too short on a slow load,
    // so the pass saw an empty list and moved on to the next profile).
    await timed("list", () => waitFor(() => all("chat-list-vertical-item").length > 0, 5000));
    await sleep(300);

    let turnedOnUnread = false;
    let turnedOffUnread = false;
    if (sendTo) {
      // The person may already be read, so the Unread filter must be off to find them.
      step = "Turn off Unread filter";
      if (unreadChip() && chipOn()) {
        click(unreadChip());
        turnedOffUnread = !!(await waitFor(() => !chipOn(), 3000));
        await sleep(300);
      }
      await sendToChat();
    }

    // Show unread chats only, so read ones are never opened. Put the filter back afterwards.
    step = "Turn on Unread filter";
    if (!sendTo && unreadChip() && !chipOn()) {
      click(unreadChip());
      turnedOnUnread = !!(await waitFor(chipOn, 3000));
      await sleep(300);
      // The filtered list loads again: give it time to show the unread rows.
      await waitFor(() => all("chat-list-vertical-item").some((r) => !isAd(r) && isUnread(r)), 4000);
    }

    // Chats opened this pass, with how many times. A chat can be opened a second time when the
    // person writes again while we are busy with other chats (it shows as unread again).
    const opened = new Map();
    const MAX_OPENS = 2;
    const canOpen = (k) => (opened.get(k) || 0) < MAX_OPENS;
    const unreadRows = () => all("chat-list-vertical-item").filter((r) => !isAd(r) && isUnread(r));
    for (let n = 0; n < (sendTo ? 0 : maxChats); n++) {
      step = "Find unread chat";
      const pick = () => {
        const unread = unreadRows();
        for (const r of unread) {
          const k = rowKey(r);
          if (skip.has(k) && !result.skippedReview.includes(k)) result.skippedReview.push(k);
        }
        return unread.find((r) => !skip.has(rowKey(r)) && canOpen(rowKey(r))) || null;
      };
      // Don't give up on the first empty look: the list can still be updating after Back.
      const row = pick() || (await waitFor(pick, 2500));
      if (!row) break;
      const key = rowKey(row);
      opened.set(key, (opened.get(key) || 0) + 1);

      const target = row.querySelector("td.conversation-content-container") ||
        row.querySelector('[aria-label="Select Chat"]') || row;
      if (isAd(row) || isAd(target)) { result.skippedAds++; continue; } // belt and braces

      step = "Open chat";
      click(target);
      if (!(await timed("open", () => waitFor(chatOpen)))) {
        result.errors.push({ userId: key, step, message: "chat did not open" });
        if (!listVisible()) await backToList();
        continue;
      }

      step = "Read chat";
      await timed("read", loadFullHistory);
      let chat = readOpenChat(row.id);

      if (replies.length > 0) {
        step = "Reply";
        const reply = await timed("reply", () => sendReply(chat));
        if (reply.status === "failed") result.errors.push({ userId: key, step, message: reply.reason });
        if (reply.status === "sent") {
          await sleep(200); // sending already waited for our message to show up
          chat = readOpenChat(row.id); // include what we just sent in the saved log
        }
        chat.reply = reply;
      }
      result.chats.push(chat);

      step = "Back to chat list";
      if (!(await timed("back", backToList))) {
        result.errors.push({ userId: key, step, message: "could not get back to the chat list" });
        break;
      }
    }

    // Unread chats we could not get to (an error, the per-pass limit, or still loading).
    // The app checks this profile again for these, even though the orange dot is gone by now.
    result.unreadLeft = unreadRows().filter((r) => !skip.has(rowKey(r))).length;

    // Unread ads left behind (never opened).
    result.skippedAds += all("app-chat-list-ad, [data-testid=\"sniffiesChatRow\"]")
      .filter((a) => a.querySelector(".new-tag, .unread-count")).length;

    if (turnedOnUnread && chipOn()) {
      step = "Restore Unread filter";
      click(unreadChip());
      await waitFor(() => !chipOn(), 3000);
    }
    if (turnedOffUnread && unreadChip() && !chipOn()) {
      step = "Restore Unread filter";
      click(unreadChip());
      await waitFor(chipOn, 3000);
    }

    step = "Close chat list";
    const b = chatButton();
    if (b && b.classList.contains("active")) {
      click(b);
      // Closed when the button is no longer active or the list is gone, whichever shows first.
      result.closedList = !!(await timed("close", () => waitFor(() => {
        const x = chatButton();
        return (x && !x.classList.contains("active")) || !listVisible();
      }, 3000)));
    } else {
      result.closedList = true;
    }

    const ok = result.errors.length === 0;
    if (sendTo) {
      const sent = (result.sent || []).filter((x) => x.status === "sent" || x.status === "dry_run").length;
      return {
        ok,
        step: ok ? "Finished" : result.errors[0].step,
        message: ok ? `sent ${sent} message(s)` : result.errors[0].message,
        data: result,
      };
    }
    return {
      ok,
      step: ok ? "Finished" : result.errors[0].step,
      message: ok
        ? `read ${result.chats.length} chat(s)` +
          (replies.length ? `, replied in ${result.chats.filter((c) => c.reply && (c.reply.status === "sent" || c.reply.status === "dry_run")).length}` : "")
        : `${result.errors.length} problem(s): ${result.errors[0].message}`,
      data: result,
    };
  } catch (e) {
    return { ok: false, step, message: "script error: " + ((e && e.message) || e), data: result };
  }

  function rowKey(r) {
    return (r.id || r.textContent || "").trim().toLowerCase().slice(0, 80);
  }
}
