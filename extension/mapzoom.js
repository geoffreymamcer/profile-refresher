// Auto zoom-out after every page load of the site.
// Developed by Jeymeyzer. XYZ Chatter (Team Mel)
//
// Runs by itself on each load (no shortcut, no focus needed), so the app never waits for the map.
// Waits until the map's zoom-out button (MapLibre "−") is visible, then keeps tapping it until
// the map is fully zoomed out (MapLibre disables the button at the minimum zoom).
// A safety cap stops it if the button never becomes disabled. Gives up if the map
// never loads (e.g. black screen).

(() => {
  if (window.__profileRefresherMapZoom) return; // once per page load
  window.__profileRefresherMapZoom = true;

  const MAX_TAPS = 30;          // safety cap (MapLibre's full zoom range is ~22 steps)
  const SETTLE_MS = 2000;       // after the button appears, let the map finish its start-up move
  const TAP_PAUSE_MS = 450;     // zoom animation between taps
  const GIVE_UP_MS = 90_000;    // stop waiting if the map never loads

  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const visible = (el) =>
    !!el && el.isConnected && el.getClientRects().length > 0 && getComputedStyle(el).visibility !== "hidden";
  const zoomOutButton = () =>
    Array.from(document.querySelectorAll("button.maplibregl-ctrl-zoom-out")).find(visible) || null;
  const disabled = (b) => b.disabled || b.getAttribute("aria-disabled") === "true";

  let started = false;

  const zoomOut = async () => {
    if (started) return;
    started = true;
    observer.disconnect();
    clearInterval(poll);

    await sleep(SETTLE_MS);
    for (let i = 0; i < MAX_TAPS; i++) {
      const b = zoomOutButton();
      if (!b || disabled(b)) break; // fully zoomed out (or button gone)
      b.click();
      await sleep(TAP_PAUSE_MS);
    }
  };

  const check = () => {
    if (started) return;
    if (zoomOutButton()) zoomOut();
    else if (performance.now() > GIVE_UP_MS) {
      observer.disconnect();
      clearInterval(poll);
    }
  };

  const observer = new MutationObserver(check);
  observer.observe(document.documentElement, { childList: true, subtree: true });
  const poll = setInterval(check, 1000); // also catches the button becoming visible without DOM changes
  check();
})();
