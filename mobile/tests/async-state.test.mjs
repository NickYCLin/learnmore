import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { webcrypto } from 'node:crypto';
import vm from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../src/main.ts', import.meta.url), 'utf8')
  .replace(/^import .*;\n/gm, '')
  .replace(/void loadSongs\(\);\s*$/, '') + `
globalThis.fixture = {
  login, openSong, showGroups, returnToLibrary, signOut, api, loadSongs,
  getDialogIntent: () => groupDialogSerial,
  getToken: () => token, setToken: (value: string) => { token = value; },
  setDetail: (value: Detail) => { detail = value; }
};`;
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.None },
}).outputText;

class Element {
  hidden = false;
  open = false;
  disabled = false;
  value = '';
  textContent = '';
  children = [];
  attributes = {};
  controls = [];
  classList = { toggle: () => false };
  setAttribute(name, value) { this.attributes[name] = value; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; this.textContent = ''; }
  querySelectorAll() { return this.controls; }
  close() { this.open = false; queueMicrotask(() => this.onclose?.()); }
  showModal() { this.open = true; }
  reset() { this.controls.forEach(control => { control.value = ''; }); }
  remove() {}
}

function setup({ digest = (...args) => webcrypto.subtle.digest(...args), diagnosticStop = () => undefined } = {}) {
  const elements = new Map(), listeners = {}, calls = [], loginURLs = [], browserCalls = [];
  const element = id => {
    if (!elements.has(id)) elements.set(id, new Element());
    return elements.get(id);
  };
  const transport = { request: async options => { throw new Error(`Unexpected request: ${options.method} ${options.url}`); } };
  const context = {
    console, URL, TextEncoder, AbortSignal, setInterval: () => 0, clearInterval: () => {}, clearTimeout: () => {},
    crypto: { getRandomValues: values => webcrypto.getRandomValues(values), subtle: { digest } },
    btoa: value => Buffer.from(value, 'binary').toString('base64'),
    document: {
      getElementById: element, createElement: () => new Element(), querySelector: element,
      querySelectorAll: () => [], head: new Element(),
    },
    window: {
      addEventListener: () => {}, removeEventListener: () => {}, scrollTo: () => {},
      setInterval: () => 0, setTimeout: () => 0, location: { origin: 'http://fixture.invalid' },
    },
    diagnosticAudio: { stop: diagnosticStop }, initializeDiagnosticAudio() {},
    Capacitor: { isNativePlatform: () => true },
    CapacitorHttp: {
      request: options => { calls.push(options); return transport.request(options); },
      get: options => { calls.push(options); return transport.request(options); },
    },
    App: { addListener: async (name, listener) => { listeners[name] = listener; } },
    Browser: {
      addListener: async (name, listener) => { listeners[name] = listener; },
      open: async options => { loginURLs.push(options.url); browserCalls.push('open'); },
      close: async () => { browserCalls.push('close'); },
    },
    DOMPurify: { sanitize: value => value },
    activeLineIndex: () => 0, loopEnd: () => 1,
    parseWebsiteCatalog: () => ({ songs: [], hasMore: false }),
  };
  element('practice').hidden = true;
  element('retry').hidden = true;
  element('new-group').controls = [element('group-name'), element('group-submit')];
  vm.createContext(context);
  vm.runInContext(compiled, context);
  return { ...context.fixture, element, listeners, calls, loginURLs, browserCalls, transport };
}

function deferred() {
  let resolve, reject;
  const promise = new Promise((accept, fail) => { resolve = accept; reject = fail; });
  return { promise, resolve, reject };
}
const response = (data, status = 200) => ({ status, data });

test('開啟歌曲必須等待診斷原生 stop，不能同時播放', async () => {
  const stopping = deferred(), fixture = setup({ diagnosticStop: () => stopping.promise });
  fixture.transport.request = () => response(detail);
  const opening = fixture.openSong('original');
  assert.equal(fixture.calls.length, 0);
  stopping.resolve(); await opening;
  assert.equal(fixture.calls.length, 1);
  assert.equal(fixture.element('practice').hidden, false);
});

test('等待診斷停止期間返回，不再復活歌曲請求', async () => {
  const stopping = deferred(), fixture = setup({ diagnosticStop: () => stopping.promise });
  const opening = fixture.openSong('original');
  fixture.returnToLibrary(); stopping.resolve(); await opening;
  assert.equal(fixture.calls.length, 0);
  assert.equal(fixture.element('practice').hidden, true);
});

