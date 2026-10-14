import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { fileURLToPath } from 'node:url';

// Use the real HTTP/JSON path, including Windows PowerShell 5.1 array behavior.
const executable = process.env.LEARNMORE_POWERSHELL || 'powershell.exe';
const script = fileURLToPath(new URL('./verify_learnmore_mobile_backend.ps1', import.meta.url));
const song = { songUid: 'song-a', title: 'Test', artist: '', performer: '', videoId: null };
const line = { id: 1, time: 0, japanese: 'Test', ruby: '', roman: '', chinese: '' };
const prefix = '/LearnMore/api/mobile/v1/';
let scenario;
const requested = [];
const server = createServer((request, response) => {
  requested.push(request.url);
  const routes = {
    status: { version: 1 },
    songs: [song, { ...song, songUid: 'song-b' }],
    'songs/song-a': { song, lyrics: [line] },
  };
  const path = request.url.slice(prefix.length);
  let status = ['groups', 'songs?favorites=true'].includes(path) ? 401 : 200;
  let body = routes[path] ?? {};
  let contentType = 'application/json';
  if (!request.url.startsWith(prefix) || !(path in routes) && status !== 401) status = 404;
  ({ status, body, contentType } = scenario.change(path, { status, body, contentType }));
  response.writeHead(status, { 'Content-Type': contentType, ...(status === 302 ? { Location: '/login' } : {}) });
  response.end(JSON.stringify(body));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const base = `http://127.0.0.1:${server.address().port}/LearnMore`;
const changeAt = (target, update) => (path, response) => path === target ? { ...response, ...update } : response;
const cases = [
  { name: 'multiple songs select a single ID', change: (_, r) => r },
  { name: 'singleton array', change: changeAt('songs', { body: [song] }) },
  { name: 'empty lyrics', change: changeAt('songs/song-a', { body: { song, lyrics: [] } }) },
  { name: 'empty songs', change: changeAt('songs', { body: [] }), error: 'no songs' },
  { name: 'object instead of array', change: changeAt('songs', { body: song }), error: 'expected JSON array' },
  { name: 'PascalCase detail', change: changeAt('songs/song-a', { body: { Song: song, Lyrics: [line] } }), error: 'missing exact JSON field song' },
  { name: 'PascalCase lyric', change: changeAt('songs/song-a', { body: { song, lyrics: [{ ...line, id: undefined, Id: 1 }] } }), error: 'missing exact JSON field id' },
  { name: 'string lyric time', change: changeAt('songs/song-a', { body: { song, lyrics: [{ ...line, time: '0' }] } }), error: 'invalid lyric id/time' },
  { name: 'wrong song', change: changeAt('songs/song-a', { body: { song: { ...song, songUid: 'song-b' }, lyrics: [] } }), error: 'invalid song detail' },
  { name: 'status HTTP error includes path', change: changeAt('status', { status: 503 }), error: 'api/mobile/v1/status: HTTP 503' },
  { name: 'detail HTTP error includes path', change: changeAt('songs/song-a', { status: 400 }), error: 'api/mobile/v1/songs/song-a: HTTP 400' },
  { name: 'groups require authentication', change: changeAt('groups', { status: 200 }), error: 'api/mobile/v1/groups: unexpected HTTP 200' },
  { name: 'favorites require authentication', change: changeAt('songs?favorites=true', { status: 200 }), error: 'songs?favorites=true: unexpected HTTP 200' },
  { name: 'reject login redirect', change: changeAt('status', { status: 302 }), error: 'api/mobile/v1/status:' },
  { name: 'reject HTML', change: changeAt('status', { contentType: 'text/html' }), error: 'response is not JSON' },
];
try {
  for (scenario of cases) {
    requested.length = 0;
    const result = await new Promise((resolve, reject) => {
      const child = spawn(executable, ['-NoProfile', '-NonInteractive', '-File', script, '-BaseUrl', base, '-TimeoutSeconds', '3']);
      let output = '';
      const timeout = setTimeout(() => child.kill(), 20_000);
      child.stdout.on('data', data => { output += data; });
      child.stderr.on('data', data => { output += data; });
      child.on('error', error => { clearTimeout(timeout); reject(error); });
      child.on('close', code => { clearTimeout(timeout); resolve({ code, output }); });
    });
    if (scenario.error) {
      assert.notEqual(result.code, 0, scenario.name);
      // Strip ANSI formatting and whitespace that PowerShell adds to long errors.
      const clean = result.output.replace(/\x1b\[[0-9;]*m/g, '').replace(/\s+/g, ' ');
      assert.ok(clean.includes(scenario.error), `${scenario.name}: ${result.output}`);
    } else {
      assert.equal(result.code, 0, `${scenario.name}: ${result.output}`);
      assert.ok(requested.includes(prefix + 'songs/song-a'), scenario.name);
      assert.ok(requested.includes(prefix + 'groups'), scenario.name);
      assert.ok(requested.includes(prefix + 'songs?favorites=true'), scenario.name);
    }
    console.log(`PASS ${scenario.name}`);
  }
} finally {
  server.closeAllConnections();
  await new Promise(resolve => server.close(resolve));
}
