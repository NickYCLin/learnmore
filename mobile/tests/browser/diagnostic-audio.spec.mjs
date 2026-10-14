import { test, expect } from '@playwright/test';
import { website, respondWebsite } from './website-fixture.mjs';

test('診斷旗標預設關閉，不出現診斷播放器', async ({ page }, info) => {
  test.skip(info.project.name === 'diagnostic-on');
  await page.route(website + '?**', route => respondWebsite(route, []));
  await page.goto('/');
  await expect(page.locator('#audio-diagnostic')).toHaveCount(0);
});

test('旗標開啟明確標示非曲庫，WebKit 不假裝提供原生背景播放', async ({ page }, info) => {
  test.skip(info.project.name !== 'diagnostic-on');
  await page.route('**/backend/**', route => route.fulfill({ status: 200, contentType: 'text/html', body: '<html></html>' }));
  await page.goto('/');
  const panel = page.locator('#audio-diagnostic');
  await expect(panel).toContainText('非曲庫');
  await expect(panel).toContainText('不下載音樂');
  await expect(panel).toContainText('180 秒');
  await expect(panel.locator('[data-seek]')).toHaveAttribute('max', '180');
  await expect(panel).toContainText('瀏覽器不模擬背景音訊');
  await expect(panel.locator('[data-play]')).toBeDisabled();
  await expect(panel.locator('[data-seek]')).toBeDisabled();
});
