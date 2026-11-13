// Profile Refresher Helper · Developed by Jeymeyzer. XYZ Chatter (Team Mel)
// When the desktop app asks (by pressing the shortcut, or in Multitask mode when we ask it), get the
// job, run the steps in the site's tab, and send the result back. It only ever talks to the app on localhost.


import { runSteps } from "./steps.js";
import { checkDot, readChats } from "./chats.js";

const APP = "http://localhost:47821";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// A random id for this Chrome profile's copy of the extension. The app learns which profile it
// belongs to when it runs a job here, so it can tell whose live dot reports are whose.
let clientIdPromise = null;
function clientId() {
  clientIdPromise ??= (async () => {
    const { clientId: saved } = await chrome.storage.local.get("clientId");
    if (saved) return saved;
    const id = crypto.randomUUID();
    await chrome.storage.local.set({ clientId: id });
    return id;
  })();
  return clientIdPromise;
}

// Live dot reports from dotwatch.js → the app (ignored when the app isn't running).
// "sleep": timers for page scripts in Multitask mode. Chrome slows timers in hidden or covered
// windows (to as little as once a minute); the extension's own timers aren't slowed.
chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (msg?.type === "dot" && sender.tab) {
    if (msg.health) autoRefreshCheck(sender.tab, msg);
    reportDot(sender.tab, msg);
  }
  if (msg?.type === "sleep") {
    setTimeout(() => sendResponse(true), Math.max(0, Math.min(60000, Number(msg.ms) || 0)));
    return true; // answer later
  }
});

// ---------- Multitask mode ----------
// The app doesn't bring the window forward or press the shortcut: we ask it for work ourselves.
// The app holds each request open until there's a job for this profile (or ~20 s pass).
let polling = false;
async function pollForJobs() {
  if (polling) return;
  polling = true;
  try {
    for (;;) {
      let r;
      try {
        r = await fetch(`${APP}/wait?client=${encodeURIComponent(await clientId())}`, { cache: "no-store" });
      } catch {
        return; // app closed
      }
      if (r.status === 200) await runJob(await r.json());
      else if (r.status !== 204) return; // the app isn't in Multitask mode any more
      await chrome.storage.local.get("clientId"); // keeps this service worker awake between requests
    }
  } finally {
    polling = false;
  }
}

// ---------- Self-update ----------
// The app's installer replaces this extension's files on disk, but Chrome keeps running the old copy
// until the extension is reloaded. The app tells us which version is installed; if ours differs we
// reload once, and after the restart reload the site tabs so they get the new page scripts too.
const MY_VERSION = chrome.runtime.getManifest().version;
const SITE_URLS = chrome.runtime.getManifest().content_scripts.flatMap((c) => c.matches);
let reloadTriedFor = null;
const reloadTriedReady = chrome.storage.local.get("reloadTriedFor").then((s) => { reloadTriedFor = s.reloadTriedFor ?? null; });

async function maybeSelfUpdate(installed) {
  if (!installed || installed === MY_VERSION) return;
  await reloadTriedReady;
  if (reloadTriedFor === installed) return; // already tried: we were loaded from another folder
  reloadTriedFor = installed;
  await chrome.storage.local.set({ reloadTriedFor: installed, reloadSiteTabs: true });
  chrome.runtime.reload();
}

(async () => {
  const { reloadSiteTabs } = await chrome.storage.local.get("reloadSiteTabs");
  if (!reloadSiteTabs) return;
  await chrome.storage.local.remove("reloadSiteTabs");
  for (const t of await chrome.tabs.query({ url: SITE_URLS })) {
    try { await chrome.tabs.reload(t.id); } catch { /* tab closed */ }
  }
})();

