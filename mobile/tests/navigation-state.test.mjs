import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../src/main.ts', import.meta.url), 'utf8')
  .replace(/^import .*;\n/gm, '').replace(/void loadSongs\(\);\s*$/, '') + `
globalThis.fixture = { login, openSong, loadSongs, signOut,
  setQuery: (value: string) => { query = value; }, getToken: () => token };`;
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

class Element {
  hidden = false; open = false; value = ''; textContent = ''; children = []; attributes = {};
  classList = { toggle: () => false };
  setAttribute(name, value) { this.attributes[name] = value; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; }
  querySelectorAll() { return []; }
  close() { this.open = false; }
  showModal() { this.open = true; }
  reset() {}
  remove() {}
}

function setup({ digest = (...args) => webcrypto.subtle.digest(...args) } = {}) {
  const elements = new Map(), listeners = {}, calls = [], loginURLs = [];
  const browserOpenWaiters = [];
  const waitForBrowserOpen = () => new Promise(resolve => { browserOpenWaiters.push(resolve); });
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const transport = { request: async options => { throw new Error(`Unexpected request: ${options.url}`); } };
  const request = options => { calls.push(options); return transport.request(options); };
  const context = {
    console, URL, TextEncoder, AbortSignal,
    crypto: { getRandomValues: values => webcrypto.getRandomValues(values), subtle: { digest } },
    btoa: value => Buffer.from(value, 'binary').toString('base64'),
    setInterval: () => 0, clearInterval() {}, clearTimeout() {},
    document: { getElementById: element, createElement: () => new Element(), querySelector: element,
      querySelectorAll: () => [], head: new Element() },
    window: { addEventListener() {}, removeEventListener() {}, scrollTo() {}, setInterval: () => 0,
      setTimeout: () => 0, location: { origin: 'http://fixture.invalid' } },
    diagnosticAudio: { stop() {} }, initializeDiagnosticAudio() {},
    Capacitor: { isNativePlatform: () => true }, CapacitorHttp: { request, get: request },
    App: { addListener: async (name, listener) => { listeners[name] = listener; } },
    Browser: { addListener: async (name, listener) => { listeners[name] = listener; },
      open: async options => { loginURLs.push(options.url); browserOpenWaiters.shift()?.(options.url); }, close: async () => {} },
    DOMPurify: { sanitize: value => value }, activeLineIndex: () => 0, loopEnd: () => 1,
    parseWebsiteCatalog: () => ({ songs: [], hasMore: false }),
  };
  element('practice').hidden = true;
  element('favorites').setAttribute('aria-pressed', 'false');
  vm.createContext(context); vm.runInContext(compiled, context);
  return { ...context.fixture, element, listeners, calls, loginURLs, transport, waitForBrowserOpen };
}

function deferred() {
  let resolve;
  const promise = new Promise(accept => { resolve = accept; });
  return { promise, resolve };
}
const response = (data, status = 200) => ({ data, status });
const song = { songUid: 'original', title: '測試歌曲', artist: 'fixture', performer: '', videoId: null };
const detail = { song, lyrics: [] };
const songs = Array.from({ length: 30 }, () => song);
const flush = () => new Promise(resolve => setImmediate(resolve));
const callback = (fixture, state) => fixture.listeners.appUrlOpen({ url: `learnmore://auth?state=${state}&code=fixture` });
const state = fixture => new URL(fixture.loginURLs.at(-1)).searchParams.get('state');
async function favoritesLogin(fixture) {
  const opened = fixture.waitForBrowserOpen();
  fixture.element('favorites').onclick();
  return new URL(await opened).searchParams.get('state');
}
function setSearch(fixture) {
  fixture.setQuery('search'); fixture.element('query').value = 'search';
}

test('收藏登入 helper 等待延遲 digest 後的 Browser.open，而非 event-loop flush', { timeout: 5000 }, async () => {
  const held = deferred(), fixture = setup({ digest: () => held.promise });
  setSearch(fixture);
  let completed = false;
  const opening = favoritesLogin(fixture).then(value => { completed = true; return value; });
  await flush();
  assert.equal(completed, false);
  assert.equal(fixture.loginURLs.length, 0);
  held.resolve(new Uint8Array(32).buffer);
  const loginState = await opening;
  assert.equal(fixture.loginURLs.length, 1);
  assert.equal(loginState, state(fixture));
  fixture.transport.request = async options => options.method === 'POST'
    ? response({ token: 'current', user: { name: 'fixture' } }) : response(songs);
  await callback(fixture, loginState); await flush();
  assert.equal(fixture.element('favorites').attributes['aria-pressed'], 'true');
  assert.equal(new URL(fixture.calls.at(-1).url).searchParams.get('favorites'), 'true');
});

