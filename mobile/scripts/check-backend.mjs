#!/usr/bin/env node

const args = process.argv.slice(2);
if (args.length && (args[0] !== '--base-url' || args.length !== 2)) {
  console.error('用法：npm run check:backend -- [--base-url https://網站/LearnMore]');
  process.exit(1);
}
const base = new URL(args[1] || 'https://magicplus-design.serveirc.com/LearnMore/');
if (base.username || base.password || base.search || base.hash ||
    (base.protocol !== 'https:' && !(base.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(base.hostname)))) {
  throw new Error('請提供 HTTPS 網站地址；本機測試可使用 localhost 的 HTTP 地址。');
}
base.pathname = base.pathname.replace(/\/?$/, '/');
const results = [];

async function check(path, expectedStatus, validate) {
  try {
    const response = await fetch(new URL(path, base), {
      redirect: 'manual', signal: AbortSignal.timeout(15000),
      headers: { Accept: 'application/json', 'User-Agent': 'LearnMore-Mobile-Check' },
    });
    if (response.status !== expectedStatus) throw new Error(`HTTP ${response.status}，預期 ${expectedStatus}`);
    let data;
    if (validate) {
      if (!response.headers.get('content-type')?.includes('application/json')) throw new Error('回應不是 JSON');
      data = await response.json();
      if (!validate(data)) throw new Error('回應內容不符合 App 使用的格式');
    }
    results.push({ path, passed: true, message: `HTTP ${response.status}` });
    return data;
  } catch (error) {
    results.push({ path, passed: false, message: error.message });
  }
}

const songValid = song => song && typeof song.songUid === 'string' && /^[A-Za-z0-9_-]{1,80}$/.test(song.songUid)
  && ['title', 'artist', 'performer'].every(key => typeof song[key] === 'string')
  && (song.videoId === null || (typeof song.videoId === 'string' && /^[A-Za-z0-9_-]{11}$/.test(song.videoId)));
const [, songs] = await Promise.all([
  check('api/mobile/v1/status', 200, data => data?.version === 1),
  check('api/mobile/v1/songs', 200, data => Array.isArray(data) && data.every(songValid)),
  check('api/mobile/v1/groups', 401),
]);
if (songs?.length) {
  const uid = songs[0].songUid;
  await check(`api/mobile/v1/songs/${encodeURIComponent(uid)}`, 200, data =>
    songValid(data?.song) && data.song.songUid === uid && Array.isArray(data.lyrics)
    && data.lyrics.every(line => line && Number.isInteger(line.id) && Number.isFinite(line.time) && line.time >= 0
      && ['japanese', 'ruby', 'roman', 'chinese'].every(key => typeof line[key] === 'string')));
} else if (songs) {
  results.push({ path: '歌曲與歌詞', passed: false, message: '尚無歌曲可供 App 試用，無法驗證單曲 API' });
}
for (const result of results) console.log(`${result.passed ? 'PASS' : 'FAIL'} ${result.path}：${result.message}`);
console.log('此檢查不會登入或修改資料；Google 登入、收藏同步與影片播放仍需真機驗證。');
process.exitCode = results.every(result => result.passed) ? 0 : 1;
