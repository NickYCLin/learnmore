import DOMPurify from 'dompurify';

// 與正式首頁使用同一份排序及分頁，避免舊版 mobile API 的 SongID 排序。
// 僅讀取卡片資料；不執行或插入網站的腳本、樣式與媒體。
export function parseWebsiteCatalog(html: string, page: number) {
  const safe = DOMPurify.sanitize(html, {
    ALLOWED_TAGS: ['div', 'a', 'span', 'nav'],
    ALLOWED_ATTR: ['class', 'href', 'aria-disabled', 'aria-current', 'data-songuid', 'data-title', 'data-subtitle', 'data-performer', 'data-video-id'],
    ALLOW_DATA_ATTR: false,
  });
  const document = new DOMParser().parseFromString(safe, 'text/html');
  const cards = Array.from(document.querySelectorAll<HTMLElement>('.card[data-songuid]'));
  if (!cards.length) throw new Error('暫時無法讀取網站歌曲清單，請稍後重新載入。');
  const songs = cards.map(card => {
    const { songuid, title, subtitle, performer, videoId } = card.dataset;
    if (!songuid || !/^[A-Za-z0-9_-]{1,80}$/.test(songuid) || !title) throw new Error('網站歌曲資料不完整，請稍後重新載入。');
    return { songUid: songuid, title, artist: subtitle || '', performer: performer || '', videoId: videoId && /^[A-Za-z0-9_-]{11}$/.test(videoId) ? videoId : null };
  });
  const hasMore = Array.from(document.querySelectorAll<HTMLAnchorElement>('.home-pagination a[href]')).some(link => {
    const url = new URL(link.getAttribute('href')!, 'https://magicplus-design.serveirc.com');
    return link.getAttribute('aria-disabled') !== 'true' && Number(url.searchParams.get('page')) > page;
  });
  return { songs, hasMore };
}