// ---------- Auto-refresh ----------
// The site's page sometimes stops updating (no new chats, no orange dot) until it's refreshed. We refresh
// the site tab ourselves, without focus or keys, also when the app isn't running:
//  - when its live connection closed and didn't come back within 30 s, or the "lost your internet
//    connection" banner has been up for a minute;
//  - every N minutes (the app's setting; default 10), but only when you haven't used the tab for
//    2 minutes, nothing is typed in the message box, and no app step is running in it.
const AUTO_DEFAULT = { enabled: true, minutes: 10 };
let autoSettings = AUTO_DEFAULT;
const autoReady = chrome.storage.local.get("autoRefresh").then((s) => { if (s.autoRefresh) autoSettings = s.autoRefresh; });
const offlineSince = new Map(); // tabId → when the banner was first seen
const lastAutoReload = new Map(); // tabId → when we last reloaded it
const autoNote = new Map(); // tabId → reason, sent to the app with the next report
let jobTabId = null;

async function autoRefreshCheck(tab, msg) {
  await autoReady;
  if (!autoSettings.enabled || tab.id === jobTabId) return;
  const h = msg.health || {};
  const now = Date.now();

  if (msg.offline) { if (!offlineSince.has(tab.id)) offlineSince.set(tab.id, now); }
  else offlineSince.delete(tab.id);

  if (h.pageAge < 60000) return; // just loaded: give it a minute
  if (now - (lastAutoReload.get(tab.id) || 0) < 120000) return; // at most every 2 minutes

  let reason = null;
  const live = h.live;
  if (live?.ever && live.open === 0 && live.closedAt && now - live.closedAt > 30000) reason = "the live connection was lost";
  else if (offlineSince.has(tab.id) && now - offlineSince.get(tab.id) > 60000) reason = "the site said the connection was lost";
  else if (h.pageAge > autoSettings.minutes * 60000 && h.idleFor > 120000 && !h.typing)
    reason = `timed refresh (every ${autoSettings.minutes} min)`;
  if (!reason) return;

  lastAutoReload.set(tab.id, now);
  offlineSince.delete(tab.id);
  autoNote.set(tab.id, reason);
  try { await chrome.tabs.reload(tab.id); } catch { /* tab closed */ }
}

async function applyAppSettings(info) {
  const a = info?.autoRefresh;
  if (!a || typeof a.enabled !== "boolean") return;
  const next = { enabled: a.enabled, minutes: Math.min(240, Math.max(2, Number(a.minutes) || 10)) };
  if (next.enabled === autoSettings.enabled && next.minutes === autoSettings.minutes) return;
  autoSettings = next;
  await chrome.storage.local.set({ autoRefresh: next });
}

async function reportDot(tab, msg) {
  try {
    await reloadTriedReady;
    const r = await fetch(`${APP}/status`, {
      method: "POST",
      headers: { "Content-Type": "text/plain" },
      body: JSON.stringify({
        client: await clientId(),
        tabId: tab.id,
        hasButton: !!msg.hasButton,
        hasDot: !!msg.hasDot,
        offline: !!msg.offline,
        version: MY_VERSION,
        reloadTried: reloadTriedFor !== null && reloadTriedFor !== MY_VERSION,
        autoRefreshed: autoNote.get(tab.id) || null,
      }),
    });
    autoNote.delete(tab.id);
    if (r.status === 200) {
      const info = await r.json();
      applyAppSettings(info);
      if (info?.multitask) pollForJobs();
      maybeSelfUpdate(info?.extensionVersion);
    }
  } catch {
    // app not running
  }
}

chrome.commands.onCommand.addListener((command) => {
  if (command === "run-job") handleJob();
});

async function handleJob() {
  let job;
  try {
    const r = await fetch(`${APP}/job?client=${encodeURIComponent(await clientId())}`, { cache: "no-store" });
    if (r.status !== 200) return; // app not running a job: ignore the key press
    job = await r.json();
  } catch {
    return;
  }
  await runJob(job);
}

