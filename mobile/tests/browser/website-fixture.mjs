export const website = 'https://magicplus-design.serveirc.com/LearnMore/';
const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
export function websiteHTML(songs, nextPage = null) {
  return songs.map(song => `<div class="card" data-songuid="${escape(song.songUid)}" data-title="${escape(song.title)}" data-subtitle="${escape(song.artist)}" data-performer="${escape(song.performer)}" data-video-id="${escape(song.videoId)}"></div>`).join('') + (nextPage ? `<nav class="home-pagination"><a href="/LearnMore/?type=all&amp;page=${nextPage}">下一頁</a></nav>` : '');
}
export function respondWebsite(route, songs, nextPage = null) {
  return route.fulfill({ contentType: 'text/html; charset=utf-8', headers: {'Access-Control-Allow-Origin':'*'}, body: websiteHTML(songs, nextPage) });
}