test('診斷 stop 失敗不開啟 YouTube，保留原歌曲重試', async () => {
  const fixture = setup({ diagnosticStop: async () => { throw new Error('診斷停止失敗'); } });
  await fixture.openSong('original');
  assert.equal(fixture.calls.length, 0);
  assert.equal(fixture.element('retry').hidden, false);
  assert.ok(fixture.element('notice').textContent.length > 0);
});
const session = token => response({ token, user: { name: token } });
const song = { songUid: 'original', title: '原創練習例句', artist: '測試資料', performer: '', videoId: null };
const detail = { song, lyrics: [{ id: 1, time: 0, japanese: 'こんにちは', ruby: '', roman: 'konnichiwa', chinese: '你好' }] };
const group = { id: 1, name: '練習群組', included: true };
const submit = fixture => fixture.element('new-group').onsubmit({ preventDefault() {} });
const posts = fixture => fixture.calls.filter(call => call.method === 'POST' && new URL(call.url).pathname.endsWith('/groups'));
const callback = (fixture, state, code) => fixture.listeners.appUrlOpen({ url: `learnmore://auth?state=${state}&code=${code}` });
async function startLogin(fixture) {
  await fixture.login();
  return new URL(fixture.loginURLs.at(-1)).searchParams.get('state');
}
async function openGroups(fixture) {
  fixture.setToken('current-token'); fixture.setDetail(detail); fixture.element('practice').hidden = false;
  fixture.transport.request = async () => response([{ ...group }]);
  await fixture.showGroups();
  fixture.element('group-name').value = '新群組';
}
function assertIdle(fixture) {
  assert.equal(fixture.element('new-group').attributes['aria-busy'], 'false');
  assert.equal(fixture.element('group-submit').disabled, false);
  assert.equal(fixture.element('group-name').disabled, false);
}

test('較早的登入交換成功不會覆蓋新登入', async () => {
  const fixture = setup(), older = deferred();
  fixture.transport.request = options => options.data.code === 'old' ? older.promise : session('new-token');
  const oldExchange = callback(fixture, await startLogin(fixture), 'old');
  await callback(fixture, await startLogin(fixture), 'new');
  older.resolve(session('old-token')); await oldExchange;
  assert.equal(fixture.getToken(), 'new-token');
  assert.equal(fixture.element('notice').textContent, '已登入，new-token');
  assert.equal(fixture.browserCalls.filter(call => call === 'close').length, 1);
});

test('新登入開始後即使尚未交換，舊成功也不會登入或關閉瀏覽器', async () => {
  const fixture = setup(), older = deferred(); fixture.transport.request = () => older.promise;
  const oldExchange = callback(fixture, await startLogin(fixture), 'old');
  const newState = await startLogin(fixture);
  older.resolve(session('old-token')); await oldExchange;
  assert.equal(fixture.getToken(), ''); assert.equal(fixture.browserCalls.includes('close'), false);
  fixture.transport.request = () => session('new-token');
  await callback(fixture, newState, 'new'); assert.equal(fixture.getToken(), 'new-token');
});

test('較早的 PKCE 計算不會覆蓋新的登入 attempt', async () => {
  const older = deferred(); let digests = 0;
  const fixture = setup({ digest: (...args) => ++digests === 1 ? older.promise : webcrypto.subtle.digest(...args) });
  const oldLogin = fixture.login(); const newState = await startLogin(fixture);
  older.resolve(new ArrayBuffer(32)); await oldLogin;
  assert.equal(fixture.loginURLs.length, 1);
  fixture.transport.request = () => session('new-token');
  await callback(fixture, newState, 'new'); assert.equal(fixture.getToken(), 'new-token');
});

test('取消瀏覽器後忽略進行中的交換成功及重複 callback', async () => {
  const fixture = setup(), held = deferred(); fixture.transport.request = () => held.promise;
  const state = await startLogin(fixture), exchange = callback(fixture, state, 'old');
  fixture.listeners.browserFinished(); held.resolve(session('cancelled')); await exchange;
  await callback(fixture, state, 'old');
  assert.equal(fixture.getToken(), ''); assert.equal(fixture.calls.length, 1);
  assert.equal(fixture.browserCalls.includes('close'), false);
});