async function runJob(job) {
  let result;
  try {
    const tab = await findTab(job.tabMatch);
    jobTabId = tab?.id ?? null; // no auto-refresh in this tab while the step runs
    if (!tab) {
      result = {
        ok: false,
        step: "Find site tab",
        message: job.tabMatch ? `no tab whose address or title contains "${job.tabMatch}"` : "no active tab",
      };
    } else {
      if (!tab.active) await chrome.tabs.update(tab.id, { active: true });
      if (!job.quiet) await chrome.windows.update(tab.windowId, { focused: true });
      await sleep(300);
      result =
        job.type === "readChats" ? await doReadChats(tab, job)
        : job.type === "reload" ? await reloadTab(tab, job)
        : await inject(tab, runSteps, job);
    }
  } catch (e) {
    result = { ok: false, step: "Run", message: String(e?.message ?? e) };
  }
  jobTabId = null;

  try {
    await fetch(`${APP}/result`, {
      method: "POST",
      headers: { "Content-Type": "text/plain" },
      body: JSON.stringify({ id: job.id, ...result }),
    });
  } catch {
    // app closed or run terminated
  }
}

/** Reload the site tab and wait for it to finish loading (Multitask mode's refresh: no keys, no focus). */
async function reloadTab(tab, job) {
  const done = new Promise((resolve) => {
    const onUpdated = (id, info) => {
      if (id === tab.id && info.status === "complete") {
        chrome.tabs.onUpdated.removeListener(onUpdated);
        resolve(true);
      }
    };
    chrome.tabs.onUpdated.addListener(onUpdated);
    setTimeout(() => { chrome.tabs.onUpdated.removeListener(onUpdated); resolve(false); },
      Math.max(15000, (job.timeoutMs || 10000) * 3));
  });
  await chrome.tabs.reload(tab.id);
  const loaded = await done;
  return { ok: true, step: "Reload", message: loaded ? "reloaded" : "reloaded (still loading)" };
}

async function inject(tab, func, ...args) {
  const [injection] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func, args });
  return injection?.result ?? { ok: false, step: "Run", message: "the page returned no result" };
}

/** Check the dot without touching the window; only if there is something to do, maximize, read, restore. */
async function doReadChats(tab, job) {
  const dot = await inject(tab, checkDot);
  if (!dot.hasButton) {
    return { ok: false, step: "Check messages", message: "Messages button not found (page not loaded or black screen?)" };
  }
  if (!job.sendTo && !dot.hasDot && !dot.listOpen) {
    return { ok: true, step: "Check messages", message: "no new messages", data: { hadDot: false, chats: [] } };
  }

  // Multitask mode: leave the window where it is (maximizing a window brings it to the front).
  if (job.quiet) return await inject(tab, readChats, job);

  const win = await chrome.windows.get(tab.windowId);
  const saved = { state: win.state, left: win.left, top: win.top, width: win.width, height: win.height };
  try {
    if (win.state !== "maximized") {
      await chrome.windows.update(win.id, { state: "maximized" });
      await sleep(1200); // let the page re-layout
    }
    return await inject(tab, readChats, job);
  } finally {
    try {
      if (saved.state === "normal") {
        await chrome.windows.update(win.id, {
          state: "normal", left: saved.left, top: saved.top, width: saved.width, height: saved.height,
        });
      } else if (saved.state === "minimized") {
        await chrome.windows.update(win.id, { state: "minimized" });
      }
    } catch {
      // window closed meanwhile
    }
  }
}

async function findTab(match) {
  const m = (match || "").trim().toLowerCase();
  const [active] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!m) return active ?? null;

  const matches = (t) =>
    !!t && ((t.url || "").toLowerCase().includes(m) || (t.title || "").toLowerCase().includes(m));
  if (matches(active)) return active;

  const tabs = await chrome.tabs.query({});
  return tabs.find((t) => t.windowId === active?.windowId && matches(t)) ?? tabs.find(matches) ?? null;
}
