import { Capacitor, CapacitorHttp } from '@capacitor/core';
import { App } from '@capacitor/app';
import { Browser } from '@capacitor/browser';
import DOMPurify from 'dompurify';
import { activeLineIndex, loopEnd } from './practice.mjs';
import './style.css';

type Song = { songUid: string; title: string; artist: string; performer: string; videoId: string | null };
type Line = { id: number; time: number; japanese: string; ruby: string; roman: string; chinese: string };
type Detail = { song: Song; lyrics: Line[] };
type Group = { id: number; name: string; included: boolean };
type Player = { destroy(): void; pauseVideo(): void; playVideo(): void; seekTo(time: number, ahead: boolean): void; getCurrentTime(): number; getDuration(): number; getPlayerState(): number };
declare global { interface Window { YT?: { Player: new (id: string, options: unknown) => Player }; onYouTubeIframeAPIReady?: () => void } }
const $ = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;
const native = Capacitor.isNativePlatform();
const backend = 'https://magicplus-design.serveirc.com/LearnMore';
let token = ''; // 憑證只留在記憶體，避免寫入 localStorage 或前端設定。
let pendingLogin: { state: string; verifier: string } | null = null;
let page = 1, favorites = false, query = '', listingSerial = 0, detailSerial = 0;
let catalogNeedsReload = false;
let detail: Detail | null = null, player: Player | null = null, selected = -1, loop = false;
let youtubePromise: Promise<void> | null = null;

function notice(text: string) { $('notice').textContent = text; }
class SessionChangedError extends Error {}
function reportError(error: unknown) {
  if (!(error instanceof SessionChangedError)) notice(errorMessage(error));
}
function signOut() {
  token = ''; pendingLogin = null;
  $('account').textContent = '登入';
  $<HTMLDialogElement>('group-dialog').close();
  $('groups').replaceChildren();
  $<HTMLFormElement>('new-group').reset();
  favorites = false;
  $('all').setAttribute('aria-pressed', 'true');
  $('favorites').setAttribute('aria-pressed', 'false');
  // 清除私人清單，也讓尚未完成的舊清單請求失效。
  listingSerial++;
  $('songs').replaceChildren(); $('more').hidden = true; $('retry').hidden = true;
  catalogNeedsReload = true;
  if ($('practice').hidden) void loadSongs();
}
async function api<T>(path: string, method = 'GET', data?: unknown): Promise<T> {
  const requestToken = token;
  const headers: Record<string,string> = { Accept: 'application/json' };
  if (data !== undefined) headers['Content-Type'] = 'application/json';
  if (requestToken) headers.Authorization = `Bearer ${requestToken}`;
  let status: number, result: unknown;
  try {
    if (native) {
      const response = await CapacitorHttp.request({ url: `${backend}/api/mobile/v1/${path}`, method, headers, data, connectTimeout: 15000, readTimeout: 20000 });
      status = response.status; result = response.data;
    } else {
      const response = await fetch(`/backend/api/mobile/v1/${path}`, { method, headers, body: data === undefined ? undefined : JSON.stringify(data), signal: AbortSignal.timeout(20000) });
      status = response.status; result = response.status === 204 ? null : await response.json().catch(() => null);
    }
  } catch (error) {
    if (requestToken && requestToken !== token) throw new SessionChangedError();
    throw error;
  }
  // 舊工作階段的成功或失敗回應，都不能更新目前帳號的畫面。
  if (requestToken && requestToken !== token) throw new SessionChangedError();
  if (status === 401) {
    if (requestToken) signOut();
    throw new Error('登入已到期，請重新登入。');
  }
  if (status < 200 || status >= 300) throw new Error(status === 429 ? '操作較頻繁，請稍後再試。' : '暫時無法取得資料，請稍後重試。');
  return result as T;
}