test('PKCE 計算中取消不會再開啟登入瀏覽器', async () => {
  const held = deferred(), fixture = setup({ digest: () => held.promise });
  const login = fixture.login(); fixture.listeners.browserFinished();
  held.resolve(new ArrayBuffer(32)); await login;
  assert.equal(fixture.loginURLs.length, 0);
});

test('登出後舊登入交換不能恢復登入', async () => {
  const fixture = setup(), held = deferred(); fixture.transport.request = () => held.promise;
  const exchange = callback(fixture, await startLogin(fixture), 'old');
  fixture.element('practice').hidden = false; fixture.signOut();
  held.resolve(session('old-token')); await exchange;
  assert.equal(fixture.getToken(), ''); assert.equal(fixture.element('account').textContent, '登入');
});

test('登出開始即失效舊交換，晚登出回應不會取消新登入', async () => {
  const fixture = setup(), exchange = deferred(), logout = deferred(); fixture.setToken('existing');
  fixture.transport.request = options => options.method === 'DELETE' ? logout.promise : exchange.promise;
  const oldExchange = callback(fixture, await startLogin(fixture), 'old');
  const signingOut = fixture.element('account').onclick();
  exchange.resolve(session('old-token')); await oldExchange;
  assert.equal(fixture.getToken(), 'existing');
  const newState = await startLogin(fixture);
  logout.resolve(response(null, 204)); await signingOut;
  fixture.transport.request = () => session('new-token');
  await callback(fixture, newState, 'new'); assert.equal(fixture.getToken(), 'new-token');
});

test('已登入帳號的舊交換 401 不會取消新 attempt', async () => {
  const fixture = setup(), older = deferred(); fixture.setToken('existing');
  fixture.transport.request = () => older.promise;
  const oldExchange = callback(fixture, await startLogin(fixture), 'old');
  const newState = await startLogin(fixture);
  older.resolve(response({}, 401)); await oldExchange;
  assert.equal(fixture.getToken(), 'existing');
  fixture.transport.request = () => session('new-token');
  await callback(fixture, newState, 'new'); assert.equal(fixture.getToken(), 'new-token');
});

test('取消後的交換錯誤不會覆蓋目前訊息', async () => {
  const fixture = setup(), held = deferred(); fixture.transport.request = () => held.promise;
  const exchange = callback(fixture, await startLogin(fixture), 'old');
  fixture.listeners.browserFinished(); fixture.element('notice').textContent = '目前畫面';
  held.reject(new Error('late failure')); await exchange;
  assert.equal(fixture.element('notice').textContent, '目前畫面');
});

test('錯誤 state 與重複 callback 不會多送交換', async () => {
  const fixture = setup(); fixture.transport.request = () => session('current-token');
  const state = await startLogin(fixture);
  await callback(fixture, 'wrong-state', 'wrong'); assert.equal(fixture.calls.length, 0);
  await callback(fixture, state, 'valid'); await callback(fixture, state, 'valid');
  assert.equal(fixture.calls.length, 1); assert.equal(fixture.getToken(), 'current-token');
});

test('建立群組連按只送一次 POST，完成 refresh 才解除 busy', async () => {
  const fixture = setup(); await openGroups(fixture);
  const creation = deferred(), refresh = deferred();
  fixture.transport.request = options => options.method === 'POST' ? creation.promise : refresh.promise;
  const submitting = submit(fixture), duplicate = submit(fixture);
  assert.equal(posts(fixture).length, 1);
  assert.equal(fixture.element('group-submit').disabled, true);
  assert.equal(fixture.element('group-name').disabled, true);
  assert.equal(fixture.element('new-group').attributes['aria-busy'], 'true');
  creation.resolve(response({ id: 2 })); await Promise.resolve(); await Promise.resolve();
  assert.equal(fixture.element('group-submit').disabled, true);
  refresh.resolve(response([{ ...group, name: '新群組' }])); await Promise.all([submitting, duplicate]);
  assertIdle(fixture); assert.equal(fixture.element('group-name').value, '');
  assert.equal(fixture.element('group-dialog').open, true);
});

test('建立失敗或 refresh 失敗都會解除 busy', async () => {
  for (const failure of ['create', 'refresh']) {
    const fixture = setup(); await openGroups(fixture);
    fixture.transport.request = async options => response({}, options.method === 'POST' && failure === 'refresh' ? 200 : 503);
    await submit(fixture); assertIdle(fixture);
    assert.match(fixture.element('notice').textContent, /暫時/);
  }
});

