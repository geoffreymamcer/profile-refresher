// Live "new messages" (and "lost connection / update" banner) report for the autopilot.
// Developed by Jeymeyzer. XYZ Chatter (Team Mel)
//
// Runs by itself in the site's tab and tells the extension whether the orange dot is on the
// Messages icon: right away when it changes, and every few seconds as a heartbeat. The extension
// passes it on to the app, so the app can open only the profiles that really have new messages
// instead of checking every profile one by one. Read only: it never clicks or changes anything.

(() => {
  if (window.__profileRefresherDotWatch) return; // once per page load
  window.__profileRefresherDotWatch = true;

  const HEARTBEAT_MS = 5000;
  let last = "";
  let pending = null;
  let dead = false;

  // The site's "You've either lost your internet connection, or we're working on an update." banner.
  const OFFLINE_TEXT = /lost your internet connection|working on an update/i;
  const visible = (el) => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const offlineShown = () =>
    Array.from(document.getElementsByTagName("p")) // the banner is a <p>; checking only these keeps it cheap
      .some((el) => el.childElementCount === 0 && OFFLINE_TEXT.test(el.textContent || "") && visible(el));

  // When you last used this tab yourself (real key presses, clicks, scrolling; not the extension's clicks).
  let lastInputAt = Date.now();
  for (const ev of ["keydown", "pointerdown", "wheel", "input"])
    window.addEventListener(ev, (e) => { if (e.isTrusted) lastInputAt = Date.now(); }, { capture: true, passive: true });

  const MESSAGE_BOX =
    '[data-testid="chatInputComposer"] textarea, [data-testid="inputV2TextArea"], [data-testid="chatTextArea"]';
  const typing = () => Array.from(document.querySelectorAll(MESSAGE_BOX)).some((t) => (t.value || "").trim().length > 0);

  // The live connection, from livewatch.js (runs in the page).
  const live = () => {
    try { return JSON.parse(document.documentElement.getAttribute("data-pr-live") || "null"); }
    catch { return null; }
  };

  const read = () => {
    const btn = document.querySelector('[data-testid="chatButtonIcon"]');
    return {
      hasButton: !!btn,
      hasDot: !!(btn && btn.querySelector('[data-testid="chatListNewMsgDot"]')),
      offline: offlineShown(),
    };
  };

  // Sent with heartbeats only (it changes all the time), for the extension's auto-refresh.
  const health = () => ({
    pageAge: Math.round(performance.now()),
    idleFor: Date.now() - lastInputAt,
    typing: typing(),
    live: live(),
  });

  const stop = () => {
    dead = true;
    observer.disconnect();
    clearInterval(beat);
  };

  const send = (force) => {
    if (dead) return;
    // After the extension is reloaded, this copy is orphaned; the reloaded page runs a new one.
    if (!chrome.runtime?.id) return stop();

    const state = read();
    const key = JSON.stringify(state);
    if (!force && key === last) return;
    last = key;

    try {
      chrome.runtime.sendMessage({ type: "dot", ...state, ...(force ? { health: health() } : {}) }).catch(() => {});
    } catch {
      stop();
    }
  };

  // The site changes the page constantly (map, lists), so check at most every 250 ms.
  const observer = new MutationObserver(() => {
    if (pending) return;
    pending = setTimeout(() => {
      pending = null;
      send(false);
    }, 250);
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });

  const beat = setInterval(() => send(true), HEARTBEAT_MS);
  document.addEventListener("visibilitychange", () => send(true));
  send(true);
})();
