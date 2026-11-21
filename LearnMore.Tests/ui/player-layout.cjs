const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const puppeteer = require('puppeteer-core');

const fixtures = process.env.LEARNMORE_UI_FIXTURES;
assert(fixtures, 'Generate fixtures with PlayerLayoutTests and LEARNMORE_UI_FIXTURES first.');
const webroot = path.resolve(__dirname, '../../LearnMore/wwwroot');
const artifacts = path.join(fixtures, 'screenshots');
fs.mkdirSync(artifacts, { recursive: true });
const lyrics = Array.from({ length: 12 }, (_, index) => ({ LyricID: index + 1, TimeStamp: index * 5, Japanese: `合成歌詞 ${index + 1}`, Chinese: '測試翻譯', Roman: 'fixture roman' }));
let memberships = [];
let groups = [{ groupId: 1, groupName: '合成群組' }];
const writes = [];
const server = http.createServer(async (request, response) => {
    const url = new URL(request.url, 'http://localhost');
    const route = url.pathname.replace(/^\/LearnMore/, '');
    const json = value => { response.setHeader('Content-Type', 'application/json'); response.end(JSON.stringify(value)); };
    if (route.startsWith('/api/SongGroup/')) {
        const action = route.split('/').pop();
        if (action === 'groupsContainingSong') return json(memberships);
        if (action === 'list') return json(groups);
        if (action === 'addsong' || action === 'removesong') {
            writes.push(action);
            memberships = action === 'addsong' ? [1] : [];
            return json({ success: true });
        }
        if (action === 'create') {
            groups.push({ groupId: 2, groupName: '合成新群組' });
            return json(groups[1]);
        }
        return json([]);
    }
    if (route === '/Lyrics/UpdateUserSettings') return json({ success: true });
    if (route === '/GroupPlayer/SongDetail') return json({ success: true, song: { SongUid: 'fixture-song', Title: '合成測試歌曲', Artist: '合成演唱者', YouTubeVideoUrl: 'https://www.youtube.com/watch?v=abcdefghijk' }, lyrics, comments: [] });
    if (route === '/api/wish') return json([{ Id: 1, Message: '測試許願', CanEdit: true }]);
    if (route === '/api/wish/1/replies') return json([{ Id: 1, Message: '測試回覆', CanEdit: true }]);
    if (route === '/Media/Wish') {
        const filename = url.searchParams.has('member') ? 'wish-member.html' : 'wish.html';
        response.setHeader('Content-Type', 'text/html; charset=utf-8');
        return response.end(fs.readFileSync(path.join(fixtures, filename)));
    }
    const fixture = route.startsWith('/Lyrics') ? (url.searchParams.has('member') ? 'member' : 'lyrics') : route.startsWith('/GroupPlayer/Play') ? 'group' : route === '/Home/Index' ? 'home' : route === '/Login' ? 'login' : null;
    if (fixture) { response.setHeader('Content-Type', 'text/html; charset=utf-8'); return response.end(fs.readFileSync(path.join(fixtures, fixture + '.html'))); }
    const file = path.resolve(webroot, '.' + route);
    if (file.startsWith(webroot + path.sep) && fs.existsSync(file) && fs.statSync(file).isFile()) {
        const type = { '.js': 'text/javascript', '.css': 'text/css', '.png': 'image/png', '.svg': 'image/svg+xml' }[path.extname(file)] || 'application/octet-stream';
        response.setHeader('Content-Type', type); return response.end(fs.readFileSync(file));
    }
    return json([]);
});