function card(song: Song) {
  const button = document.createElement('button'); button.className = 'song-card';
  const art = document.createElement('span'); art.className = 'song-art';
  const placeholder = document.createElement('span'); placeholder.className = 'art-placeholder'; placeholder.textContent = '♫'; placeholder.setAttribute('aria-hidden', 'true'); art.append(placeholder);
  if (song.videoId && /^[A-Za-z0-9_-]{11}$/.test(song.videoId)) {
    const image = document.createElement('img'); image.src = `https://i.ytimg.com/vi/${song.videoId}/mqdefault.jpg`; image.alt = ''; image.loading = 'lazy'; image.onerror = () => image.remove(); art.append(image);
  }
  const play = document.createElement('span'); play.className = 'play-badge'; play.textContent = '▶'; play.setAttribute('aria-hidden', 'true'); art.append(play);
  const title = document.createElement('strong'); title.textContent = song.title;
  const artist = document.createElement('span'); artist.className = 'song-artist'; artist.textContent = song.performer || song.artist;
  button.append(art, title, artist); button.onclick = () => void openSong(song.songUid); return button;
}

async function loadSongs(append = false) {
  const serial = ++listingSerial;
  $('catalog-title').textContent = query ? '搜尋結果' : favorites ? '我的收藏' : '探索歌曲';
  $('catalog-eyebrow').textContent = favorites ? 'YOUR PERSONAL COLLECTION' : 'YOUR DAILY PLAYLIST';
  $('songs').setAttribute('aria-busy', 'true');
  notice('載入歌曲中…'); $('retry').hidden = true; $('more').hidden = true;
  if (!append) { page = 1; $('songs').replaceChildren(); }
  try {
    const songs = await api<Song[]>(`songs?q=${encodeURIComponent(query)}&page=${page}&favorites=${favorites}`);
    if (serial !== listingSerial) return;
    catalogNeedsReload = false;
    $('songs').append(...songs.map(card)); $('more').hidden = songs.length < 30;
    notice(!append && !songs.length ? (favorites ? '還沒有收藏，選一首歌加入群組吧。' : '沒有找到歌曲，試試其他關鍵字。') : '');
  } catch (error) { if (serial === listingSerial && !(error instanceof SessionChangedError)) { reportError(error); $('retry').hidden = false; if (append) page--; } }
  finally { if (serial === listingSerial) $('songs').setAttribute('aria-busy', 'false'); }
}

function errorMessage(error: unknown) { return error instanceof Error && !/fetch|network|timeout/i.test(error.message) ? error.message : '目前無法連線，請確認網路後重試。'; }
function loadYouTube() {
  if (window.YT?.Player) return Promise.resolve();
  if (youtubePromise) return youtubePromise;
  youtubePromise = new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(() => { youtubePromise = null; reject(new Error('播放器載入逾時，請返回歌曲再試一次。')); }, 15000);
    window.onYouTubeIframeAPIReady = () => { clearTimeout(timer); resolve(); };
    const script = document.createElement('script'); script.src = 'https://www.youtube.com/iframe_api';
    script.onerror = () => { clearTimeout(timer); youtubePromise = null; script.remove(); reject(new Error('無法載入 YouTube 播放器。')); };
    document.head.append(script);
  });
  return youtubePromise;
}

function nativePlayer(videoId: string): Player {
  const frame = document.createElement('iframe');
  const origin = new URL(backend).origin;
  frame.src = `${backend}/Mobile/Player?videoId=${encodeURIComponent(videoId)}`;
  frame.title = 'YouTube 歌曲播放器'; frame.allow = 'autoplay; encrypted-media; fullscreen';
  frame.referrerPolicy = 'strict-origin-when-cross-origin';
  let time = 0, duration = 0, state = -1;
  const command = (command: string, extra = {}) => frame.contentWindow?.postMessage({ type: 'learnmore-player-command', command, ...extra }, origin);
  const receive = (event: MessageEvent) => {
    if (event.source !== frame.contentWindow || event.origin !== origin || event.data?.type !== 'learnmore-player-status') return;
    time = Number(event.data.time) || 0; duration = Number(event.data.duration) || 0; state = Number(event.data.state);
    if (event.data.error) $('playback-status').textContent = '影片暫時無法播放，請稍後再試。';
    if (event.data.ready) clearTimeout(timeout);
  };
  window.addEventListener('message', receive);
  const handshake = window.setInterval(() => command('status'), 1000);
  const timeout = window.setTimeout(() => { $('playback-status').textContent = '播放器連線逾時，請確認後端已更新並重新開啟歌曲。'; }, 20000);
  document.querySelector('.player-wrap')!.replaceChildren(frame);
  return {
    destroy: () => { clearInterval(handshake); clearTimeout(timeout); window.removeEventListener('message', receive); frame.remove(); },
    pauseVideo: () => command('pause'), playVideo: () => command('play'), seekTo: value => command('seek', { time: value }),
    getCurrentTime: () => time, getDuration: () => duration, getPlayerState: () => state
  };
}