for (const returnBeforeResponse of [true, false]) {
  test(`分頁晚成功在${returnBeforeResponse ? '返回後' : '返回前'}恢復載入更多且不跳頁`, async () => {
    const fixture = setup(); setSearch(fixture);
    fixture.transport.request = async () => response(songs);
    await fixture.loadSongs();
    const held = deferred();
    fixture.transport.request = options => options.url.includes('songs?') ? held.promise : Promise.resolve(response(detail));
    fixture.element('more').onclick(); await fixture.openSong('original');
    if (returnBeforeResponse) fixture.element('back').onclick();
    held.resolve(response(songs)); await flush();
    if (!returnBeforeResponse) fixture.element('back').onclick();
    assert.equal(fixture.element('more').hidden, false);
    assert.equal(fixture.element('library').hidden, false);
    assert.equal(fixture.element('songs').children.length, 60);
    assert.equal(fixture.element('songs').attributes['aria-busy'], 'false');
    fixture.transport.request = async () => response([]);
    fixture.element('more').onclick(); await flush();
    assert.equal(new URL(fixture.calls.at(-1).url).searchParams.get('page'), '3');
  });
}

test('返回後晚分頁失敗顯示清單重試並恢復載入更多', async () => {
  const fixture = setup(); setSearch(fixture);
  fixture.transport.request = async () => response(songs); await fixture.loadSongs();
  const held = deferred();
  fixture.transport.request = options => options.url.includes('songs?') ? held.promise : Promise.resolve(response(detail));
  fixture.element('more').onclick(); await fixture.openSong('original'); fixture.element('back').onclick();
  held.resolve(response({}, 503)); await flush();
  assert.equal(fixture.element('retry').hidden, false);
  fixture.transport.request = async () => response(songs);
  fixture.element('retry').onclick(); await flush();
  assert.equal(new URL(fixture.calls.at(-1).url).searchParams.get('page'), '1');
  assert.equal(fixture.element('more').hidden, false);
});

test('歌曲畫面晚分頁失敗不覆蓋歌曲重試，返回後重載清單', async () => {
  const fixture = setup(); setSearch(fixture); const held = deferred();
  fixture.transport.request = () => held.promise; const listing = fixture.loadSongs(true);
  fixture.transport.request = async () => response({}, 503); await fixture.openSong('original');
  const notice = fixture.element('notice').textContent;
  held.resolve(response({}, 503)); await listing;
  assert.equal(fixture.element('notice').textContent, notice);
  assert.equal(fixture.element('retry').hidden, false);
  fixture.element('retry').onclick(); await flush();
  assert.equal(new URL(fixture.calls.at(-1).url).pathname.endsWith('/songs/original'), true);
  fixture.transport.request = async () => response(songs);
  fixture.element('back').onclick(); await flush();
  assert.equal(new URL(fixture.calls.at(-1).url).searchParams.get('page'), '1');
  assert.equal(fixture.element('more').hidden, false);
});

test('未登入點收藏後成功登入會清除搜尋並載入收藏', async () => {
  const fixture = setup(); setSearch(fixture);
  fixture.transport.request = async options => options.method === 'POST'
    ? response({ token: 'current', user: { name: 'fixture' } }) : response(songs);
  await callback(fixture, await favoritesLogin(fixture)); await flush();
  assert.equal(fixture.element('favorites').attributes['aria-pressed'], 'true');
  assert.equal(fixture.element('all').attributes['aria-pressed'], 'false');
  assert.equal(fixture.element('query').value, '');
  assert.equal(fixture.element('catalog-title').textContent, '我的收藏');
  const request = new URL(fixture.calls.at(-1).url);
  assert.equal(request.searchParams.get('favorites'), 'true');
  assert.equal(request.searchParams.get('q'), '');
  assert.equal(fixture.calls.filter(options => options.method === 'GET').length, 1);
});

for (const invalidate of ['cancel', 'new-login', 'logout']) {
  test(`${invalidate} 失效收藏登入意圖，晚交換及新登入不會復活舊導覽`, async () => {
    const fixture = setup(); setSearch(fixture);
    const oldState = await favoritesLogin(fixture), held = deferred();
    fixture.transport.request = () => held.promise; const exchange = callback(fixture, oldState);
    if (invalidate === 'cancel') fixture.listeners.browserFinished();
    if (invalidate === 'logout') { fixture.element('practice').hidden = false; fixture.signOut(); }
    await fixture.login(); const newState = state(fixture);
    held.resolve(response({ token: 'old', user: { name: 'old' } })); await exchange;
    fixture.transport.request = async () => response({ token: 'new', user: { name: 'new' } });
    await callback(fixture, newState); await flush();
    assert.equal(fixture.getToken(), 'new');
    assert.equal(fixture.element('favorites').attributes['aria-pressed'], 'false');
    assert.equal(fixture.calls.filter(options => options.method === 'GET').length, 0);
  });
}

test('較新的收藏登入只執行自己的導覽，舊交換不能觸發清單', async () => {
  const fixture = setup(), oldState = await favoritesLogin(fixture), held = deferred();
  fixture.transport.request = () => held.promise; const exchange = callback(fixture, oldState);
  const newState = await favoritesLogin(fixture);
  fixture.transport.request = async options => options.method === 'POST'
    ? response({ token: 'new', user: { name: 'new' } }) : response(songs);
  await callback(fixture, newState); await flush();
  held.resolve(response({ token: 'old', user: { name: 'old' } })); await exchange;
  assert.equal(fixture.getToken(), 'new');
  assert.equal(fixture.element('favorites').attributes['aria-pressed'], 'true');
  assert.equal(fixture.calls.filter(options => options.method === 'GET').length, 1);
});
