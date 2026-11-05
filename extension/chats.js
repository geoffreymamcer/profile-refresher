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
//   [data-testid="chatTextArea"]                  message box (present while a chat is open)
//   [data-testid="sendButton"]                    send (paper plane)
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
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
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
  const maxChats = Math.max(1, job.maxChats || 25);
  const result = { hadDot: false, chats: [], skippedAds: 0, skippedReview: [], errors: [], closedList: false };
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
  const chatOpen = () => !!all('[data-testid="chatTextArea"]')[0];
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
      await sleep(700);
      if (i >= 2 && msgEls().length === before) break;
    }
    let last = -1;
    for (let i = 0; i < 10; i++) {
      const c = msgEls().length;
      if (c === last) break;
      last = c;
      await sleep(400);
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
  const textArea = () => all('[data-testid="chatTextArea"]')[0] || null;
  const sendButton = () => all('[data-testid="sendButton"]')[0] || null;
  const isOurs = (el) => el.classList.contains("sent_message") || !!el.closest(".chat-text-message-stack--sent");

  // Angular only notices a value it was told about, so set it the way typing does.
  const setText = (ta, value) => {
    ta.focus();
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
    setter.call(ta, value);
    ta.dispatchEvent(new Event("input", { bubbles: true }));
    ta.dispatchEvent(new Event("change", { bubbles: true }));
  };

  /** Sends the next message of the sequence in the open chat. Returns what happened. */
  const sendReply = async (chat) => {
    const last = chat.messages[chat.messages.length - 1];
    if (!last) return { status: "skipped", reason: "no messages found in the chat" };
    if (last.from !== "them") return { status: "skipped", reason: "last message is ours; waiting for their reply" };

    const ours = chat.messages.filter((m) => m.from === "us" && m.type === "text").length;
    if (ours >= replies.length) {
      return { status: "finished", reason: `all ${replies.length} message(s) of the sequence already sent` };
    }
    const index = ours + 1;
    const text = replies[ours];

    const ta = await waitFor(textArea, 4000);
    if (!ta) return { status: "failed", index, text, reason: "message box not found" };
    if (ta.value.trim().length > 0) {
      return { status: "failed", index, text, reason: "message box already has text in it; not sending" };
    }

    setText(ta, text);
    await sleep(sendDelay);
    if (ta.value !== text) return { status: "failed", index, text, reason: "the typed message was changed or cleared" };

    if (dryRun) {
      setText(ta, "");
      return { status: "dry_run", index, text };
    }

    const btn = sendButton();
    if (!btn) {
      setText(ta, "");
      return { status: "failed", index, text, reason: "send button not found" };
    }

    const before = new Set(msgEls().map((el) => el.id));
    const wanted = norm(text);
    click(btn);
    const confirmed = await waitFor(() =>
      msgEls().find((el) => !before.has(el.id) && isOurs(el) && norm(el.textContent).includes(wanted)), T);
    if (confirmed) return { status: "sent", index, text };

    // Not shown in the chat yet. An emptied box means the site took it.
    const box = textArea();
    if (box && box.value.trim().length === 0) {
      return { status: "sent", index, text, reason: "sent, but not yet shown in the chat" };
    }
    if (box) setText(box, ""); // don't leave it behind as a draft
    return { status: "failed", index, text, reason: "message did not send (still in the message box)" };
  };

  /** Back, Back → chat list (as on the site). */
  const backToList = async () => {
    const b1 = await waitFor(() => all('[data-testid="headerGoBackButton"]')[0], 4000);
    if (!b1) return false;
    click(b1);
    await waitFor(() => !chatOpen(), 4000);
    await sleep(600);

    const b2 = all('[data-testid="headerGoBackButton"]')[0];
    if (b2) {
      click(b2);
      await sleep(600);
    }
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
    await sleep(800); // let the rows render

    // Show unread chats only, so read ones are never opened. Put the filter back afterwards.
    step = "Turn on Unread filter";
    let turnedOnUnread = false;
    if (unreadChip() && !chipOn()) {
      click(unreadChip());
      turnedOnUnread = !!(await waitFor(chipOn, 3000));
      await sleep(800);
    }

    const attempted = new Set();
    for (let n = 0; n < maxChats; n++) {
      step = "Find unread chat";
      const unread = all("chat-list-vertical-item").filter((r) => !isAd(r) && isUnread(r));
      const fresh = unread.filter((r) => !attempted.has(rowKey(r)));

      for (const r of fresh) {
        const k = rowKey(r);
        if (skip.has(k)) {
          attempted.add(k);
          if (!result.skippedReview.includes(k)) result.skippedReview.push(k);
        }
      }

      const row = fresh.find((r) => !skip.has(rowKey(r)));
      if (!row) break;
      const key = rowKey(row);
      attempted.add(key);

      const target = row.querySelector("td.conversation-content-container") ||
        row.querySelector('[aria-label="Select Chat"]') || row;
      if (isAd(row) || isAd(target)) { result.skippedAds++; continue; } // belt and braces

      step = "Open chat";
      click(target);
      if (!(await waitFor(chatOpen))) {
        result.errors.push({ userId: key, step, message: "chat did not open" });
        if (!listVisible()) await backToList();
        continue;
      }

      step = "Read chat";
      await loadFullHistory();
      let chat = readOpenChat(row.id);

      if (replies.length > 0) {
        step = "Reply";
        const reply = await sendReply(chat);
        if (reply.status === "failed") result.errors.push({ userId: key, step, message: reply.reason });
        if (reply.status === "sent") {
          await sleep(800);
          chat = readOpenChat(row.id); // include what we just sent in the saved log
        }
        chat.reply = reply;
      }
      result.chats.push(chat);

      step = "Back to chat list";
      if (!(await backToList())) {
        result.errors.push({ userId: key, step, message: "could not get back to the chat list" });
        break;
      }
      await sleep(500);
    }

    // Unread ads left behind (never opened).
    result.skippedAds += all("app-chat-list-ad, [data-testid=\"sniffiesChatRow\"]")
      .filter((a) => a.querySelector(".new-tag, .unread-count")).length;

    if (turnedOnUnread && chipOn()) {
      step = "Restore Unread filter";
      click(unreadChip());
      await waitFor(() => !chipOn(), 3000);
      await sleep(300);
    }

    step = "Close chat list";
    const b = chatButton();
    if (b && b.classList.contains("active")) {
      click(b);
      result.closedList = !!(await waitFor(() => {
        const x = chatButton();
        return x && !x.classList.contains("active");
      }, 4000));
    } else {
      result.closedList = true;
    }

    const ok = result.errors.length === 0;
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
