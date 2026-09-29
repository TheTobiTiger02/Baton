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
// The token Baton gave this browser when the user allowed it (store builds), or the one a
// development build carries in config.js. Until Baton knows it, the popup says to allow it.
let token = globalThis.BATON_TOKEN || null;
let awaitingApproval = false;
let confirmsHandoffs = false;
const transfers = new Map();

function startTransfer(message) {
  const previous = message.previousRequestId && transfers.get(message.previousRequestId);
  if (previous) message = { ...message, tabId: previous.tabId, targetDeviceId: previous.targetDeviceId,
    url: previous.url, sourceUrl: previous.sourceUrl };
  const requestId = crypto.randomUUID();
  const transfer = { requestId, tabId: message.tabId, targetDeviceId: message.targetDeviceId, url: message.url,
    sourceUrl: message.sourceUrl || tabs.get(message.tabId)?.url, status: "pending", detail: "Sending…", startedAt: Date.now() };
  transfers.set(requestId, transfer);
  while (transfers.size > 40) transfers.delete(transfers.keys().next().value);
  if (message.previousRequestId && (!previous || !previous.sent && !previous.url &&
      (!previous.sourceUrl || tabs.get(previous.tabId)?.url !== previous.sourceUrl))) {
    Object.assign(transfer, { status: "failed", detail: "The original activity is no longer available. Open the original page to continue it again." });
    return transfer;
  }
  const sent = send(previous?.sent
    ? { type: "handoffRetry", requestId, previousRequestId: previous.requestId }
    : message.url ? { type: "sendUrl", requestId, url: message.url, targetDeviceId: message.targetDeviceId }
    : { type: "send", requestId, tabId: message.tabId, targetDeviceId: message.targetDeviceId });
  transfer.sent = sent;
  if (!sent) Object.assign(transfer, { status: "failed", detail: "Baton is disconnected. Reconnect before retrying." });
  else if (!confirmsHandoffs) Object.assign(transfer, { status: "unsupported", detail: "Request sent; destination status unavailable. Update Baton on this PC for confirmed feedback." });
  return transfer;
}

const tokenLoaded = chrome.storage.local.get("batonToken").then((stored) => {
  if (stored && stored.batonToken) token = stored.batonToken;
}).catch(() => {});

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
    // Anything but Baton's development build proves itself with its token; without one, Baton
    // asks the user to allow this browser and then sends one.
    await tokenLoaded;
    send({ type: "hello", browser: await browserName(), token, version: chrome.runtime.getManifest().version });
    scheduleReport(0);
  };
  socket.onmessage = (event) => onHostMessage(JSON.parse(event.data));
  socket.onclose = () => {
    socket = null;
    devices = [];
    confirmsHandoffs = false;
    for (const transfer of transfers.values()) {
      if (transfer.status === "pending") Object.assign(transfer, { status: "unconfirmed", detail: "Connection lost. The activity may already have opened." });
    }
  };
  socket.onerror = () => {};
}

function send(message) {
  if (!socket || socket.readyState !== WebSocket.OPEN) return false;
  try { socket.send(JSON.stringify(message)); return true; } catch { return false; }
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
    ? chrome.scripting.executeScript({ target: { tabId, allFrames: true }, files: ["content.js"] })
    : chrome.tabs.executeScript(tabId, { file: "content.js", allFrames: true });
}

async function injectIntoOpenTabs() {
  const open = await chrome.tabs.query({ url: ["http://*/*", "https://*/*"] }).catch(() => []);
  for (const tab of open) inject(tab.id).catch(() => {});
}

/**
 * Asks the page script of the frame playing in a tab (the page itself unless an embedded player
 * does); a tab without one gets it injected and is asked once more.
 */
async function askTab(tabId, message) {
  const frameId = (tabs.get(tabId) || {}).frameId || 0;
  try {
    return await chrome.tabs.sendMessage(tabId, message, { frameId });
  } catch {
    await inject(tabId).catch(() => {});
    return chrome.tabs.sendMessage(tabId, message, { frameId }).catch(() => null);
  }
}

chrome.runtime.onInstalled.addListener(() => {
  connect();
  injectIntoOpenTabs();
  buildMenu();
});

/**
 * One right-click entry per phone online, the default phone (Baton's shortcut target) first;
 * browsers group them under "Baton" by themselves. Rebuilt whenever the phones change.
 */