test('關閉後晚 create 不會重開 dialog 或 refresh', async () => {
  const fixture = setup(); await openGroups(fixture);
  const held = deferred(); fixture.transport.request = () => held.promise;
  const submitting = submit(fixture); fixture.element('close-groups').onclick();
  held.resolve(response({ id: 2 })); await submitting;
  assert.equal(fixture.element('group-dialog').open, false); assert.equal(fixture.calls.length, 2);
  assert.equal(fixture.element('group-name').value, '新群組'); assertIdle(fixture);
});

test('cancel 與返回均阻止晚 showGroups 重開 dialog', async () => {
  for (const close of ['close', 'cancel', 'back']) {
    const fixture = setup(); await openGroups(fixture);
    const held = deferred(); fixture.transport.request = () => held.promise;
    const loading = fixture.showGroups();
    if (close === 'close') fixture.element('close-groups').onclick();
    else if (close === 'cancel') fixture.element('group-dialog').oncancel();
    else fixture.returnToLibrary();
    held.resolve(response([{ ...group, name: '過期群組' }])); await loading;
    assert.equal(fixture.element('group-dialog').open, false);
    assert.notEqual(fixture.element('groups').children[0]?.textContent, '過期群組');
  }
});

test('原生 close 事件失效目前 dialog 請求', async () => {
  const fixture = setup(); await openGroups(fixture);
  const held = deferred(); fixture.transport.request = () => held.promise;
  const submitting = submit(fixture);
  fixture.element('group-dialog').close(); await Promise.resolve();
  held.resolve(response({ id: 2 })); await submitting;
  assert.equal(fixture.element('group-dialog').open, false); assertIdle(fixture);
});

test('同曲重開後舊 create 的成功、錯誤與 finally 不影響新 dialog', async () => {
  for (const status of [200, 503]) {
    const fixture = setup(); await openGroups(fixture);
    const older = deferred(), newer = deferred();
    fixture.transport.request = () => older.promise;
    const oldSubmit = submit(fixture); fixture.element('close-groups').onclick();
    fixture.transport.request = async () => response([{ ...group, name: '目前群組' }]);
    await fixture.showGroups(); fixture.element('group-name').value = '目前名稱';
    fixture.transport.request = options => options.method === 'POST' ? newer.promise : response([{ ...group, name: '目前群組' }]);
    const newSubmit = submit(fixture);
    older.resolve(response({ id: 2 }, status)); await oldSubmit;
    assert.equal(fixture.element('group-dialog').open, true);
    assert.equal(fixture.element('group-name').value, '目前名稱');
    assert.equal(fixture.element('group-submit').disabled, true);
    assert.equal(fixture.element('new-group').attributes['aria-busy'], 'true');
    assert.equal(fixture.element('groups').children[0].textContent, '目前群組');
    newer.resolve(response({ id: 3 })); await newSubmit; assertIdle(fixture);
  }
});

test('同曲重開後舊 showGroups 不覆蓋新群組', async () => {
  const fixture = setup(); await openGroups(fixture);
  const older = deferred(); fixture.transport.request = () => older.promise;
  const oldLoad = fixture.showGroups(); fixture.element('close-groups').onclick();
  fixture.transport.request = async () => response([{ ...group, name: '目前群組' }]);
  await fixture.showGroups();
  older.resolve(response([{ ...group, name: '過期群組' }])); await oldLoad;
  assert.equal(fixture.element('groups').children[0].textContent, '目前群組');
  assert.equal(fixture.element('group-dialog').open, true);
});

test('同一 dialog 只套用最後一次 refresh 的回應', async () => {
  const fixture = setup(); await openGroups(fixture);
  const older = deferred(); fixture.transport.request = () => older.promise;
  const intent = fixture.getDialogIntent(), oldLoad = fixture.showGroups(intent);
  fixture.transport.request = async () => response([{ ...group, name: '最新群組' }]);
  await fixture.showGroups(intent); older.resolve(response([{ ...group, name: '較早群組' }])); await oldLoad;
  assert.equal(fixture.element('groups').children[0].textContent, '最新群組');
});

test('晚清單回應不會覆蓋歌曲失敗的重試與訊息', async () => {
  const fixture = setup(), held = deferred();
  fixture.transport.request = () => held.promise;
  const listing = fixture.loadSongs();
  fixture.transport.request = async () => response({}, 503);
  await fixture.openSong('original');
  held.resolve(response('<html></html>')); await listing;
  assert.equal(fixture.element('retry').hidden, false);
  assert.match(fixture.element('notice').textContent, /暫時/);
});

