import { test, expect } from '@playwright/test';
import { website, respondWebsite } from './website-fixture.mjs';

const api = 'https://magicplus-design.serveirc.com/LearnMore/api/mobile/v1/';
const song = { songUid: 'original', title: '原創練習例句', artist: '測試資料', performer: '', videoId: null };
const favorite = { ...song, songUid: 'private', title: '測試帳號的收藏' };

async function respond(route, data, status = 200) {
  await route.fulfill({ status, contentType: 'application/json',
    headers: { 'Access-Control-Allow-Origin': '*' },
    body: status === 204 ? '' : JSON.stringify(data) });
}

test.beforeEach(async ({ page }) => {
  await page.route(website + '?**', route => respondWebsite(route, [song]));
  await page.route(`${api}**`, async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (request.method() === 'OPTIONS') {
      await route.fulfill({ status: 204, headers: {
        'Access-Control-Allow-Origin': '*',
        'Access-Control-Allow-Headers': '*',
        'Access-Control-Allow-Methods': 'GET, POST, PUT, DELETE',
      }});
    } else if (url.pathname.endsWith('/session')) {
      await respond(route, request.method() === 'DELETE' ? null
        : { token: request.postDataJSON().code, user: { name: '測試者' } }, request.method() === 'DELETE' ? 204 : 200);
    } else if (url.pathname.endsWith('/songs')) {
      await respond(route, url.searchParams.get('favorites') === 'true' ? [favorite] : [song]);
    } else if (url.pathname.includes('/songs/')) {
      await respond(route, { song, lyrics: [{ id: 1, time: 0, japanese: 'こんにちは', ruby: '', roman: 'konnichiwa', chinese: '你好' }] });
    } else if (url.pathname.endsWith('/groups')) {
      await respond(route, [{ id: 1, name: '私人練習群組', included: true }]);
    } else {
      throw new Error(`未預期的 API：${request.method()} ${url}`);
    }
  });
  await page.goto('/');
  await expect(page.getByRole('button', { name: /原創練習例句/ })).toBeVisible();
});

async function login(page, code = 'account-a') {
  await page.locator('#account').click();
  await expect.poll(() => page.evaluate(() => window.testLoginURL)).toContain('/Mobile/Connect?');
  await page.evaluate(code => {
    const state = new URL(window.testLoginURL).searchParams.get('state');
    window.testLoginURL = '';
    window.dispatchEvent(new CustomEvent('test:appUrlOpen', {
      detail: { url: `learnmore://auth?state=${state}&code=${code}` },
    }));
  }, code);
  await expect(page.locator('#account')).toHaveText('登出');
}

async function holdGroups(page) {
  let release;
  const held = new Promise(resolve => { release = resolve; });
  let received;
  const requested = new Promise(resolve => { received = resolve; });
  await page.route(`${api}groups?**`, async route => {
    if (route.request().method() === 'OPTIONS') { await route.fallback(); return; }
    received();
    const { data, status } = await held;
    await respond(route, data, status);
  });
  await page.getByRole('button', { name: /原創練習例句/ }).click();
  await page.getByRole('button', { name: '加入收藏' }).click();
  await requested;
  return async (data, status = 200) => {
    const response = page.waitForResponse(response => response.url().includes('/groups?') && response.request().method() === 'GET');
    release({ data, status });
    await (await response).finished();
    // 等待 fetch 的後續處理走完，才檢查是否重新打開對話框。
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  };
}

test('登入到期後清除收藏並回到公開歌曲', async ({ page }) => {
  await login(page);
  await page.getByRole('button', { name: '我的收藏', exact: true }).click();
  await expect(page.getByRole('button', { name: /測試帳號的收藏/ })).toBeVisible();
  await page.route(`${api}songs/private`, route => respond(route, {}, 401));
  await page.getByRole('button', { name: /測試帳號的收藏/ }).click();
  await expect(page.locator('#account')).toHaveText('登入');
  await expect(page.locator('#favorites')).toHaveAttribute('aria-pressed', 'false');
  await expect(page.getByRole('button', { name: /測試帳號的收藏/ })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /原創練習例句/ })).toBeVisible();
});

test('舊帳號的 401 不會清除後來的新登入', async ({ page }) => {
  await login(page);
  const release = await holdGroups(page);
  await page.locator('#account').click();
  await expect(page.locator('#account')).toHaveText('登入');
  await login(page, 'account-b');
  await release({}, 401);
  await expect(page.locator('#account')).toHaveText('登出');
  await expect(page.locator('#notice')).not.toContainText('登入已到期');
});

test('登出後不顯示晚回傳的私人群組', async ({ page }) => {
  await login(page);
  const release = await holdGroups(page);
  await page.locator('#account').click();
  await expect(page.locator('#account')).toHaveText('登入');
  await release([{ id: 1, name: '私人練習群組', included: true }]);
  await expect(page.locator('#group-dialog')).not.toBeVisible();
  await expect(page.locator('#groups')).toBeEmpty();
});

test('目前帳號仍可正常移除收藏', async ({ page }) => {
  await login(page);
  await page.getByRole('button', { name: /原創練習例句/ }).click();
  await page.getByRole('button', { name: '加入收藏' }).click();
  const group = page.getByRole('button', { name: '私人練習群組' });
  await expect(group).toHaveAttribute('aria-pressed', 'true');
  await page.route(`${api}groups/1/songs/original`, async route => {
    if (route.request().method() === 'OPTIONS') { await route.fallback(); return; }
    expect(route.request().method()).toBe('PUT');
    expect(route.request().headers().authorization).toBe('Bearer account-a');
    expect(route.request().postDataJSON()).toEqual({ included: false });
    await respond(route, null, 204);
  });
  await group.click();
  await expect(group).toHaveAttribute('aria-pressed', 'false');
  await expect(page.locator('#group-dialog')).toBeVisible();
});

test('收藏操作遇到登入到期時關閉並清空群組視窗', async ({ page }) => {
  await login(page);
  await page.getByRole('button', { name: /原創練習例句/ }).click();
  await page.getByRole('button', { name: '加入收藏' }).click();
  await page.locator('#group-name').fill('尚未送出的私人名稱');
  await page.route(`${api}groups/1/songs/original`, async route => {
    if (route.request().method() === 'OPTIONS') { await route.fallback(); return; }
    await respond(route, {}, 401);
  });
  await page.getByRole('button', { name: '私人練習群組' }).click();
  await expect(page.locator('#account')).toHaveText('登入');
  await expect(page.locator('#group-dialog')).not.toBeVisible();
  await expect(page.locator('#groups')).toBeEmpty();
  await expect(page.locator('#group-name')).toHaveValue('');
  await page.getByRole('button', { name: '← 返回歌曲' }).click();
  await expect(page.getByRole('button', { name: /原創練習例句/ })).toBeVisible();
});
