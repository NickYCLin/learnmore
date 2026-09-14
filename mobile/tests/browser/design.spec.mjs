import { test, expect } from '@playwright/test';

const songs = [
  { songUid: 'one', title: '夜に駆ける', artist: 'YOASOBI', performer: '', videoId: 'x8VYWazR5mE' },
  { songUid: 'two', title: '打上花火', artist: 'DAOKO × 米津玄師', performer: '', videoId: '-tKVN2mAKRI' },
  { songUid: 'three', title: '一首很長很長的日語練習歌曲名稱', artist: '日語練習室', performer: '', videoId: null },
  { songUid: 'four', title: 'Lemon', artist: '米津玄師', performer: '', videoId: 'SX_ViT4Ra7k' },
];
const lines = [
  { id: 1, time: 0, japanese: '今日はいい天気ですね', ruby: '<ruby>今日<rt>きょう</rt></ruby>はいい<ruby>天気<rt>てんき</rt></ruby>ですね', roman: 'kyou wa ii tenki desu ne', chinese: '今天天氣真好呢' },
  { id: 2, time: 5, japanese: '一緒に歌いましょう', ruby: '', roman: 'issho ni utaimashou', chinese: '一起唱歌吧' },
];

test('手機首頁、練習與收藏視窗在窄螢幕保持完整操作', async ({ page }) => {
  await page.route('**/api/mobile/v1/**', async route => {
    const url = new URL(route.request().url());
    const data = url.pathname.endsWith('/session') ? { token: 'design', user: { name: '練習者' } }
      : url.pathname.endsWith('/groups') ? [{ id: 1, name: '每天練一首', included: true }, { id: 2, name: '喜歡的日劇主題曲', included: false }]
      : url.pathname.endsWith('/songs') ? (url.searchParams.get('q') ? [songs[0]] : songs)
      : { song: songs[0], lyrics: lines };
    await route.fulfill({ json: data, headers: { 'Access-Control-Allow-Origin': '*', 'Access-Control-Allow-Headers': '*' } });
  });
  await page.route('**/Mobile/Player?**', route => route.fulfill({ contentType: 'text/html; charset=utf-8', body: '<meta charset="utf-8"><body style="margin:0;background:#161427;color:#ddd;font:14px sans-serif;display:grid;place-items:center;height:100vh">YouTube · 播放器測試預覽</body>' }));
  await page.goto('/');
  await expect(page.locator('.song-card')).toHaveCount(4);
  for (const width of [320, 390, 430]) {
    await page.setViewportSize({ width, height: 844 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await expect(page.locator('#favorites')).toBeInViewport();
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: '../artifacts/ios/redesign-home.png', fullPage: true });
  await page.locator('#query').fill('夜');
  await page.getByRole('button', { name: '搜尋', exact: true }).click();
  await expect(page.locator('.song-card')).toHaveCount(1);
  await expect(page.locator('#catalog-title')).toHaveText('搜尋結果');
  await page.locator('.song-card').click();
  await expect(page.locator('#practice')).toBeVisible();
  await page.locator('#roman').click();
  await expect(page.locator('.roman').first()).toBeHidden();
  await page.locator('#roman').click();
  await page.locator('#loop').click();
  await expect(page.locator('#loop')).toHaveAttribute('aria-pressed', 'true');
  await page.screenshot({ path: '../artifacts/ios/redesign-practice.png', fullPage: true });
  await page.locator('#account').click();
  await page.evaluate(() => {
    const state = new URL(window.testLoginURL).searchParams.get('state');
    window.dispatchEvent(new CustomEvent('test:appUrlOpen', { detail: { url: `learnmore://auth?state=${state}&code=design` } }));
  });
  await expect(page.locator('#account')).toHaveText('登出');
  await page.locator('#collect').click();
  await expect(page.locator('#group-dialog')).toBeVisible();
  await page.screenshot({ path: '../artifacts/ios/redesign-collection.png' });
  await page.locator('#close-groups').click();
  await page.locator('#all').click();
  await expect(page.locator('#library')).toBeVisible();
  await expect(page.locator('#practice')).toBeHidden();
  await expect(page.locator('.player-wrap iframe')).toHaveCount(0);
});
