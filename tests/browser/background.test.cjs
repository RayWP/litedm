const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const backgroundSource = fs.readFileSync(
  path.join(__dirname, '../../extension/background.js'),
  'utf8',
);

function loadBackground() {
  const listeners = { installed: null, clicked: null, message: null };
  const nativeCalls = [];
  const menuItems = [];
  const stored = {};
  const actionState = {};
  const chrome = {
    runtime: {
      lastError: undefined,
      onInstalled: { addListener: (listener) => { listeners.installed = listener; } },
      onMessage: { addListener: (listener) => { listeners.message = listener; } },
      sendNativeMessage: (host, message, callback) => nativeCalls.push({ host, message, callback }),
    },
    contextMenus: {
      removeAll: (callback) => callback(),
      create: (item) => menuItems.push(item),
      onClicked: { addListener: (listener) => { listeners.clicked = listener; } },
    },
    storage: {
      local: { set: (value, callback) => { Object.assign(stored, value); callback?.(); } },
    },
    action: {
      setBadgeBackgroundColor: (value) => { actionState.badgeColor = value.color; },
      setBadgeText: (value) => { actionState.badgeText = value.text; },
      setTitle: (value) => { actionState.title = value.title; },
    },
  };
  vm.runInNewContext(backgroundSource, { chrome, URL, Date, console });
  return { listeners, nativeCalls, menuItems, stored, actionState, chrome };
}

test('context menu handoff sends the expected request and reports review acknowledgement', () => {
  const state = loadBackground();
  state.listeners.installed();
  assert.equal(JSON.stringify(state.menuItems), JSON.stringify([
    { id: 'litedm-download-link', title: 'Download with LiteDM', contexts: ['link'] },
  ]));

  state.listeners.clicked({ menuItemId: 'litedm-download-link', linkUrl: 'https://example.test/file.zip' });

  assert.equal(state.nativeCalls.length, 1);
  assert.equal(state.nativeCalls[0].host, 'com.litedm.bridge');
  assert.equal(JSON.stringify(state.nativeCalls[0].message), JSON.stringify({
    action: 'addDownload', url: 'https://example.test/file.zip',
  }));
  state.nativeCalls[0].callback({ accepted: true });
  assert.equal(state.stored.lastHandoff.ok, true);
  assert.match(state.stored.lastHandoff.message, /for review/i);
  assert.equal(state.actionState.badgeText, 'OK');
});

test('unsupported URL is reported without launching the native host', () => {
  const state = loadBackground();

  state.listeners.clicked({ menuItemId: 'litedm-download-link', linkUrl: 'file:///C:/private.txt' });

  assert.equal(state.nativeCalls.length, 0);
  assert.equal(state.stored.lastHandoff.ok, false);
  assert.match(state.stored.lastHandoff.message, /HTTP and HTTPS/i);
  assert.equal(state.stored.lastHandoff.url, '');
  assert.equal(state.actionState.badgeText, '!');
});

test('native host rejection remains visible and provides a retry target', () => {
  const state = loadBackground();
  const url = 'https://example.test/asset';
  state.listeners.clicked({ menuItemId: 'litedm-download-link', linkUrl: url });
  state.nativeCalls[0].callback({ accepted: false, error: 'LiteDM did not start.' });

  assert.equal(state.stored.lastHandoff.ok, false);
  assert.equal(state.stored.lastHandoff.message, 'LiteDM did not start.');
  assert.equal(state.stored.lastHandoff.url, url);
  state.listeners.message({ type: 'retry', url: state.stored.lastHandoff.url });
  assert.equal(state.nativeCalls.length, 2);
  assert.equal(JSON.stringify(state.nativeCalls[1].message), JSON.stringify({ action: 'addDownload', url }));
});

test('browser native messaging errors are stored for popup feedback and can be retried', () => {
  const state = loadBackground();
  const url = 'https://example.test/asset';
  state.listeners.clicked({ menuItemId: 'litedm-download-link', linkUrl: url });
  state.chrome.runtime.lastError = { message: 'Specified native messaging host not found.' };
  state.nativeCalls[0].callback(undefined);

  assert.equal(state.stored.lastHandoff.ok, false);
  assert.equal(state.stored.lastHandoff.message, 'Specified native messaging host not found.');
  assert.equal(state.stored.lastHandoff.url, url);
  state.listeners.message({ type: 'retry', url: state.stored.lastHandoff.url });
  assert.equal(state.nativeCalls.length, 2);
});

test('popup open control sends a show request', () => {
  const state = loadBackground();
  state.listeners.message({ type: 'open' });

  assert.equal(JSON.stringify(state.nativeCalls[0].message), JSON.stringify({ action: 'show', url: '' }));
});
