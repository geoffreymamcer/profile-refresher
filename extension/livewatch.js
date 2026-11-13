// Watches the site's live connection (its WebSockets), so a frozen page can be refreshed.
// Developed by Jeymeyzer. XYZ Chatter (Team Mel)
//
// Runs in the page itself, before the site's own scripts, and only counts: how many live connections
// are open, when the last one closed and when the last message came in. It never reads or changes
// what's sent. The numbers are written to an attribute on <html>, where dotwatch.js reads them.

(() => {
  if (window.__profileRefresherLiveWatch) return;
  window.__profileRefresherLiveWatch = true;

  const Native = window.WebSocket;
  if (typeof Native !== "function") return;

  const open = new Set();
  let everOpened = false;
  let closedAt = 0; // when the last open connection closed (0 = one is open, or none yet)
  let lastMessageAt = 0;

  // Times are Date.now() values; the reader works out how long ago they were.
  let lastPublishedMessageAt = 0;
  const publish = () => {
    lastPublishedMessageAt = lastMessageAt;
    document.documentElement?.setAttribute(
      "data-pr-live",
      JSON.stringify({ open: open.size, ever: everOpened, closedAt, lastMessageAt }),
    );
  };

  const track = (ws) => {
    ws.addEventListener("open", () => {
      open.add(ws);
      everOpened = true;
      closedAt = 0;
      lastMessageAt = Date.now();
      publish();
    });
    ws.addEventListener("message", () => {
      lastMessageAt = Date.now();
      if (lastMessageAt - lastPublishedMessageAt > 10000) publish(); // keep the attribute cheap
    });
    ws.addEventListener("close", () => {
      if (open.delete(ws) && open.size === 0) closedAt = Date.now();
      publish();
    });
  };

  // Same WebSocket for the site, only watched. Constants and instanceof keep working.
  class WatchedWebSocket extends Native {
    constructor(...args) {
      super(...args);
      try { track(this); } catch { /* never break the site */ }
    }
  }
  window.WebSocket = WatchedWebSocket;
})();