function buildMenu() {
  chrome.contextMenus.removeAll(() => {
    const online = devices.filter((d) => d.online).sort((a, b) => (b.isDefault ? 1 : 0) - (a.isDefault ? 1 : 0));
    if (online.length === 0) {
      chrome.contextMenus.create({ id: "baton-none", title: "Continue on phone (none connected)", contexts: ["link", "page"], enabled: false });
      return;
    }
    for (const device of online) {
      chrome.contextMenus.create({ id: `baton-send:${device.deviceId}`, title: `Continue on ${device.name}`, contexts: ["link", "page"] });
    }
  });
}
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
    list.push({ tabId, active: isActive, audible: audible.get(tabId) ?? null, url: entry.url, title: entry.pageTitle, media: mediaRelevant ? entry.media : null, anchor: entry.anchor || null, updatedAt: entry.updatedAt, playingSince: entry.playingSince || null });
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
    let frameId = sender.frameId || 0;
    let current = message.state;
    if (frameId !== 0) {
      // An embedded player: its media, the tab's own address and title.
      current = { ...current, url: sender.tab.url, pageTitle: sender.tab.title };
    } else if (!current.media && previous && previous.frameId && previous.media) {
      // The page itself has no player but one of its frames does: keep that one.
      current = { ...previous, url: current.url, pageTitle: current.pageTitle };
      frameId = previous.frameId;
    }
    // When it started playing, not when it last reported: the newest thing started wins.
    const playing = current.media && current.media.playing;
    const playingSince = playing ? (previous && previous.playingSince && previous.media && previous.media.playing ? previous.playingSince : Date.now()) : null;
    tabs.set(sender.tab.id, { ...current, frameId, playingSince, updatedAt: Date.now() });
    if (!previous || JSON.stringify(previous.media && { ...previous.media, positionMs: 0 }) !== JSON.stringify(current.media && { ...current.media, positionMs: 0 }) || previous.url !== current.url || previous.anchor !== current.anchor) {
      scheduleReport();
    }
    return;
  }
  if (message.type === "ready" && sender.tab) {
    applyPendingSeek(sender.tab.id, sender.frameId ? sender.tab.url : message.url, sender.frameId || 0);
    return;
  }
  // From the popup.
  if (message.type === "popup") {
    activeTabId().then((tabId) => reply({ connected: !!(socket && socket.readyState === WebSocket.OPEN), awaitingApproval, devices, confirmsHandoffs, transfers: [...transfers.values()].filter(t => t.tabId === tabId).reverse(), tab: tabId !== null ? tabs.get(tabId) : null, tabId }));
    return true;
  }
  if (message.type === "sendTab") {
    if (sender.tab) return;
    reply(startTransfer(message));
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
  const id = String(info.menuItemId);
  if (!id.startsWith("baton-send:") || !tab) return;
  const targetDeviceId = id.slice("baton-send:".length);
  startTransfer({ tabId: tab.id, targetDeviceId, url: info.linkUrl, sourceUrl: tab.url });
});

async function onHostMessage(message) {
  switch (message.type) {
    case "capabilities":
      confirmsHandoffs = message.handoffStatus === true;
      break;
    case "handoffStatus": {
      const transfer = transfers.get(message.requestId);
      if (transfer && (transfer.status === "pending" || transfer.status === "unconfirmed" ||
          message.status === "failed" && ["opened", "fallback"].includes(transfer.status))) {
        Object.assign(transfer, { status: message.status, detail: message.detail || "Destination reported the activity opened.", title: message.title });
      }
      break;
    }
    case "devices":
      devices = message.devices;
      awaitingApproval = false;
      buildMenu();
      break;
    case "approval":
      awaitingApproval = message.state === "waiting";
      break;
    case "token":
      token = message.token;
      awaitingApproval = false;
      chrome.storage.local.set({ batonToken: token }).catch(() => {});
      scheduleReport(0);
      break;
    case "ping":
      send({ type: "pong" });
      break;
    case "take": {
      const tab = await chrome.tabs.get(message.tabId).catch(() => null);
      let snapshot = null;
      if (tab) {
        snapshot = await askTab(message.tabId, { type: "take", pause: message.pause });
        // An embedded player answers with its frame's address; the tab's is what continues.
        snapshot = { ...(snapshot || { media: null }), url: tab.url, pageTitle: tab.title };
        const frameId = (tabs.get(message.tabId) || {}).frameId || 0;
        tabs.set(message.tabId, { ...snapshot, frameId, playingSince: null, updatedAt: Date.now() });
        // Report the pause at once: the page's next update now matches what is stored, so it
        // wouldn't, and Baton would go on showing this tab as playing.
        scheduleReport(0);
      }
      const tabState = snapshot && { tabId: message.tabId, active: true, url: snapshot.url, title: snapshot.pageTitle, media: snapshot.media, anchor: snapshot.anchor || null, updatedAt: Date.now() };
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

function applyPendingSeek(tabId, url, frameId) {
  pendingSeeks = pendingSeeks.filter((p) => p.expiresAt > Date.now());
  const index = pendingSeeks.findIndex((p) => sameContent(p.url, url));
  if (index < 0) return;
  const [seek] = pendingSeeks.splice(index, 1);
  chrome.tabs.sendMessage(tabId, { type: "seek", positionMs: seek.positionMs, play: true }, { frameId }).catch(() => {});
}
