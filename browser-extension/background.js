// Baton browser bridge: tells the Baton app on this PC which tabs are playing or open, and carries out
// its requests (pause and snapshot a tab, seek a freshly opened page). The app listens on loopback and
// only accepts this extension's origin.

const ENDPOINT = "ws://127.0.0.1:7836/browser";
const PENDING_SEEK_MS = 60_000;

const tabs = new Map(); // tabId -> { url, pageTitle, media, updatedAt }
let socket = null;
let devices = [];
let pendingSeeks = []; // { url, positionMs, expiresAt }
let sendTimer = null;

async function browserName() {
  // Firefox-based browsers (Zen, LibreWolf...) name themselves; Chromium ones only through brands.
  if (typeof browser !== "undefined" && browser.runtime && browser.runtime.getBrowserInfo) {
    try {
      return (await browser.runtime.getBrowserInfo()).name;
    } catch {}
  }
  const brands = (navigator.userAgentData && navigator.userAgentData.brands) || [];
  const names = brands.map((b) => b.brand);
  if (names.some((n) => /Opera|OPR/.test(n)) || /OPR\//.test(navigator.userAgent)) return "Opera";
  if (names.some((n) => /Edge/.test(n))) return "Edge";
  if (names.some((n) => /Brave/.test(n))) return "Brave";
  return "Chrome";
}

function connect() {
  if (socket && (socket.readyState === WebSocket.OPEN || socket.readyState === WebSocket.CONNECTING)) return;
  try {
    socket = new WebSocket(ENDPOINT);
  } catch {
    return;
  }
  socket.onopen = async () => {
    // Firefox extensions have a random origin, so they prove themselves with the token their
    // build carries (config.js); Chromium ones are known by their fixed origin.
    send({ type: "hello", browser: await browserName(), token: globalThis.BATON_TOKEN, version: chrome.runtime.getManifest().version });
    scheduleReport(0);
  };
  socket.onmessage = (event) => onHostMessage(JSON.parse(event.data));
  socket.onclose = () => {
    socket = null;
    devices = [];
  };
  socket.onerror = () => {};
}

function send(message) {
  if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
}

// The worker sleeps when idle; the alarm wakes it to reconnect after the app starts.
chrome.alarms.create("baton-connect", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(() => connect());
chrome.runtime.onStartup.addListener(connect);

// Content scripts only reach pages loaded after the extension: tabs already open (when it is
// installed or reloaded) get it now, or Baton could neither see nor pause their players.
function inject(tabId) {
  // The Zen/Firefox build is Manifest V2, which has no chrome.scripting.
  return chrome.scripting
    ? chrome.scripting.executeScript({ target: { tabId }, files: ["content.js"] })
    : chrome.tabs.executeScript(tabId, { file: "content.js" });
}

async function injectIntoOpenTabs() {
  const open = await chrome.tabs.query({ url: ["http://*/*", "https://*/*"] }).catch(() => []);
  for (const tab of open) inject(tab.id).catch(() => {});
}

/** Asks a tab's page script; a tab without one gets it injected and is asked once more. */
async function askTab(tabId, message) {
  try {
    return await chrome.tabs.sendMessage(tabId, message);
  } catch {
    await inject(tabId).catch(() => {});
    return chrome.tabs.sendMessage(tabId, message).catch(() => null);
  }
}

chrome.runtime.onInstalled.addListener(() => {
  connect();
  injectIntoOpenTabs();
  chrome.contextMenus.create({ id: "baton-send-link", title: "Continue on phone", contexts: ["link", "page"] });
});
connect();

async function activeTabId() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  return tab ? tab.id : null;
}

function scheduleReport(delay = 300) {
  clearTimeout(sendTimer);
  sendTimer = setTimeout(report, delay);
}

async function report() {
  const activeId = await activeTabId();
  // Whether each tab makes sound: a muted autoplay video in a background tab plays but is silent,
  // and must not pass for what the user is watching.
  const audible = new Map((await chrome.tabs.query({}).catch(() => [])).map((t) => [t.id, !!t.audible && !(t.mutedInfo && t.mutedInfo.muted)]));
  const list = [];
  for (const [tabId, entry] of tabs) {
    const isActive = tabId === activeId;
    const mediaRelevant = entry.media && (entry.media.playing || Date.now() - entry.updatedAt < 30 * 60_000);
    if (!mediaRelevant && !isActive) continue;
    list.push({ tabId, active: isActive, audible: audible.get(tabId) ?? null, url: entry.url, title: entry.pageTitle, media: mediaRelevant ? entry.media : null, updatedAt: entry.updatedAt });
  }
  if (activeId !== null && !tabs.has(activeId)) {
    const tab = await chrome.tabs.get(activeId).catch(() => null);
    if (tab && /^https?:/.test(tab.url || "")) {
      list.push({ tabId: activeId, active: true, url: tab.url, title: tab.title, media: null, updatedAt: Date.now() });
    }
  }
  send({ type: "tabs", tabs: list });
}

chrome.runtime.onMessage.addListener((message, sender, reply) => {
  if (message.type === "state" && sender.tab) {
    const previous = tabs.get(sender.tab.id);
    tabs.set(sender.tab.id, { ...message.state, updatedAt: Date.now() });
    if (!previous || JSON.stringify(previous.media && { ...previous.media, positionMs: 0 }) !== JSON.stringify(message.state.media && { ...message.state.media, positionMs: 0 }) || previous.url !== message.state.url) {
      scheduleReport();
    }
    return;
  }
  if (message.type === "ready" && sender.tab) {
    applyPendingSeek(sender.tab.id, message.url);
    return;
  }
  // From the popup.
  if (message.type === "popup") {
    activeTabId().then((tabId) => reply({ connected: !!(socket && socket.readyState === WebSocket.OPEN), devices, tab: tabId !== null ? tabs.get(tabId) : null, tabId }));
    return true;
  }
  if (message.type === "sendTab") {
    send({ type: "send", tabId: message.tabId, targetDeviceId: message.targetDeviceId });
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  tabs.delete(tabId);
  scheduleReport();
});
chrome.tabs.onActivated.addListener(() => scheduleReport());
chrome.windows.onFocusChanged.addListener(() => scheduleReport());
chrome.tabs.onUpdated.addListener((tabId, change) => {
  if ("audible" in change || "mutedInfo" in change) scheduleReport();
  if (change.url || change.title) {
    const entry = tabs.get(tabId);
    if (entry && change.url) entry.url = change.url;
    if (entry && change.title) entry.pageTitle = change.title;
    scheduleReport();
  }
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  const target = devices.find((d) => d.online);
  if (!target || !tab) return;
  if (info.linkUrl) {
    send({ type: "sendUrl", url: info.linkUrl, title: info.linkUrl, targetDeviceId: target.deviceId });
  } else {
    send({ type: "send", tabId: tab.id, targetDeviceId: target.deviceId });
  }
});

async function onHostMessage(message) {
  switch (message.type) {
    case "devices":
      devices = message.devices;
      break;
    case "ping":
      send({ type: "pong" });
      break;
    case "take": {
      const tab = await chrome.tabs.get(message.tabId).catch(() => null);
      let snapshot = null;
      if (tab) {
        snapshot = await askTab(message.tabId, { type: "take", pause: message.pause });
        snapshot = snapshot || { url: tab.url, pageTitle: tab.title, media: null };
        tabs.set(message.tabId, { ...snapshot, updatedAt: Date.now() });
        // Report the pause at once: the page's next update now matches what is stored, so it
        // wouldn't, and Baton would go on showing this tab as playing.
        scheduleReport(0);
      }
      const tabState = snapshot && { tabId: message.tabId, active: true, url: snapshot.url, title: snapshot.pageTitle, media: snapshot.media, updatedAt: Date.now() };
      send({ type: "taken", requestId: message.requestId, tabId: message.tabId, tab: tabState });
      break;
    }
    case "resume": {
      // Back from the phone: continue in the tab it left, not a new one. A tab the browser
      // unloaded meanwhile is reloaded at the phone's position (its link carries the time).
      const before = await chrome.tabs.get(message.tabId).catch(() => null);
      const reload = before && before.discarded && message.url;
      const tab = await chrome.tabs.update(message.tabId, reload ? { active: true, url: message.url } : { active: true }).catch(() => null);
      if (!tab) {
        send({ type: "resumed", requestId: message.requestId, applied: false });
        break;
      }
      chrome.windows.update(tab.windowId, { focused: true }).catch(() => {});
      // A tab the browser unloaded meanwhile reloads now; the seek then waits for its player.
      pendingSeeks.push({ url: tab.url, positionMs: message.positionMs, expiresAt: Date.now() + PENDING_SEEK_MS });
      const result = reload ? { applied: true, reason: "reloading at the link's time" }
        : (await askTab(message.tabId, { type: "seek", positionMs: message.positionMs, play: true })) || { applied: false, reason: "no page script" };
      send({ type: "resumed", requestId: message.requestId, applied: !!result.applied });
      send({ type: "log", text: `resume tab ${message.tabId} at ${message.positionMs} ms: ${JSON.stringify(result)}` });
      break;
    }
    case "command":
      askTab(message.tabId, { type: "command", action: message.action, positionMs: message.positionMs, volume: message.volume });
      break;
    case "expect":
      pendingSeeks.push({ url: message.url, positionMs: message.positionMs, expiresAt: Date.now() + PENDING_SEEK_MS });
      break;
  }
}

function sameContent(a, b) {
  try {
    const ua = new URL(a);
    const ub = new URL(b);
    const va = ua.searchParams.get("v");
    const vb = ub.searchParams.get("v");
    if (va || vb) return va === vb;
    return ua.host === ub.host && ua.pathname.replace(/\/$/, "") === ub.pathname.replace(/\/$/, "");
  } catch {
    return false;
  }
}

function applyPendingSeek(tabId, url) {
  pendingSeeks = pendingSeeks.filter((p) => p.expiresAt > Date.now());
  const index = pendingSeeks.findIndex((p) => sameContent(p.url, url));
  if (index < 0) return;
  const [seek] = pendingSeeks.splice(index, 1);
  chrome.tabs.sendMessage(tabId, { type: "seek", positionMs: seek.positionMs, play: true }).catch(() => {});
}
