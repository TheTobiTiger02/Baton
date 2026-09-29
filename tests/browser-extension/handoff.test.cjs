const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const { randomUUID } = require('node:crypto');

function background() {
  const event = () => ({ addListener() {} });
  const sockets = [];
  class WebSocket {
    static OPEN = 1; static CONNECTING = 0;
    constructor() { this.readyState = 1; this.sent = []; sockets.push(this); }
    send(value) { this.sent.push(JSON.parse(value)); }
  }
  const chrome = {
    storage: { local: { get: async () => ({}), set: async () => {} } },
    runtime: { onStartup: event(), onInstalled: event(), onMessage: event(), getManifest: () => ({ version: '0.2.0' }) },
    alarms: { create() {}, onAlarm: event() },
    tabs: { query: async () => [{ id: 1 }], onRemoved: event(), onActivated: event(), onUpdated: event() },
    windows: { onFocusChanged: event() },
    contextMenus: { onClicked: event(), removeAll(callback) { callback(); }, create() {} }
  };
  const context = vm.createContext({ chrome, WebSocket, crypto: { randomUUID }, navigator: { userAgent: 'Test' },
    setTimeout: () => 1, clearTimeout() {}, URL, console });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../browser-extension/background.js'), 'utf8'), context);
  return { run: source => vm.runInContext(source, context), socket: sockets[0] };
}

test('pending send only becomes opened after correlated host result', () => {
  const b = background();
  b.run('onHostMessage({type:"capabilities",handoffStatus:true})');
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  assert.equal(transfer.status, 'pending');
  assert.equal(b.socket.sent[0].requestId, transfer.requestId);
  b.run(`onHostMessage({type:"handoffStatus",requestId:${JSON.stringify(transfer.requestId)},status:"opened",detail:"Opened"})`);
  assert.equal(transfer.status, 'opened');
  b.run(`onHostMessage({type:"handoffStatus",requestId:${JSON.stringify(transfer.requestId)},status:"pending"})`);
  assert.equal(transfer.status, 'opened');
});

test('disconnect is uncertain and a late result can resolve it', () => {
  const b = background(); b.run('confirmsHandoffs=true');
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  b.socket.onclose();
  assert.equal(transfer.status, 'unconfirmed');
  b.run(`onHostMessage({type:"handoffStatus",requestId:${JSON.stringify(transfer.requestId)},status:"fallback",detail:"Search opened"})`);
  assert.equal(transfer.status, 'fallback');
});

test('old hosts report unavailable confirmation instead of success', () => {
  const b = background();
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  assert.equal(transfer.status, 'unsupported');
  assert.match(transfer.detail, /destination status unavailable/);
});

test('immediate send failure and retry preserve original correlation', () => {
  const b = background(); b.run('confirmsHandoffs=true');
  const old = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  b.run(`startTransfer({tabId:1,targetDeviceId:"phone",previousRequestId:${JSON.stringify(old.requestId)}})`);
  assert.equal(b.socket.sent[1].type, 'handoffRetry');
  assert.equal(b.socket.sent[1].previousRequestId, old.requestId);
  assert.notEqual(b.socket.sent[1].requestId, old.requestId);
  b.socket.readyState = 3;
  assert.equal(b.run('startTransfer({tabId:1,targetDeviceId:"phone"})').status, 'failed');
});

test('popup reopening keeps history and background retains at most forty', async () => {
  const b = background(); b.run('confirmsHandoffs=true');
  b.run('for(let i=0;i<45;i++) startTransfer({tabId:1,targetDeviceId:"phone"})');
  assert.equal(b.run('transfers.size'), 40);
  assert.equal(b.run('[...transfers.values()].filter(t=>t.tabId===1).length'), 40);
});

test('unknown or duplicate final results cannot alter another transfer', () => {
  const b = background(); b.run('confirmsHandoffs=true');
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  b.run('onHostMessage({type:"handoffStatus",requestId:"other",status:"opened"})');
  assert.equal(transfer.status, 'pending');
  b.run(`onHostMessage({type:"handoffStatus",requestId:${JSON.stringify(transfer.requestId)},status:"failed",detail:"Closed"})`);
  b.run(`onHostMessage({type:"handoffStatus",requestId:${JSON.stringify(transfer.requestId)},status:"opened"})`);
  assert.equal(transfer.status, 'failed');
});

