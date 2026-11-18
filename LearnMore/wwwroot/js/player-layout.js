(() => {
    const mobile = window.matchMedia('(max-width: 991.98px)');
    const root = document.documentElement;
    const disclosures = new Set();
    const navbar = document.querySelector('.navbar');
    const searchToggle = document.querySelector('.player-search-toggle');
    let observedStack;
    let scheduled = false;
    const sizeObserver = new ResizeObserver(() => schedule());
    if (navbar) sizeObserver.observe(navbar);

    function measure() {
        const navbarBottom = Math.max(0, navbar?.getBoundingClientRect().bottom || 0);
        root.style.setProperty('--player-navbar-height', `${navbarBottom}px`);
        const stack = document.querySelector('.video-sticky-stack');
        if (stack && stack !== observedStack) {
            if (observedStack) sizeObserver.unobserve(observedStack);
            observedStack = stack;
            sizeObserver.observe(stack);
        }
        const height = stack?.getBoundingClientRect().height || 0;
        root.style.setProperty('--official-player-height', `${height}px`);
        const navigation = document.getElementById('navigation-bar');
        if (navigation) {
            const room = !mobile.matches || window.innerHeight > navbarBottom + height + 140;
            navigation.hidden = !room || window.scrollY < 160;
        }
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        requestAnimationFrame(() => { scheduled = false; enhance(); measure(); });
    }

    function disclosure(content, className, label) {
        const details = document.createElement('details');
        details.className = className;
        details.open = !mobile.matches;
        const summary = document.createElement('summary');
        summary.textContent = label;
        content.before(details);
        details.append(summary, content);
        disclosures.add(details);
        return details;
    }

    function enhance() {
        document.querySelectorAll('.video-sticky-stack').forEach(stack => {
            if (!stack._practiceDetails) {
                const practice = stack.querySelector('.practice-controls');
                if (practice) {
                    const anchor = document.createComment('practice controls');
                    practice.before(anchor);
                    stack._practiceAnchor = anchor;
                    stack._practiceDetails = disclosure(practice, 'practice-disclosure', '練習控制');
                }
            }
            const details = stack._practiceDetails;
            if (details) {
                if (mobile.matches && details.parentElement === stack) stack.after(details);
                else if (!mobile.matches && details.parentElement !== stack) stack._practiceAnchor.after(details);
            }
            if (!stack._continuePrompt) {
                const prompt = stack.querySelector('.playback-continue-prompt');
                if (prompt) {
                    const anchor = document.createComment('playback recovery');
                    prompt.before(anchor); stack._promptAnchor = anchor; stack._continuePrompt = prompt;
                }
            }
            const prompt = stack._continuePrompt;
            if (prompt) {
                if (mobile.matches && prompt.parentElement === stack) stack.after(prompt);
                else if (!mobile.matches && prompt.parentElement !== stack) stack._promptAnchor.after(prompt);
            }
        });
        document.querySelectorAll('.controls').forEach(controls => {
            if (!controls.closest('.line-more')) disclosure(controls, 'line-more', '更多');
            controls.querySelectorAll('button').forEach(button => {
                if (!button.hasAttribute('aria-label')) button.setAttribute('aria-label', button.title || button.textContent || '歌詞操作');
            });
        });
        const modes = document.querySelector('.feature-section, .group-player-main-content .lyrics-card > .toggle-wrapper');
        if (modes && !modes.dataset.layoutReady) {
            modes.dataset.layoutReady = 'true';
            modes.classList.add('player-mode-row');
            if (modes.classList.contains('toggle-wrapper')) {
                const anchor = document.createComment('group mode controls');
                modes.before(anchor);
                modes._desktopAnchor = anchor;
                const tabs = document.createElement('div');
                tabs.className = 'group-mode-tabs';
                ['完整歌詞', '聚焦', '遮翻譯', '聽寫'].forEach((label, index) => {
                    const button = document.createElement('button');
                    button.type = 'button'; button.className = 'tab-button'; button.textContent = label;
                    button.setAttribute('aria-pressed', String(index === 0));
                    button.addEventListener('click', () => {
                        const card = document.getElementById('lyric-card-right');
                        ['group-focus', 'group-hide-translation', 'group-dictation'].forEach((name, modeIndex) => card.classList.toggle(name, index === modeIndex + 1));
                        tabs.querySelectorAll('button').forEach(item => item.setAttribute('aria-pressed', String(item === button)));
                        card.querySelectorAll('.lyrics-line').forEach(line => {
                            line.tabIndex = index === 3 ? 0 : -1;
                            if (index === 3) line.setAttribute('aria-label', '聽寫歌詞，聚焦顯示答案');
                            else line.removeAttribute('aria-label');
                        });
                    });
                    tabs.append(button);
                });
                modes.prepend(tabs);
            }
        }
        const groupModes = document.querySelector('.toggle-wrapper.player-mode-row');
        if (groupModes) {
            if (mobile.matches && groupModes.parentElement.classList.contains('lyrics-card')) document.querySelector('.video-sticky-stack').after(groupModes);
            else if (!mobile.matches && groupModes._desktopAnchor && groupModes.previousSibling !== groupModes._desktopAnchor) groupModes._desktopAnchor.after(groupModes);
        }
        const lyricsModes = document.querySelector('.feature-section.player-mode-row');
        if (lyricsModes) {
            if (!lyricsModes._desktopAnchor) {
                const anchor = document.createComment('lyrics mode controls');
                lyricsModes.before(anchor); lyricsModes._desktopAnchor = anchor;
            }
            if (mobile.matches && lyricsModes.previousElementSibling !== document.querySelector('.video-sticky-stack')) document.querySelector('.video-sticky-stack').after(lyricsModes);
            else if (!mobile.matches && lyricsModes._desktopAnchor && lyricsModes.previousSibling !== lyricsModes._desktopAnchor) lyricsModes._desktopAnchor.after(lyricsModes);
        }
        document.querySelectorAll('[data-mika-avatar-panel]:not([data-mobile-ready])').forEach(panel => {
            panel.dataset.mobileReady = 'true';
            const toggle = document.createElement('button');
            toggle.type = 'button'; toggle.className = 'mika-mobile-toggle'; toggle.textContent = '展開 Mika';
            toggle.setAttribute('aria-expanded', 'false');
            toggle.addEventListener('click', () => {
                const expanded = panel.classList.toggle('mobile-expanded');
                toggle.setAttribute('aria-expanded', String(expanded)); toggle.textContent = expanded ? '收合 Mika' : '展開 Mika';
            });
            panel.prepend(toggle);
        });
        for (const details of disclosures) if (!details.isConnected) disclosures.delete(details);
    }

    searchToggle?.addEventListener('click', () => {
        const expanded = document.body.classList.toggle('search-expanded');
        searchToggle.setAttribute('aria-expanded', String(expanded));
        searchToggle.setAttribute('aria-label', expanded ? '收合搜尋' : '展開搜尋');
        if (expanded) document.querySelector('.search-input')?.focus();
        schedule();
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && document.body.classList.contains('search-expanded')) { searchToggle?.click(); searchToggle?.focus(); }
    });
    mobile.addEventListener('change', () => {
        disclosures.forEach(details => { details.open = !mobile.matches; });
        document.querySelectorAll('.player-share').forEach(button => { button.textContent = mobile.matches ? '分享' : '複製連結'; });
        schedule();
    });
    document.querySelectorAll('.player-share').forEach(button => {
        button.textContent = mobile.matches ? '分享' : '複製連結';
        button.addEventListener('click', async () => {
            const url = new URL(window.location.href);
            url.hash = '';
            const status = document.querySelector('.player-action-status');
            try {
                if (mobile.matches && navigator.share) await navigator.share({ title: document.title, url: url.href });
                else { await navigator.clipboard.writeText(url.href); if (status) status.textContent = '已複製連結'; }
            } catch (error) {
                if (error.name !== 'AbortError' && status) status.textContent = '無法分享，請複製瀏覽器網址。';
            }
        });
    });
    new MutationObserver(schedule).observe(document.querySelector('main') || document.body, { childList: true, subtree: true });
    window.addEventListener('resize', schedule);
    window.addEventListener('scroll', schedule, { passive: true });
    enhance(); measure();

    const heart = document.querySelector('.lyrics-favorite');
    if (!heart) return;
    const status = document.querySelector('.player-action-status');
    const login = () => { window.location.assign(heart.dataset.loginUrl); };
    async function request(url, form) {
        const response = await fetch(url, { credentials: 'same-origin', ...(form ? { method: 'POST', body: form } : {}) });
        if (response.status === 401) { login(); throw new Error('請先登入'); }
        if (!response.ok && !(form && response.status === 409)) throw new Error('收藏更新失敗，請稍後重試');
        return response;
    }
    const containingUrl = () => `${heart.dataset.containingUrl}?songUid=${encodeURIComponent(heart.dataset.songUid)}`;
    async function refreshHeart() {
        const memberships = await (await request(containingUrl())).json();
        const saved = memberships.length > 0;
        heart.setAttribute('aria-pressed', String(saved)); heart.textContent = saved ? '♥' : '♡';
        heart.setAttribute('aria-label', saved ? '已收藏，管理歌曲群組' : '收藏歌曲');
        return memberships;
    }
    if (heart.dataset.loggedIn === 'true') refreshHeart().catch(error => { status.textContent = error.message; });
    heart.addEventListener('click', async () => {
        if (heart.dataset.loggedIn !== 'true') { login(); return; }
        const existingPicker = document.querySelector('.favorite-picker');
        if (existingPicker) { existingPicker.close(); return; }
        heart.disabled = true;
        try {
            const groups = await (await request(heart.dataset.groupsUrl)).json();
            const memberships = await refreshHeart();
            const dialog = document.createElement('dialog'); dialog.className = 'favorite-picker';
            dialog.setAttribute('aria-label', '收藏到歌曲群組');
            const heading = document.createElement('h2'); heading.textContent = '收藏到歌曲群組';
            const list = document.createElement('div'); list.className = 'favorite-picker-groups';
            const message = document.createElement('p'); message.setAttribute('role', 'status');
            function addGroup(group) {
                const label = document.createElement('label');
                const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.checked = memberships.includes(group.groupId);
                checkbox.addEventListener('change', async () => {
                    const wanted = checkbox.checked; checkbox.disabled = true;
                    const form = new FormData(); form.append('groupId', group.groupId); form.append('songUid', heart.dataset.songUid);
                    try { await request(wanted ? heart.dataset.addUrl : heart.dataset.removeUrl, form); await refreshHeart(); message.textContent = '已更新收藏'; }
                    catch (error) { checkbox.checked = !wanted; message.textContent = error.message; }
                    finally { checkbox.disabled = false; }
                });
                label.append(checkbox, document.createTextNode(group.groupName)); list.append(label);
            }
            groups.forEach(addGroup);
            const form = document.createElement('form');
            const name = document.createElement('input'); name.type = 'text'; name.required = true; name.maxLength = 80; name.placeholder = '新群組名稱'; name.setAttribute('aria-label', '新群組名稱');
            const create = document.createElement('button'); create.type = 'submit'; create.textContent = '建立群組';
            form.append(name, create);
            form.addEventListener('submit', async event => {
                event.preventDefault(); if (!name.value.trim()) return; create.disabled = true;
                const data = new FormData(); data.append('groupName', name.value.trim());
                try { const group = await (await request(heart.dataset.createUrl, data)).json(); addGroup(group); name.value = ''; message.textContent = '已建立，勾選即可收藏'; }
                catch (error) { message.textContent = error.message; }
                finally { create.disabled = false; }
            });
            const close = document.createElement('button'); close.type = 'button'; close.textContent = '完成'; close.addEventListener('click', () => dialog.close());
            dialog.append(heading, list, form, message, close); heart.closest('.player-title-actions').after(dialog);
            dialog.addEventListener('keydown', event => { if (event.key === 'Escape') dialog.close(); });
            dialog.addEventListener('close', () => { dialog.remove(); heart.focus(); }, { once: true }); dialog.show();
        } catch (error) { status.textContent = error.message; }
        finally { heart.disabled = false; }
    });
})();