const youtubeStub = `window.__stubPlayers = [];
window.YT = { PlayerState: { UNSTARTED: -1, ENDED: 0, PLAYING: 1, PAUSED: 2, BUFFERING: 3 }, Player: function(id, options) {
    const frame = document.createElement('iframe'); frame.title = 'Synthetic official player boundary';
    frame.id = id; frame.setAttribute('frameborder', '0');
    frame.width = options.width; frame.height = options.height;
    frame.allow = 'autoplay; encrypted-media; picture-in-picture; web-share';
    document.getElementById(id).replaceWith(frame);
    this.time = 0; this.state = 2; this.rate = 1; this.muted = false; this.seeks = [];
    this.getIframe = () => frame; this.getCurrentTime = () => this.time; this.getDuration = () => 60;
    this.getPlayerState = () => this.state; this.getPlaybackRate = () => this.rate;
    this.setPlaybackRate = rate => { this.rate = rate; }; this.getAvailablePlaybackRates = () => [0.5, 0.75, 1, 1.25, 1.5, 2];
    this.seekTo = time => { this.time = time; this.seeks.push(time); };
    this.playVideo = () => { this.state = 1; options.events.onStateChange?.({ data: 1, target: this }); };
    this.pauseVideo = () => { this.state = 2; options.events.onStateChange?.({ data: 2, target: this }); };
    this.stopVideo = this.pauseVideo; this.mute = () => { this.muted = true; }; this.unMute = () => { this.muted = false; };
    this.isMuted = () => this.muted; this.setVolume = () => {}; this.destroy = () => frame.remove();
    this.end = () => { this.state = 0; options.events.onStateChange?.({ data: 0, target: this }); };
    window.__stubPlayers.push(this); setTimeout(() => options.events.onReady?.({ target: this }), 0);
}}; window.onYouTubeIframeAPIReady?.();`;

