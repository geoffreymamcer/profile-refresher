// Profile Refresher Helper · Developed by Jeymeyzer. XYZ Chatter (Team Mel)
// When the desktop app presses the shortcut, ask the app for the job, run the steps in the
// site's tab, and send the result back. It only ever talks to the app on localhost.

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
chrome.runtime.onMessage.addListener((msg, sender) => {
  if (msg?.type === "dot" && sender.tab) reportDot(sender.tab, msg);
});

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
      }),
    });
    if (r.status === 200) maybeSelfUpdate((await r.json())?.extensionVersion);
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

  let result;
  try {
    const tab = await findTab(job.tabMatch);
    if (!tab) {
      result = {
        ok: false,
        step: "Find site tab",
        message: job.tabMatch ? `no tab whose address or title contains "${job.tabMatch}"` : "no active tab",
      };
    } else {
      await chrome.tabs.update(tab.id, { active: true });
      await chrome.windows.update(tab.windowId, { focused: true });
      await sleep(300);
      result = job.type === "readChats" ? await doReadChats(tab, job) : await inject(tab, runSteps, job);
    }
  } catch (e) {
    result = { ok: false, step: "Run", message: String(e?.message ?? e) };
  }

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
