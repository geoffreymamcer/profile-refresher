// Live "new messages" report for the autopilot.
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

  const read = () => {
    const btn = document.querySelector('[data-testid="chatButtonIcon"]');
    return {
      hasButton: !!btn,
      hasDot: !!(btn && btn.querySelector('[data-testid="chatListNewMsgDot"]')),
    };
  };

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
      chrome.runtime.sendMessage({ type: "dot", ...state }).catch(() => {});
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