async function openSong(uid: string) {
  const serial = ++detailSerial;
  notice('載入歌詞中…'); $('retry').hidden = true;
  try {
    const loaded = await api<Detail>(`songs/${encodeURIComponent(uid)}`);
    if (serial !== detailSerial) return;
    detail = loaded; selected = -1; loop = false; $('loop').textContent = '單句重複：關'; $('loop').setAttribute('aria-pressed', 'false');
    $('library').hidden = true; $('practice').hidden = false;
    $('song-title').textContent = detail.song.title; $('song-artist').textContent = detail.song.performer || detail.song.artist;
    $('playback-status').textContent = '';
    $('lyrics').replaceChildren(...detail.lyrics.map((line, index) => {
      const button = document.createElement('button'); button.className = 'lyric';
      const japanese = document.createElement('span'); japanese.className = 'ja';
      japanese.innerHTML = DOMPurify.sanitize(line.ruby || line.japanese, { ALLOWED_TAGS: ['ruby','rt','rp'], ALLOWED_ATTR: [] });
      const roman = document.createElement('span'); roman.className = 'roman'; roman.textContent = DOMPurify.sanitize(line.roman, { ALLOWED_TAGS: [] });
      const chinese = document.createElement('span'); chinese.className = 'zh'; chinese.textContent = line.chinese;
      button.append(japanese, roman, chinese);
      button.onclick = () => { selected = index; player?.seekTo(line.time, true); player?.playVideo(); };
      return button;
    }));
    notice(detail.lyrics.length ? '' : '這首歌的歌詞還在整理中。');
    player?.destroy(); player = null;
    document.querySelector('.player-wrap')!.innerHTML = '<div id="player"></div>';
    const playerWrap = document.querySelector<HTMLElement>('.player-wrap')!;
    playerWrap.hidden = !detail.song.videoId;
    if (!detail.song.videoId) { $('playback-status').textContent = '這首歌沒有可嵌入的 YouTube 影片。'; return; }
    if (native) { player = nativePlayer(detail.song.videoId); window.scrollTo(0, 0); return; }
    await loadYouTube();
    if (serial !== detailSerial) return;
    player = new window.YT!.Player('player', {
      videoId: detail.song.videoId, width: '100%', height: '100%',
      playerVars: { playsinline: 1, controls: 1, origin: window.location.origin },
      events: { onError: () => { $('playback-status').textContent = '影片暫時無法播放，可能是嵌入限制或連線問題。'; } }
    });
    window.scrollTo(0, 0);
  } catch (error) { if (serial === detailSerial) reportError(error); }
}

setInterval(() => {
  if (!player?.getCurrentTime || !detail || $('practice').hidden) return;
  const time = player.getCurrentTime(); const index = activeLineIndex(detail.lyrics, time);
  document.querySelectorAll('.lyric').forEach((el, i) => el.classList.toggle('active', i === index));
  if (loop && selected >= 0 && [0, 1].includes(player.getPlayerState())) {
    const end = loopEnd(detail.lyrics, selected, player.getDuration());
    if (end !== null && time >= end - 0.08) {
      const ended = player.getPlayerState() === 0;
      player.seekTo(detail.lyrics[selected].time, true);
      if (ended) player.playVideo();
    }
  }
}, 150);

function base64Url(bytes: Uint8Array) { return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, ''); }
async function login() {
  if (!native) { notice('登入請使用 iOS App；瀏覽器預覽可測試搜尋與歌詞。'); return; }
  const verifier = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const state = base64Url(crypto.getRandomValues(new Uint8Array(32)));
  const challenge = base64Url(new Uint8Array(await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier))));
  pendingLogin = { verifier, state };
  await Browser.open({ url: `${backend}/Mobile/Connect?challenge=${challenge}&state=${state}` });
}