test('歌曲 503 顯示重試，重試原歌曲後正常顯示歌詞', async () => {
  const fixture = setup(); fixture.transport.request = async () => response({}, 503);
  await fixture.openSong('original'); assert.equal(fixture.element('retry').hidden, false);
  assert.match(fixture.element('notice').textContent, /暫時/);
  const retry = deferred(); fixture.transport.request = () => retry.promise;
  fixture.element('retry').onclick(); assert.equal(fixture.element('retry').hidden, true);
  assert.equal(new URL(fixture.calls.at(-1).url).pathname.endsWith('/songs/original'), true);
  retry.resolve(response(detail)); await new Promise(resolve => setImmediate(resolve));
  assert.equal(fixture.element('practice').hidden, false);
  assert.equal(fixture.element('lyrics').children.length, 1);
  assert.equal(fixture.element('retry').hidden, true);
});

test('返回後晚歌曲錯誤不會留下過期重試', async () => {
  const fixture = setup(), held = deferred(); fixture.transport.request = () => held.promise;
  const loading = fixture.openSong('original'); fixture.returnToLibrary();
  held.resolve(response({}, 503)); await loading;
  assert.equal(fixture.element('retry').hidden, true); assert.equal(fixture.element('notice').textContent, '');
});

test('切歌後晚歌曲錯誤不覆蓋新歌曲或顯示過期重試', async () => {
  const fixture = setup(), older = deferred(); fixture.transport.request = () => older.promise;
  const loading = fixture.openSong('original');
  fixture.transport.request = async () => response({ ...detail, song: { ...song, songUid: 'new', title: '新歌曲' } });
  await fixture.openSong('new'); older.resolve(response({}, 503)); await loading;
  assert.equal(fixture.element('song-title').textContent, '新歌曲'); assert.equal(fixture.element('retry').hidden, true);
});

test('重試進行中返回不會被晚成功帶回歌曲', async () => {
  const fixture = setup(); fixture.transport.request = async () => response({}, 503);
  await fixture.openSong('original');
  const held = deferred(); fixture.transport.request = () => held.promise;
  fixture.element('retry').onclick(); fixture.returnToLibrary();
  held.resolve(response(detail)); await new Promise(resolve => setImmediate(resolve));
  assert.equal(fixture.element('practice').hidden, true); assert.equal(fixture.element('retry').hidden, true);
});

test('無影片歌曲保留歌詞', async () => {
  const fixture = setup(); fixture.transport.request = async () => response(detail);
  await fixture.openSong('original'); assert.equal(fixture.element('lyrics').children.length, 1);
  assert.equal(fixture.element('.player-wrap').hidden, true);
});

test('返回失效尚未完成的歌曲 detail', async () => {
  const fixture = setup(), held = deferred(); fixture.transport.request = () => held.promise;
  const loading = fixture.openSong('original'); fixture.returnToLibrary();
  held.resolve(response(detail)); await loading; assert.equal(fixture.element('practice').hidden, true);
});

test('舊帳號的 401 不會清除新 token', async () => {
  const fixture = setup(), held = deferred(); fixture.setToken('old-token');
  fixture.transport.request = () => held.promise;
  const loading = fixture.api('groups').catch(error => error);
  fixture.setToken('new-token'); held.resolve(response({}, 401)); await loading;
  assert.equal(fixture.getToken(), 'new-token');
});

test('目前帳號仍可移除收藏，401 仍會清空私人狀態', async () => {
  const fixture = setup(); await openGroups(fixture);
  const button = fixture.element('groups').children[0];
  fixture.transport.request = async options => {
    assert.equal(options.method, 'PUT'); assert.equal(options.headers.Authorization, 'Bearer current-token');
    assert.equal(options.data.included, false); return response(null, 204);
  };
  await button.onclick(); assert.equal(button.attributes['aria-pressed'], 'false');
  fixture.transport.request = async () => response({}, 401);
  await button.onclick(); assert.equal(fixture.getToken(), '');
  assert.equal(fixture.element('group-dialog').open, false);
  assert.equal(fixture.element('groups').children.length, 0);
  assert.equal(fixture.element('group-name').value, ''); assertIdle(fixture);
});
