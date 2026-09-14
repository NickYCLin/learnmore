import { test, expect } from '@playwright/test';
import { website, websiteHTML, respondWebsite } from './website-fixture.mjs';
const songs = [
  { songUid: 'popular-first', title: 'Lemon', artist: '米津玄師', videoId: 'SX_ViT4Ra7k' },
  { songUid: 'popular-second', title: '打上花火 & 練習', artist: 'DAOKO', videoId: '-tKVN2mAKRI' },
];

test('沿用網站卡片順序及下一頁，不使用舊 API 的最新歌曲排序', async ({ page }) => {
  let apiCalled = false;
  await page.route('**/api/mobile/v1/songs?**', route => { apiCalled = true; return route.fulfill({ json: [...songs].reverse() }); });
  await page.route(website + '?**', route => {
    const number = Number(new URL(route.request().url()).searchParams.get('page'));
    return respondWebsite(route, number === 1 ? songs : [{ ...songs[0], songUid: 'page-two', title: '第二頁歌曲' }], number === 1 ? 2 : null);
  });
  await page.goto('/');
  await expect(page.locator('.song-card strong')).toHaveText(['Lemon', '打上花火 & 練習']);
  await page.locator('#more').click();
  await expect(page.locator('.song-card strong')).toHaveText(['Lemon', '打上花火 & 練習', '第二頁歌曲']);
  await expect(page.locator('#more')).toBeHidden();
  expect(apiCalled).toBe(false);
});

test('網站失敗會提示重試，不悄悄退回不同排序；不執行網站脚本', async ({ page }) => {
  let fail = true;
  await page.route(website + '?**', route => route.fulfill({
    status: fail ? 503 : 200, contentType: 'text/html', headers: {'Access-Control-Allow-Origin':'*'},
    body: fail ? 'unavailable' : websiteHTML(songs) + '<script>window.catalogInjected=true</script><img src=x onerror="window.catalogInjected=true">',
  }));
  await page.goto('/');
  await expect(page.locator('#retry')).toBeVisible();
  await expect(page.locator('.song-card')).toHaveCount(0);
  fail = false;
  await page.locator('#retry').click();
  await expect(page.locator('.song-card')).toHaveCount(2);
  expect(await page.evaluate(() => window.catalogInjected)).toBeUndefined();
});