(async () => {
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const base = `http://127.0.0.1:${server.address().port}/LearnMore`;
    const browser = await puppeteer.launch({ executablePath: process.env.CHROME_PATH || '/usr/bin/google-chrome', headless: true, args: ['--no-sandbox'] });
    let checks = 0;
    const check = (value, message) => { assert(value, message); checks++; };
    async function open(route, width, height = 900) {
        const page = await browser.newPage();
        await page.setViewport({ width, height });
        await page.setRequestInterception(true);
        page.on('request', request => {
            if (request.url().startsWith(base.replace('/LearnMore', '')) || request.url() === 'about:blank') return request.continue();
            if (request.url().includes('youtube.com/iframe_api')) return request.respond({ contentType: 'text/javascript', body: youtubeStub });
            return request.respond({ contentType: request.resourceType() === 'script' ? 'text/javascript' : 'text/html', body: '' });
        });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.evaluateOnNewDocument(() => {
            window.__hidden = false;
            Object.defineProperty(document, 'hidden', { configurable: true, get: () => window.__hidden });
            window.lottie = { loadAnimation: () => ({ destroy() {}, addEventListener() {}, play() {}, pause() {} }) };
            window.__shares = []; window.__copies = [];
            navigator.share = async value => { window.__shares.push(value); };
            Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async value => { window.__copies.push(value); } } });
        });
        await page.goto(base + route, { waitUntil: route === '/Login' ? 'load' : 'networkidle0' });
        return { page, errors };
    }
    async function geometry(page, label) {
        await page.waitForFunction(() => {
            const stack = document.querySelector('.video-sticky-stack');
            return Math.abs(parseFloat(getComputedStyle(stack).top) - document.querySelector('.navbar').getBoundingClientRect().bottom) <= 1
                && Math.abs(parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--official-player-height')) - stack.getBoundingClientRect().height) <= 1;
        });
        const values = await page.evaluate(() => {
            const stack = document.querySelector('.video-sticky-stack');
            const frame = stack.querySelector('iframe');
            const modes = document.querySelector('.player-mode-row');
            const navbar = document.querySelector('.navbar');
            const rect = element => { const bounds = element.getBoundingClientRect(); return { top: bounds.top, bottom: bounds.bottom, width: bounds.width, height: bounds.height }; };
            return { navbar: rect(navbar), stack: rect(stack), frame: rect(frame), modes: rect(modes), stickyTop: parseFloat(getComputedStyle(stack).top), overflow: document.documentElement.scrollWidth > innerWidth + 1, allow: frame.allow };
        });
        check(!values.overflow, label + ': no horizontal page overflow');
        check(values.stack.top >= values.navbar.bottom - 1, label + ': player below navbar');
        check(values.modes.top >= values.stack.bottom - 1, label + ': chips do not cover player ' + JSON.stringify(values));
        check(values.modes.top >= values.frame.bottom - 1, label + ': chips below official iframe ' + JSON.stringify(values));
        check(values.modes.height < 90, label + ': chips stay on one horizontal row');
        check(values.frame.height >= values.frame.width * 9 / 16 - 3, label + ': original 16:9 area retained ' + JSON.stringify(values));
        check(!values.allow.includes('picture-in-picture'), label + ': dynamic iframe PiP removed');
        check(Math.abs(values.stickyTop - values.navbar.bottom) <= 1, label + ': measured sticky offset');
        return values;
    }
    try {
        for (const width of [390, 768, 991]) {
            const { page, errors } = await open('/Lyrics/Index?songUid=fixture-song', width);
            await page.waitForSelector('.video-sticky-stack iframe');
            check(await page.$eval('.practice-disclosure', element => !element.open && !element.closest('.video-sticky-stack')), 'mobile practice collapsed and nonsticky');
            check(await page.$eval('.navbar .search-container', element => getComputedStyle(element).display === 'none'), 'mobile search collapsed');
            await page.click('.player-search-toggle');
            check(await page.$eval('.search-input', element => document.activeElement === element), 'search expands and focuses');
            await geometry(page, 'expanded search ' + width);
            await page.keyboard.press('Escape');
            await page.evaluate(() => scrollTo(0, 350));
            await new Promise(resolve => setTimeout(resolve, 200));
            await geometry(page, 'scrolled lyrics ' + width);
            check(!await page.$('#current-lyric-button, #top-button'), 'manual navigation buttons are absent after scrolling');
            await page.click('.practice-disclosure > summary');
            check(await page.$eval('#practice-speed', element => element.getBoundingClientRect().height >= 44), 'practice touch target');
            await page.click('.line-more > summary');
            check(await page.$eval('.line-more .controls button', element => element.getBoundingClientRect().width >= 44 && element.getBoundingClientRect().height >= 44), 'line more touch target');
            check(await page.$eval('.lyrics-favorite', element => element.getBoundingClientRect().height >= 44), 'favorite target');
            check(await page.$eval('.mika-avatar-panel', element => !element.classList.contains('mobile-expanded')), 'Mika collapsed');
            await page.evaluate(() => {
                const toggle = document.querySelector('.mika-mobile-toggle');
                const visibleTop = document.querySelector('.player-mode-row').getBoundingClientRect().bottom + 30;
                scrollBy({ top: toggle.getBoundingClientRect().top - visibleTop, behavior: 'instant' });
            });
            await page.click('.mika-mobile-toggle');
            check(await page.$eval('.mika-mobile-toggle', element => element.getAttribute('aria-expanded') === 'true'), 'Mika expands');
            await page.evaluate(() => document.querySelector('.player-share').click());
            check(await page.$eval('.player-share span', label => label.textContent === '分享'), 'mobile shows the native share action');
            check((await page.evaluate(() => window.__shares[0].url)).startsWith(base + '/Lyrics/'), 'share uses site PathBase URL');
            await page.evaluate(() => { const player = window.__stubPlayers[0]; player.playVideo(); window.__hidden = true; document.dispatchEvent(new Event('visibilitychange')); });
            check(await page.evaluate(() => window.__stubPlayers[0].state === 2), 'background playback paused');
            await page.evaluate(() => window.__stubPlayers[0].playVideo());
            check(await page.evaluate(() => window.__stubPlayers[0].state === 2), 'hidden lyrics player cannot restart');
            await page.screenshot({ path: path.join(artifacts, `lyrics-${width}.png`) });
            check(errors.length === 0, 'lyrics runtime errors: ' + errors.join('; '));
            await page.close();
        }
        const { page: member, errors: memberErrors } = await open('/Lyrics/Index?songUid=fixture-song&member=1', 1440);
        check(await member.$eval('.practice-disclosure', element => element.open), 'desktop replay open');
        check(await member.$eval('.practice-controls-mobile', element => getComputedStyle(element).display === 'none'), 'desktop advanced practice hidden');
        check(await member.$eval('.player-search-toggle', element => getComputedStyle(element).display === 'none'), 'desktop search unchanged');
        check(!await member.$('#current-lyric-button, #top-button'), 'manual navigation buttons are absent on desktop');
        await member.evaluate(() => {
            const sync = document.querySelector('#sync-lyrics-toggle');
            if (!sync.checked) sync.click();
            updateLyricsDisplay(55);
        });
        await member.waitForFunction(() => {
            const highlighted = document.querySelector('#lyric-55.highlight');
            return highlighted && highlighted.closest('.lyric-card').scrollTop > 0;
        });
        check(true, 'automatic lyric highlighting and scrolling still work');
        const actions = await member.$eval('.player-title-actions', row => {
            const favorite = row.querySelector('.lyrics-favorite');
            const share = row.querySelector('.player-share');
            return { favoriteHeight: favorite.offsetHeight, shareHeight: share.offsetHeight, width: share.offsetWidth };
        });
        check(actions.favoriteHeight === actions.shareHeight && actions.shareHeight >= 44, 'song actions align and keep touch targets');
        await member.click('.player-share');
        check((await member.evaluate(() => window.__copies[0])).startsWith(base + '/Lyrics/'), 'desktop copies site link');
        check(await member.$eval('.player-share', (button, width) =>
            button.querySelector('span').textContent === '已複製' && button.offsetWidth === width, actions.width),
        'copy confirmation stays inside the button without shifting its width');
        check(await member.$eval('.player-action-status', element =>
            element.classList.contains('visually-hidden') && element.textContent === '已複製連結'),
        'copy confirmation is announced without an extra visible label');
        await member.screenshot({ path: path.join(artifacts, 'song-actions-copied.png') });
        await member.waitForFunction(() => document.querySelector('.player-share span').textContent === '複製連結');
        await member.click('.lyrics-favorite');
        await member.waitForSelector('dialog[open]');
        check(await member.$eval('.favorite-picker', element => getComputedStyle(element).position === 'static' && !element.matches(':modal')), 'favorite picker stays in flow without covering official player');
        check(await member.$$eval('.favorite-picker input[type=text], .favorite-picker button', controls =>
            controls.every(control => parseFloat(getComputedStyle(control).borderRadius) >= 8 && control.offsetHeight >= 44)),
        'favorite form uses rounded, touch-sized controls');
        await member.screenshot({ path: path.join(artifacts, 'favorite-desktop.png') });
        await member.click('.favorite-picker input[type=checkbox]');
        await member.waitForFunction(() => document.querySelector('.lyrics-favorite').getAttribute('aria-pressed') === 'true');
        check(await member.$eval('.lyrics-favorite', button =>
            button.querySelector('i').classList.contains('fa-solid') && button.querySelector('span').textContent === '已收藏'),
        'saved song shows a filled heart and label');
        await member.click('.favorite-picker input[type=checkbox]');
        await member.waitForFunction(() => document.querySelector('.lyrics-favorite').getAttribute('aria-pressed') === 'false');
        check(writes.join(',') === 'addsong,removesong', 'existing favorite APIs add and remove');
        await member.click('.favorite-picker input[type=text]');
        await member.type('.favorite-picker input[type=text]', '合成新群組');
        await member.click('.favorite-picker button[type=submit]');
        await member.waitForFunction(() => document.querySelectorAll('.favorite-picker input[type=checkbox]').length === 2);
        check(true, 'existing group creation API');
        await member.keyboard.press('Escape');
        await member.waitForFunction(() => !document.querySelector('.favorite-picker') && document.activeElement.matches('.lyrics-favorite'));
        groups.push({ groupId: 3, groupName: '很長的群組名稱'.repeat(12) });
        await member.setViewport({ width: 320, height: 800 });
        await member.evaluate(() => window.LearnMoreTheme.toggle());
        await member.click('.lyrics-favorite');
        await member.waitForSelector('dialog[open]');
        await member.waitForFunction(() => getComputedStyle(document.body).backgroundColor === 'rgb(30, 41, 59)');
        const pickerLayout = await member.$eval('.favorite-picker', element => {
            const colors = getComputedStyle(element);
            const list = element.querySelector('.favorite-picker-groups');
            const checkbox = list.querySelector('input');
            return {
                dark: getComputedStyle(checkbox).colorScheme === 'dark' && colors.color === 'rgb(241, 245, 249)',
                panelOverflow: element.scrollWidth - element.clientWidth,
                listOverflow: list.scrollWidth - list.clientWidth,
                pastViewport: element.getBoundingClientRect().right - innerWidth
            };
        });
        check(pickerLayout.dark && pickerLayout.panelOverflow <= 0 && pickerLayout.listOverflow <= 0 && pickerLayout.pastViewport <= 0,
            'dark favorite picker fits a narrow screen with a long group name: ' + JSON.stringify(pickerLayout));
        await member.screenshot({ path: path.join(artifacts, 'favorite-mobile-dark.png') });
        await member.click('.favorite-picker > button');
        await member.waitForSelector('.favorite-picker', { hidden: true });
        await member.evaluate(() => window.LearnMoreTheme.toggle());
        await member.waitForFunction(() => !document.querySelector('.practice-disclosure').open);
        await member.setViewport({ width: 1440, height: 900 });
        await member.waitForFunction(() => document.querySelector('.practice-disclosure').open);
        check(await member.$eval('.feature-section', element => element.parentElement.classList.contains('video-section')), 'desktop modes restored after resize');
        check(memberErrors.length === 0, 'member runtime errors: ' + memberErrors.join('; '));
        await member.screenshot({ path: path.join(artifacts, 'lyrics-desktop.png') });
        await member.close();
        const { page: guest } = await open('/Lyrics/Index?songUid=fixture-song', 390, 350);
        await guest.evaluate(() => document.querySelector('.lyrics-favorite').click());
        await guest.waitForFunction(() => location.pathname.startsWith('/LearnMore/Login'));
        check(true, 'guest favorite uses existing PathBase login flow');
        await guest.close();
        const { page: group, errors: groupErrors } = await open('/GroupPlayer/Play/fixture-group', 390);
        await group.waitForSelector('.video-sticky-stack iframe');
        await geometry(group, 'group initial');
        check(await group.$eval('.practice-disclosure', element => !element.open && !element.closest('.video-sticky-stack')), 'group practice collapsed and nonsticky');
        await group.evaluate(() => document.querySelector('.player-share').click());
        check((await group.evaluate(() => window.__shares[0].url)).startsWith(base + '/GroupPlayer/Play/'), 'group shares site URL');
        check(await group.$eval('.autoplay-overlay', element => getComputedStyle(element).position === 'static'), 'group consent never covers player on mobile');
        check(await group.$eval('.autoplay-overlay', element =>
            getComputedStyle(element).backgroundColor === 'rgb(255, 255, 255)'),
        'mobile group consent uses the same surface as comments');
        await group.evaluate(() => scrollTo(0, 650));
        await new Promise(resolve => setTimeout(resolve, 200));
        await geometry(group, 'group scrolled');
        await group.evaluate(() => document.querySelector('.practice-disclosure').open = true);
        await group.evaluate(() => { const player = window.__stubPlayers.at(-1); player.time = 10; document.querySelector('[data-group-practice=a]').click(); player.time = 15; document.querySelector('[data-group-practice=b]').click(); player.time = 15.5; player.playVideo(); });
        await group.waitForFunction(() => window.__stubPlayers.at(-1).seeks.includes(10));
        check(true, 'group A-B loops at boundary');
        await group.evaluate(() => { const player = window.__stubPlayers.at(-1); player.pauseVideo(); player.time = 16; });
        const pausedSeekCount = await group.evaluate(() => window.__stubPlayers.at(-1).seeks.length);
        await new Promise(resolve => setTimeout(resolve, 400));
        check(await group.evaluate(count => window.__stubPlayers.at(-1).state === 2 && window.__stubPlayers.at(-1).seeks.length === count, pausedSeekCount), 'group pause does not restart A-B playback');
        await group.evaluate(() => { const player = window.__stubPlayers.at(-1); player.pauseVideo(); document.querySelector('[data-group-practice=clear]').click(); player.time = 55; updateLyricsDisplayRight(55); document.querySelector('#group-practice-line-loop').click(); player.end(); });
        check(await group.evaluate(() => window.__stubPlayers.at(-1).seeks.at(-1) === 55), 'group last line loops on ended');
        await group.evaluate(() => { document.querySelector('#group-practice-line-loop').click(); document.querySelector('#group-practice-song-loop').click(); window.__stubPlayers.at(-1).end(); });
        check(await group.evaluate(() => window.__stubPlayers.at(-1).seeks.at(-1) === 0), 'group whole-song replay returns to start');
        await group.evaluate(() => { window.__hidden = true; document.dispatchEvent(new Event('visibilitychange')); });
        check(await group.evaluate(() => window.__stubPlayers.at(-1).state === 2), 'group background paused');
        await group.evaluate(() => window.__stubPlayers.at(-1).playVideo());
        check(await group.evaluate(() => window.__stubPlayers.at(-1).state === 2), 'hidden group player cannot restart');
        await group.screenshot({ path: path.join(artifacts, 'group-mobile.png') });
        for (const width of [768, 991]) {
            await group.setViewport({ width, height: 900 });
            await geometry(group, 'group tablet ' + width);
        }
        await group.setViewport({ width: 1440, height: 900 });
        await group.waitForFunction(() => document.querySelector('.toggle-wrapper.player-mode-row').parentElement.classList.contains('lyrics-card'));
        await group.waitForFunction(() => {
            const prompt = document.querySelector('.autoplay-overlay');
            return getComputedStyle(prompt).position === 'fixed' && !prompt.getAnimations({ subtree: true }).length;
        });
        const desktopConsent = await group.$eval('.autoplay-overlay h2', heading => ({
            text: getComputedStyle(heading).color,
            background: getComputedStyle(heading.parentElement).backgroundColor
        }));
        check(desktopConsent.text === 'rgb(255, 255, 255)' && desktopConsent.background === 'rgba(0, 0, 0, 0.85)',
            'desktop group consent heading stays white on the dark overlay: ' + JSON.stringify(desktopConsent));
        check(await group.$eval('.group-practice-controls', element => getComputedStyle(element).display === 'none'), 'group practice mobile only');
        check(groupErrors.length === 0, 'group runtime errors: ' + groupErrors.join('; '));
        await group.close();
        for (const width of [390, 991, 992, 1440]) {
            const { page, errors } = await open('/Home/Index', width);
            await page.hover('.card-hover');
            await new Promise(resolve => setTimeout(resolve, 900));
            const visible = await page.$eval('.hover-card-preview', element => element.classList.contains('show'));
            check(visible === (width >= 992), 'home preview width ' + width);
            if (width < 992) {
                check(await page.$eval('#group-buttons-container', element => getComputedStyle(element).maskImage !== 'none'), 'home overflow fade');
                check(await page.$eval('.search-container', element => getComputedStyle(element).display !== 'none'), 'home search unchanged');
            } else {
                await page.setViewport({ width: 390, height: 900 });
                await page.waitForFunction(() => !document.querySelector('.hover-card-preview iframe'));
                check(true, 'resize removes desktop preview iframe');
            }
            check(errors.length === 0, 'home runtime errors: ' + errors.join('; '));
            await page.close();
        }
        const { page: login } = await open('/Login', 320);
        check(await login.$eval('.login-logo', logo =>
            logo.naturalWidth > 0 && logo.width === 64 && document.documentElement.scrollWidth <= innerWidth),
        'login loads a local logo and fits narrow screens when external assets are unavailable');
        await login.screenshot({ path: path.join(artifacts, 'login-mobile.png') });
        await login.close();
        const { page: wish } = await open('/Media/Wish', 390);
        await wish.waitForSelector('.reply-edit');
        check(await wish.$$eval('.wish-edit, .reply-edit', editors =>
            editors.length === 2 && editors.every(editor => editor.hidden && getComputedStyle(editor).display === 'none')),
        'wish and reply editors stay hidden for guests without the external stylesheet');
        await wish.close();
        const { page: wishMember } = await open('/Media/Wish?member=1', 390);
        for (const target of ['wish', 'reply']) {
            await wishMember.click(`.edit-${target}-btn`);
            await wishMember.waitForSelector(`.${target}-edit`, { visible: true });
            const control = await wishMember.$eval(`.save-${target}-btn`, button => ({
                height: button.offsetHeight,
                radius: parseFloat(getComputedStyle(button).borderRadius)
            }));
            check(control.height >= 44 && control.radius > 0,
                `the ${target} editor keeps its styled save button without the external stylesheet: ${JSON.stringify(control)}`);
            await wishMember.click(`.cancel-${target}-btn`);
            await wishMember.waitForSelector(`.${target}-edit`, { hidden: true });
        }
        await wishMember.close();
        console.log(`PASS: ${checks} browser assertions; screenshots: ${artifacts}`);
    } finally {
        await browser.close();
        server.close();
    }
})().catch(error => { console.error(error); server.close(); process.exitCode = 1; });
