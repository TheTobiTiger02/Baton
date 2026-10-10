const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

function background(open = [], stored = {}) {
  const events = {};
  const event = name => ({ addListener(fn) { events[name] = fn; } });
  const inventory = new Map(open.map(tab => [tab.id, { ...tab }]));
  const updates = [], windows = [], messages = [];
  let player = { applied: true };
  class WebSocket {
    static OPEN = 1; static CONNECTING = 0;
    constructor() { this.readyState = 1; this.sent = []; }
    send(value) { this.sent.push(JSON.parse(value)); }
  }
  const chrome = {
    storage: { local: { get: async () => stored, set: async value => Object.assign(stored, value) } },
    runtime: { onStartup: event('startup'), onInstalled: event('install'), onMessage: event('message'), getManifest: () => ({ version: '0.2.1' }) },
    alarms: { create() {}, onAlarm: event('alarm') },
    scripting: { executeScript: async () => {} },
    tabs: {
      query: async () => [...inventory.values()],
      get: async id => { if (!inventory.has(id)) throw Error('closed'); return inventory.get(id); },
      update: async (id, value) => { updates.push({ id, ...value }); Object.assign(inventory.get(id), value); return inventory.get(id); },
      create: async value => { const tab = { id: 99, windowId: 9, pinned: false, ...value }; inventory.set(tab.id, tab); return tab; },
      sendMessage: async (id, message) => { messages.push({ id, ...message }); return player; },
      onRemoved: event('removed'), onActivated: event('activated'), onUpdated: event('updated')
    },
    windows: { update: async id => windows.push(id), onFocusChanged: event('focus') },
    contextMenus: { onClicked: event('menu'), removeAll(fn) { fn(); }, create() {} }
  };
  const context = vm.createContext({ chrome, WebSocket, crypto: { randomUUID }, navigator: { userAgent: 'Test' },
    setTimeout: () => 1, clearTimeout() {}, URL, console });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../browser-extension/background.js'), 'utf8'), context);
  const run = source => vm.runInContext(source, context);
  return { run, events, inventory, updates, windows, messages, socket: run('socket'), setPlayer: value => { player = value; },
    resume: async value => { await run(`onHostMessage(${JSON.stringify({ type: 'resume', requestId: 'return', tabId: 7,
      url: 'https://www.twitch.tv/trymacs', expectedUrl: 'https://www.twitch.tv/trymacs', mode: 'live', positionMs: 90_000, ...value })})`);
      return run('socket').sent.filter(m => m.type === 'resumed').at(-1); } };
}

for (const pinned of [false, true]) test(`same and changed channel preserve tab identity, pin=${pinned}, and its own window`, async () => {
  const b = background([{ id: 7, url: 'https://www.twitch.tv/trymacs', pinned, windowId: 4 }]);
  assert.equal((await b.resume()).applied, true);
  assert.equal(b.updates[0].url, undefined);
  const result = await b.resume({ url: 'https://www.twitch.tv/papaplatte' });
  assert.equal(result.activated, true);
  assert.equal(result.applied, false); // Loading a new player isn't confirmed playback.
  assert.equal(b.inventory.size, 1);
  assert.equal(b.inventory.get(7).pinned, pinned);
  assert.equal(b.inventory.get(7).url, 'https://www.twitch.tv/papaplatte');
  assert.deepEqual(b.windows, [4, 4]);
});

test('discarded original tab is restored in place', async () => {
  const b = background([{ id: 7, url: 'https://www.twitch.tv/trymacs', pinned: true, discarded: true, windowId: 2 }]);
  assert.equal((await b.resume()).activated, true);
  assert.equal(b.updates[0].url, 'https://www.twitch.tv/trymacs');
  assert.equal(b.inventory.size, 1);
});

test('ordinary pages activate without asking for a player', async () => {
  const b = background([{ id: 7, url: 'https://example.com/article', pinned: true, windowId: 2 }]);
  b.setPlayer(null);
  const result = await b.resume({ mode: 'page', url: 'https://example.com/article', expectedUrl: 'https://example.com/article' });
  assert.equal(result.applied, true);
  assert.equal(b.messages.length, 0);
});

for (const player of [null, { applied: false, reason: 'no player' }, { applied: false, reason: 'autoplay rejected' }])
  test(`activation survives unavailable playback: ${JSON.stringify(player)}`, async () => {
    const b = background([{ id: 7, url: 'https://www.twitch.tv/trymacs', windowId: 3 }]);
    b.setPlayer(player);
    const result = await b.resume();
    assert.equal(result.activated, true);
    assert.equal(result.applied, false);
    assert.equal(b.inventory.size, 1);
    assert.equal(b.run('pendingSeeks.length'), 1);
    b.setPlayer({ applied: true });
    await b.run('applyPendingSeek(7,"https://www.twitch.tv/trymacs",0)');
    assert.equal(b.run('pendingSeeks.length'), 0);
  });