if (native) {
  void App.addListener('appUrlOpen', async ({ url }) => {
    try {
      const callback = new URL(url);
      if (callback.protocol !== 'learnmore:' || callback.hostname !== 'auth' || !pendingLogin || callback.searchParams.get('state') !== pendingLogin.state) return;
      const verifier = pendingLogin.verifier; pendingLogin = null;
      const result = await api<{ token: string; user: { name: string } }>('session', 'POST', { code: callback.searchParams.get('code'), verifier });
      token = result.token; $('account').textContent = '登出'; notice(`已登入，${result.user.name}`);
      await Browser.close().catch(() => {});
      if (favorites) await loadSongs();
    } catch (error) { reportError(error); }
  });
  void App.addListener('appStateChange', ({ isActive }) => { if (!isActive) player?.pauseVideo(); });
}

async function showGroups() {
  if (!token) { await login(); return; }
  if (!detail) return;
  try {
    const uid = detail.song.songUid;
    const groups = await api<Group[]>(`groups?songUid=${encodeURIComponent(uid)}`);
    if (detail?.song.songUid !== uid) return;
    $('groups').replaceChildren(...groups.map(group => {
      const button = document.createElement('button'); button.textContent = group.name; button.setAttribute('aria-pressed', String(group.included));
      button.onclick = async () => {
        button.disabled = true;
        try { await api(`groups/${group.id}/songs/${encodeURIComponent(uid)}`, 'PUT', { included: !group.included }); group.included = !group.included; button.setAttribute('aria-pressed', String(group.included)); }
        catch (error) { if (!(error instanceof SessionChangedError)) { reportError(error); $<HTMLDialogElement>('group-dialog').close(); } }
        finally { button.disabled = false; }
      }; return button;
    }));
    if (!groups.length) $('groups').textContent = '先建立一個群組，就能收藏這首歌。';
    if (!$<HTMLDialogElement>('group-dialog').open) $<HTMLDialogElement>('group-dialog').showModal();
  } catch (error) { reportError(error); }
}

$('search').onsubmit = event => { event.preventDefault(); query = $<HTMLInputElement>('query').value.trim(); void loadSongs(); };
function returnToLibrary() { detailSerial++; player?.destroy(); player = null; detail = null; $('practice').hidden = true; $('library').hidden = false; notice(''); window.scrollTo(0, 0); }
$('home').onclick = event => { event.preventDefault(); returnToLibrary(); if (favorites || catalogNeedsReload) void loadSongs(); };
$('all').onclick = () => { returnToLibrary(); favorites = false; $('all').setAttribute('aria-pressed','true'); $('favorites').setAttribute('aria-pressed','false'); void loadSongs(); };
$('favorites').onclick = () => { if (!token) { void login().catch(error => notice(errorMessage(error))); return; } returnToLibrary(); favorites = true; $('all').setAttribute('aria-pressed','false'); $('favorites').setAttribute('aria-pressed','true'); void loadSongs(); };
$('more').onclick = () => { page++; void loadSongs(true); };
$('retry').onclick = () => void loadSongs();
$('back').onclick = () => { returnToLibrary(); if (favorites || catalogNeedsReload) void loadSongs(); };
$('loop').onclick = () => { loop = !loop; if (loop && selected < 0) selected = Math.max(0, activeLineIndex(detail?.lyrics || [], player?.getCurrentTime() || 0)); $('loop').textContent = `單句重複：${loop ? '開' : '關'}`; $('loop').setAttribute('aria-pressed',String(loop)); };
$('roman').onclick = () => { const hidden = $('lyrics').classList.toggle('hide-roman'); $('roman').textContent = `羅馬拼音：${hidden ? '關' : '開'}`; $('roman').setAttribute('aria-pressed',String(!hidden)); };
$('collect').onclick = () => void showGroups();
$('close-groups').onclick = () => $<HTMLDialogElement>('group-dialog').close();
$('new-group').onsubmit = async event => { event.preventDefault(); try { await api('groups','POST',{ name: $<HTMLInputElement>('group-name').value }); $<HTMLInputElement>('group-name').value = ''; await showGroups(); } catch (error) { if (!(error instanceof SessionChangedError)) { reportError(error); $<HTMLDialogElement>('group-dialog').close(); } } };
$('account').onclick = async () => { try { if (!token) { await login(); return; } await api('session','DELETE'); signOut(); } catch (error) { reportError(error); } };
window.addEventListener('offline', () => notice('目前沒有網路連線，恢復連線後可以重新載入歌曲。'));
void loadSongs();