test('retrying an unsent request preserves its tab and rejects changed content', () => {
  const b = background(); b.run('confirmsHandoffs=true; tabs.set(1,{url:"https://example.com"})');
  b.socket.readyState = 3;
  const failed = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  b.socket.readyState = 1;
  b.run(`startTransfer({tabId:9,targetDeviceId:"other",previousRequestId:${JSON.stringify(failed.requestId)}})`);
  assert.equal(b.socket.sent[0].type, 'send');
  assert.equal(b.socket.sent[0].tabId, 1);
  assert.equal(b.socket.sent[0].targetDeviceId, 'phone');
  b.run('tabs.set(1,{url:"https://example.com/other"})');
  assert.equal(b.run(`startTransfer({tabId:1,targetDeviceId:"phone",previousRequestId:${JSON.stringify(failed.requestId)}})`).status, 'failed');
  assert.equal(b.socket.sent.length, 1);
});

test('context menu links receive the same correlated feedback', () => {
  const b = background(); b.run('confirmsHandoffs=true');
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone",url:"https://example.com/link"})');
  assert.equal(b.socket.sent[0].type, 'sendUrl');
  assert.equal(b.socket.sent[0].requestId, transfer.requestId);
});

test('host-reported streaming failure remains visible after viewer opened', () => {
  const b = background(); b.run('confirmsHandoffs=true');
  const transfer = b.run('startTransfer({tabId:1,targetDeviceId:"phone"})');
  const requestId = JSON.stringify(transfer.requestId);
  b.run(`onHostMessage({type:"handoffStatus",requestId:${requestId},status:"opened",detail:"Viewer opened"})`);
  b.run(`onHostMessage({type:"handoffStatus",requestId:${requestId},status:"failed",detail:"Consent declined"})`);
  assert.equal(transfer.status, 'failed');
  assert.equal(transfer.detail, 'Consent declined');
});

function popup(state, confirm = () => true) {
  class Element {
    constructor(tag) { this.tag = tag; this.children = []; this.listeners = {}; }
    set textContent(value) { this.text = value; this.children = []; }
    get textContent() { return this.text || ''; }
    append(child) { this.children.push(child); }
    addEventListener(type, callback) { this.listeners[type] = callback; }
  }
  const root = new Element('root');
  const sent = [];
  const chrome = { runtime: { sendMessage(message, reply) {
    if (message.type === 'popup') reply(state);
    else { sent.push(message); reply({}); }
  } } };
  const context = vm.createContext({ document: { getElementById: () => root, createElement: tag => new Element(tag) },
    chrome, window: { confirm }, setInterval() {} });
  vm.runInContext(fs.readFileSync(path.join(__dirname, '../../browser-extension/popup.js'), 'utf8'), context);
  const all = () => { const result = []; const visit = node => { result.push(node); node.children.forEach(visit); }; visit(root); return result; };
  return { all, sent };
}

const popupState = transfer => ({ connected: true, awaitingApproval: false, confirmsHandoffs: true,
  tabId: 7, tab: { pageTitle: 'Original' }, devices: [{ online: true, deviceId: 'phone', name: 'S21' }], transfers: [transfer] });

test('popup shows reported progress and keeps failures with Retry when reopened', () => {
  const state = popupState({ requestId: 'id', tabId: 7, targetDeviceId: 'phone', status: 'pending', detail: 'Sending' });
  assert.ok(popup(state).all().some(node => node.textContent === 'Sending…'));
  state.transfers[0].status = 'failed';
  const reopened = popup(state);
  assert.ok(reopened.all().some(node => node.textContent === "Couldn't continue"));
  const retry = reopened.all().find(node => node.textContent === 'Retry');
  assert.equal(retry.disabled, false);
  retry.listeners.click();
  assert.equal(reopened.sent[0].previousRequestId, 'id');
  assert.equal(reopened.sent[0].targetDeviceId, 'phone');
});

test('popup requires uncertainty confirmation and disables offline recovery', () => {
  const state = popupState({ requestId: 'id', tabId: 7, targetDeviceId: 'phone', status: 'unconfirmed', detail: 'May already be open' });
  const declined = popup(state, () => false);
  declined.all().find(node => node.textContent === 'Retry').listeners.click();
  assert.equal(declined.sent.length, 0);
  state.connected = false;
  assert.equal(popup(state).all().find(node => node.textContent === 'Retry').disabled, true);
});