test('delayed correction is scoped to the original tab', async () => {
  const b = background([{ id: 7, url: 'https://www.twitch.tv/trymacs', windowId: 1 }]);
  await b.resume({ url: 'https://www.twitch.tv/papaplatte' });
  await b.run('applyPendingSeek(8,"https://www.twitch.tv/papaplatte",0)');
  assert.equal(b.messages.length, 0);
  await b.run('applyPendingSeek(7,"https://www.twitch.tv/papaplatte",0)');
  assert.equal(b.messages[0].mode, 'live');
});

for (const repurposed of [false, true]) test(`missing source uses exact inventory match, repurposed=${repurposed}`, async () => {
  const b = background([...(repurposed ? [{ id: 7, url: 'https://example.com/unrelated', windowId: 1 }] : []),
    { id: 8, url: 'https://m.twitch.tv/Trymacs', pinned: false, windowId: 6 }]);
  const result = await b.resume({ allowCreate: true });
  assert.equal(result.tabId, 8);
  assert.equal(b.inventory.has(99), false);
  if (repurposed) assert.equal(b.inventory.get(7).url, 'https://example.com/unrelated');
  assert.deepEqual(b.windows, [6]);
});

test('missing match opens one tab and duplicate requests reuse it', async () => {
  const b = background([{ id: 7, url: 'https://example.com/unrelated', windowId: 1 }]);
  assert.equal((await b.resume({ allowCreate: true })).tabId, 99);
  assert.equal((await b.resume({ allowCreate: true })).tabId, 99);
  assert.equal(b.inventory.size, 2);
});

test('profile identity persists through worker reload and reconnect', async () => {
  const stored = {};
  const first = background([], stored);
  await first.socket.onopen();
  const id = first.socket.sent.find(m => m.type === 'hello').browserInstanceId;
  const second = background([], stored);
  await second.socket.onopen();
  assert.equal(second.socket.sent.find(m => m.type === 'hello').browserInstanceId, id);
  second.socket.onclose();
  second.run('connect()');
  await second.run('socket.onopen()');
  assert.equal(second.run('socket.sent[0].browserInstanceId'), id);
});

test('exact identity includes host, port and meaningful queries, ignoring time and tracking', () => {
  const b = background();
  const same = (a, c) => b.run(`sameContent(${JSON.stringify(a)},${JSON.stringify(c)})`);
  assert.equal(same('https://example.com/item?id=1', 'https://example.com/item?id=2'), false);
  assert.equal(same('https://example.com/watch?v=abcdef', 'https://evil.test/watch?v=abcdef'), false);
  assert.equal(same('https://example.com:8000/a', 'https://example.com/a'), false);
  assert.equal(same('https://example.com/a?id=1&t=50&utm_source=test', 'https://www.example.com/a?id=1'), true);
  assert.equal(same('https://m.twitch.tv/Trymacs', 'https://www.twitch.tv/trymacs?t=1h'), true);
});

function content(duration, rejects = false, previousVersion) {
  let listener;
  let listeners = 0;
  const player = { readyState: 4, paused: true, ended: false, currentTime: 40, duration,
    getBoundingClientRect: () => ({ width: 100, height: 100 }), addEventListener() {}, removeEventListener() {},
    play: async () => { if (rejects) throw Error('autoplay rejected'); player.paused = false; } };
  const window = { scrollY: 0, addEventListener() {}, __batonContent: previousVersion }; window.top = window;
  const context = vm.createContext({ window, document: { title: 'Video', querySelectorAll: () => [player], addEventListener() {} },
    navigator: {}, location: { href: 'https://example.com/video', hostname: 'example.com' },
    chrome: { runtime: { getManifest: () => ({ version: '0.2.1' }), sendMessage: async () => {},
      onMessage: { addListener(fn) { listener = fn; listeners++; } } } },
    performance: { now: () => 100 }, setInterval() {}, setTimeout() {}, clearTimeout() {}, URL });
  const source = fs.readFileSync(path.join(__dirname, '../../browser-extension/content.js'), 'utf8');
  vm.runInContext(source, context);
  return { player, reload: () => vm.runInContext(source, context), listeners: () => listeners,
    seek: mode => new Promise(resolve => listener({ type: 'seek', mode, positionMs: 90_000, play: true }, {}, resolve)) };
}

for (const previousVersion of [true, '0.2.0']) test(`extension update replaces obsolete page marker: ${previousVersion}`, async () => {
  const c = content(1800, false, previousVersion);
  assert.equal(c.listeners(), 1);
  assert.equal((await c.seek('live')).applied, true);
  assert.equal(c.player.currentTime, 40);
  c.reload();
  assert.equal(c.listeners(), 1);
});

test('live channels play without historical seek even when duration is finite', async () => {
  const c = content(1800);
  assert.equal((await c.seek('live')).applied, true);
  assert.equal(c.player.currentTime, 40);
});
test('recorded videos restore position and rejected play is reported', async () => {
  const c = content(1800);
  assert.equal((await c.seek('seek')).applied, true);
  assert.equal(c.player.currentTime, 90);
  assert.equal((await content(1800, true).seek('seek')).applied, false);
});
