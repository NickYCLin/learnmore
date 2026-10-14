import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createDiagnosticController } from '../src/diagnostic-controller.mjs';

function deferred() {
  let resolve;
  const promise = new Promise(accept => { resolve = accept; });
  return { promise, resolve };
}
function setup() {
  const calls = [], states = [];
  let revision = 0, listener, removed = false;
  const state = (status, seconds = 0) => ({ revision: ++revision, status, seconds, duration: 180, rate: status === 'playing' ? 1 : 0, error: '' });
  const bridge = {
    async addListener(name, receive) { assert.equal(name, 'stateChange'); listener = receive; return { remove: async () => { removed = true; } }; },
    async start(options) { calls.push(['start', options]); return state('playing'); },
    async pause() { calls.push(['pause']); return state('paused'); },
    async seek(options) { calls.push(['seek', options]); return state('paused', options.seconds); },
    async stop() { calls.push(['stop']); return state('stopped'); },
    async getState() { calls.push(['getState']); return state('paused', 12); },
  };
  const controller = createDiagnosticController({ bridge, suspendYouTube: () => calls.push(['destroyYouTube']), render: value => states.push(value) });
  return { bridge, controller, calls, states, state, emit: value => listener(value), removed: () => removed };
}

test('診斷先停止 YouTube，只傳固定測試 ID；pause/seek 以原生回傳為準', async () => {
  const fixture = setup();
  await fixture.controller.start(); await fixture.controller.pause(); await fixture.controller.seek(7);
  assert.deepEqual(fixture.calls, [['destroyYouTube'], ['start', { testId: 'tone-220-180s' }], ['pause'], ['seek', { seconds: 7 }]]);
  assert.equal(fixture.states.at(-1).seconds, 7);
  assert.equal(fixture.states.at(-1).rate, 0);
  await fixture.controller.dispose();
});

test('切歌在 start 尚未開始時失效舊意圖，不復活診斷音訊', async () => {
  const fixture = setup();
  const started = fixture.controller.start(); const stopped = fixture.controller.stop();
  await Promise.all([started, stopped]);
  assert.deepEqual(fixture.calls, [['destroyYouTube'], ['stop']]);
});

test('已送出的延遲 start 必須先完成 stop，切歌才可建立 YouTube', async () => {
  const fixture = setup(), entered = deferred(), response = deferred();
  fixture.bridge.start = async () => { entered.resolve(); return response.promise; };
  const start = fixture.controller.start(); await entered.promise;
  const stop = fixture.controller.stop().then(() => fixture.calls.push(['createYouTube']));
  assert.equal(fixture.calls.some(call => call[0] === 'createYouTube'), false);
  response.resolve(fixture.state('playing')); await Promise.all([start, stop]);
  assert.deepEqual(fixture.calls, [['destroyYouTube'], ['stop'], ['createYouTube']]);
  assert.equal(fixture.states.at(-1).status, 'stopped');
});

test('晚查詢不能覆蓋較新的 interruption/route pause 事件；恢復前景查詢原生', async () => {
  const fixture = setup(), entered = deferred(), response = deferred();
  await fixture.controller.start();
  const old = fixture.state('playing', 4);
  fixture.bridge.getState = async () => { entered.resolve(); return response.promise; };
  const refresh = fixture.controller.refresh(); await entered.promise;
  fixture.emit(fixture.state('interrupted', 6));
  fixture.emit(fixture.state('paused', 6));
  response.resolve(old); await refresh;
  assert.equal(fixture.states.at(-1).status, 'paused');
  assert.equal(fixture.states.at(-1).seconds, 6);
});

test('dispose 失效待播放、停止原生並移除監聽，晚事件不更新 UI', async () => {
  const fixture = setup();
  const start = fixture.controller.start(); await fixture.controller.dispose(); await start;
  const count = fixture.states.length;
  fixture.emit(fixture.state('playing'));
  assert.equal(fixture.states.length, count);
  assert.equal(fixture.removed(), true);
  assert.deepEqual(fixture.calls, [['destroyYouTube'], ['stop']]);
});

test('bridge 失敗會傳回呼叫端，後续 stop 不受失敗佇列阻塞', async () => {
  const fixture = setup();
  fixture.bridge.start = async () => { throw new Error('AVAudioSession unavailable'); };
  await assert.rejects(fixture.controller.start(), /AVAudioSession/);
  await fixture.controller.stop();
  assert.equal(fixture.states.at(-1).status, 'stopped');
});

test('iOS source wiring：target、兩個 bridge 入口、背景模式與封閉測試輸入', () => {
  const read = path => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
  const source = read('ios/App/App/DiagnosticAudioPlugin.swift');
  const require = createRequire(import.meta.url), project = require('xcode').project(new URL('../ios/App/App.xcodeproj/project.pbxproj', import.meta.url).pathname);
  project.parseSync();
  const sources = Object.values(project.hash.project.objects.PBXSourcesBuildPhase).filter(value => typeof value === 'object').flatMap(value => value.files ?? []);
  assert.ok(sources.some(value => value.comment === 'DiagnosticAudioPlugin.swift in Sources'));
  assert.match(read('ios/App/App/SceneDelegate.swift'), /rootViewController = LearnMoreBridgeViewController\(\)/);
  assert.match(read('ios/App/App/Base.lproj/Main.storyboard'), /customClass="LearnMoreBridgeViewController"/);
  assert.match(read('ios/App/App/Info.plist'), /UIBackgroundModes<\/key>\s*<array><string>audio<\/string>/);
  assert.match(source, /registerPluginInstance\(DiagnosticAudioPlugin\(\)\)/);
  assert.match(source, /call.getString\("testId"\) == "tone-220-180s"/);
  assert.doesNotMatch(source, /URL\(string:|URLSession|AVAudioEngine|setCategory\(\.record/);
  assert.match(source, /resumeAfterInterruption && desiredPlaying && options.contains\(\.shouldResume\)/);
  assert.match(source, /desiredPlaying = false; resumeAfterInterruption = false/);
  assert.match(source, /oldDeviceUnavailable/);
  assert.match(source, /guard self\?\.player != nil/);
  assert.match(source, /guard let value = note.userInfo/);
  assert.match(source, /notifyOthersOnDeactivation/);
  assert.match(source, /subdirectory: "public"/);
});
