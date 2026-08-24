const HOME_CONFIG = window.learnMoreHomeConfig || { urls: {} };
/* ========================= 優化配置 ========================= */
    const CONFIG = {
        HOVER_DELAY_MS: 200,
        HIDE_DELAY_MS: 100,
        HOVER_LOCK_MS: 150,
        PROXIMITY_PX: 30,
        DEBOUNCE_MS: 50,
        THROTTLE_MS: 50,
        PREVIEW_SCALE: 1.35,
        PREVIEW_MIN_W: 320,
        PREVIEW_MAX_W: 480,
    };

    /* ========================= 全域狀態 ========================= */
    let cachedGroups = [];
    const joinedUids   = new Set();
    const locallyFaved = new Set();

    let hoverTimer = null;
    let hideTimer = null;
    let proximityWatcher = null;
    let hoverLockUntil = 0;
    let currentPreviewUid = null;
    let animationFrame = null;

    const nowMs = () => performance.now();

    // 停止鄰近監聽（修正：提供函式給事件處理使用）
    function stopProximityWatch() {
        if (proximityWatcher) {
            document.removeEventListener('mousemove', proximityWatcher);
            proximityWatcher = null;
        }
    }

    /* ========================= 效能優化工具 ========================= */
    function rafUpdate(callback) {
        if (animationFrame) {
            cancelAnimationFrame(animationFrame);
        }
        animationFrame = requestAnimationFrame(callback);
    }

    function debounce(func, wait) {
        let timeout;
        return function(...args) {
            clearTimeout(timeout);
            timeout = setTimeout(() => func.apply(this, args), wait);
        };
    }

    function throttle(func, limit) {
        let inThrottle;
        return function(...args) {
            if (!inThrottle) {
                func.apply(this, args);
                inThrottle = true;
                setTimeout(() => inThrottle = false, limit);
            }
        };
    }

    /* ========================= 安全工具函數 ========================= */
    function safePost(iframe, obj){
        try{
            if (!iframe || !iframe.contentWindow) return;
            const payload = typeof obj === 'string' ? obj : JSON.stringify(obj);
            iframe.contentWindow.postMessage(payload, '*');
        }catch{}
    }

    function escapeHtml(s){
        return String(s).replace(/[&<>"']/g, m=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;','\'':'&#39;'}[m]));
    }

    /* ========================= 主要初始化 ========================= */
    document.addEventListener('DOMContentLoaded', async function () {
        // 載入初始數據
        if (isLoggedIn === true || isLoggedIn === 'true') {
            await Promise.all([loadGroups(), loadJoinedUids()]);
            paintMiniHearts();
        }

        // 創建預覽元素
        const preview = document.createElement('div');
        preview.classList.add('hover-card-preview');
        document.body.appendChild(preview);

        // 優化：使用事件委派處理卡片懸停
        let activeHoverTimer = null;

        const handleCardEnter = (card) => {
            if (nowMs() < hoverLockUntil) return;

            clearTimeout(hideTimer);
            clearTimeout(activeHoverTimer);
            stopProximityWatch();

            // 如果已顯示且是不同卡片，立即切換
            if (preview.classList.contains('show') && currentPreviewUid !== card.dataset.songuid) {
                showPreview(card, preview);
            } else if (!preview.classList.contains('show')) {
                // 新顯示則等待延遲
                activeHoverTimer = setTimeout(() => {
                    showPreview(card, preview);
                }, CONFIG.HOVER_DELAY_MS);
            }
        };

        const handleCardLeave = (e) => {
            clearTimeout(activeHoverTimer);

            const to = e.relatedTarget;
            if (!to || !preview.contains(to)) {
                hideTimer = setTimeout(() => hidePreview(preview), CONFIG.HIDE_DELAY_MS);
            }
        };

        // 事件委派監聽
        document.addEventListener('mouseenter', (e) => {
            const card = (e.target && e.target.closest) ? e.target.closest('.card-hover') : null;
            if (card) handleCardEnter(card);
        }, true);

        document.addEventListener('mouseleave', (e) => {
            const card = (e.target && e.target.closest) ? e.target.closest('.card-hover') : null;
            if (card) handleCardLeave(e);
        }, true);

        // 預覽區域事件
        preview.addEventListener('mouseenter', () => {
            clearTimeout(hideTimer);
            stopProximityWatch();
        });

        preview.addEventListener('mouseleave', () => {
            hideTimer = setTimeout(() => hidePreview(preview), CONFIG.HIDE_DELAY_MS);
        });

        // 優化：節流的全域滑鼠移動處理
        const handleGlobalMove = throttle((e) => {
            if (nowMs() < hoverLockUntil) return;

            const overCard = e.target.closest?.('.card-hover');
            const overPreview = e.target.closest?.('.hover-card-preview');

            if (preview.classList.contains('show')) {
                if (!overCard && !overPreview) {
                    clearTimeout(activeHoverTimer);
                    hideTimer = setTimeout(() => hidePreview(preview), CONFIG.HIDE_DELAY_MS);
                } else if (overCard && currentPreviewUid !== overCard.dataset.songuid) {
                    clearTimeout(hideTimer);
                    clearTimeout(activeHoverTimer);
                    showPreview(overCard, preview);
                }
            }
        }, CONFIG.THROTTLE_MS);

        document.addEventListener('pointermove', handleGlobalMove, { passive: true });

        // 關閉預覽的各種觸發
        const closeWithCooldown = (ms = CONFIG.HOVER_LOCK_MS) => {
            clearTimeout(activeHoverTimer);
            hidePreview(preview);
            hoverLockUntil = nowMs() + ms;
        };

        // 優化：防抖和節流處理
        const closeOnScroll = throttle(() => closeWithCooldown(), 100);
        const closeOnResize = debounce(() => closeWithCooldown(), 100);

        window.addEventListener('scroll', closeOnScroll, { passive: true });
        window.addEventListener('resize', closeOnResize, { passive: true });
        window.addEventListener('wheel', closeOnScroll, { passive: true });
        window.addEventListener('touchstart', () => closeWithCooldown(), { passive: true });

        document.addEventListener('keydown', (e) => {
            if (['Escape','PageDown','PageUp','ArrowDown','ArrowUp',' '].includes(e.key)) {
                closeWithCooldown();
            }
        });

        // 🆕 群組刪除功能
        initGroupDeleteFeature();

        // 迷你愛心按鈕事件
        document.addEventListener('pointerdown', (e) => {
            const btn = e.target.closest('.mini-heart-btn');
            if (btn) { e.stopPropagation(); e.preventDefault(); }
        }, { capture: true });

        document.addEventListener('click', (e) => {
            const btn = e.target.closest('.mini-heart-btn');
            if (!btn) return;
            e.stopPropagation();
            e.preventDefault();

            const card = btn.closest('.card-hover');
            if (!card) return;

            const songUid = card.dataset.songuid;

            if (!(isLoggedIn === true || isLoggedIn === 'true')) {
                alert('請先登入才能使用群組功能喔！');
                return;
            }

            const inThisGroupPage = (hasGroupId === "true" && currentGroupId && card.dataset.inCurrentGroup === 'true');
            const alreadyJoined = joinedUids.has(songUid) || locallyFaved.has(songUid) || btn.classList.contains('on');

            if (inThisGroupPage && alreadyJoined) {
                ajaxRemoveAndUpdate(currentGroupId, songUid);
                return;
            }

            // 🆕 如果預覽卡片已經顯示，直接展開群組面板
            if (preview.classList.contains('show') && currentPreviewUid === songUid) {
                const panel = preview.querySelector('.preview-group-panel');
                if (panel && !panel.classList.contains('expanded')) {
                    expandGroupPanel(preview, songUid);
                }
            }
        });

        // 群組播放事件（新的右側操作按鈕）
        const groupPlayBaseUrl = HOME_CONFIG.urls.groupPlay;
        document.addEventListener('click', (e) => {
            const playIcon = e.target.closest('.group-action-btn.play') || e.target.closest('.group-play-inline-btn');
            if (!playIcon) return;
            e.preventDefault();
            e.stopPropagation();
            const groupBtn = playIcon.closest('.btn-purple');
            const groupUid = groupBtn?.dataset.groupUid;
            if (groupUid) {
                const sep = groupPlayBaseUrl.includes('?') ? '&' : '?';
                window.location.href = groupPlayBaseUrl + sep + 'groupUid=' + encodeURIComponent(groupUid);
            }
        });
    });

    /* ========================= 🆕 群組刪除功能 ========================= */
    function initGroupDeleteFeature() {
        // 創建模態框和遮罩
        const overlay = document.createElement('div');
        overlay.className = 'delete-modal-overlay';
        document.body.appendChild(overlay);

        const modal = document.createElement('div');
        modal.className = 'delete-confirm-modal';
        document.body.appendChild(modal);

        // 點擊遮罩關閉
        overlay.addEventListener('click', () => {
            hideDeleteModal(modal, overlay);
        });

        // 事件委派：處理刪除圖標點擊
        document.addEventListener('click', (e) => {
            const deleteIcon = e.target.closest('.group-delete-icon');
            if (!deleteIcon) return;

            e.preventDefault();
            e.stopPropagation();

            if (!(isLoggedIn === true || isLoggedIn === 'true')) {
                alert('請先登入');
                return;
            }

            const groupBtn = deleteIcon.closest('.btn-purple');
            if (!groupBtn) return;

            const groupId = groupBtn.dataset.groupId;
            const groupName = groupBtn.dataset.groupName;

            showDeleteModal(modal, overlay, groupId, groupName);
        });

        // ESC 鍵關閉
        document.addEventListener('keydown', (e) => {
            if (e.key === 'Escape' && modal.classList.contains('show')) {
                hideDeleteModal(modal, overlay);
            }
        });
    }

    function showDeleteModal(modal, overlay, groupId, groupName) {
        modal.innerHTML = `
            <div class="delete-modal-header">
                <div class="icon">
                    <i class="fa-solid fa-triangle-exclamation"></i>
                </div>
                <div class="text">
                    <h3>刪除群組</h3>
                    <p>此操作無法復原</p>
                </div>
            </div>
            <div class="delete-modal-body">
                <p>確定要刪除群組 <span class="group-name">${escapeHtml(groupName)}</span> 嗎？</p>
                <p class="warning">⚠️ 群組內的所有歌曲關聯也會一併移除</p>
            </div>
            <div class="delete-modal-footer">
                <button type="button" class="btn-cancel">取消</button>
                <button type="button" class="btn-delete">
                    <i class="fa-solid fa-trash-can me-1"></i> 確定刪除
                </button>
            </div>
        `;

        const cancelBtn = modal.querySelector('.btn-cancel');
        const deleteBtn = modal.querySelector('.btn-delete');

        cancelBtn.addEventListener('click', () => {
            hideDeleteModal(modal, overlay);
        });

        deleteBtn.addEventListener('click', async () => {
            await handleDeleteGroup(groupId, groupName, deleteBtn, modal, overlay);
        });

        // 顯示模態框
        requestAnimationFrame(() => {
            overlay.classList.add('show');
            modal.classList.add('show');
        });
    }

    function hideDeleteModal(modal, overlay) {
        modal.classList.remove('show');
        overlay.classList.remove('show');
        setTimeout(() => {
            modal.innerHTML = '';
        }, 200);
    }

    async function handleDeleteGroup(groupId, groupName, deleteBtn, modal, overlay) {
        const oldHtml = deleteBtn.innerHTML;
        deleteBtn.disabled = true;
        deleteBtn.innerHTML = '<span class="spinner-border spinner-border-sm me-1"></span> 刪除中…';

        // 使用 Url.Action 產生正確路徑（避免 IIS 虛擬目錄問題）
        const deleteGroupUrl = HOME_CONFIG.urls.deleteGroup;

        try {
            const fd = new FormData();
            fd.append('groupId', groupId);
            const resp = await fetch(deleteGroupUrl, {
                method: 'POST',
                body: fd,
                credentials: 'include'
            });

            if (!resp.ok) {
                if (resp.status === 403) alert('無權刪除此群組');
                else if (resp.status === 404) alert('找不到此群組');
                else alert('刪除失敗');
                deleteBtn.disabled = false;
                deleteBtn.innerHTML = oldHtml;
                return;
            }

            // 成功後的處理
            hideDeleteModal(modal, overlay);

            // 如果當前在被刪除的群組頁面，跳轉到全部頁面
            if (currentGroupId && currentGroupId === groupId.toString()) {
                window.location.href = HOME_CONFIG.urls.homeAll;
            } else {
                // 否則重新載入群組和已加入歌曲列表
                await loadGroups();
                await loadJoinedUids();

                // 更新按鈕和愛心狀態
                refreshGroupButtons();
                paintMiniHearts();

                // 🆕 如果預覽卡片正在顯示，也要更新預覽中的愛心
                const preview = document.querySelector('.hover-card-preview');
                if (preview && preview.classList.contains('show') && currentPreviewUid) {
                    const heart = preview.querySelector('.round-btn.heart');
                    if (heart) {
                        const shouldBeActive = joinedUids.has(currentPreviewUid) || locallyFaved.has(currentPreviewUid);
                        if (shouldBeActive) {
                            heart.classList.add('active');
                        } else {
                            heart.classList.remove('active');
                        }
                    }
                }

                // 顯示成功提示
                showToast(`已刪除群組「${groupName}」`, 'success');
            }
        } catch (err) {
            console.error('刪除群組錯誤:', err);
            alert('刪除發生錯誤');
            deleteBtn.disabled = false;
            deleteBtn.innerHTML = oldHtml;
        }
    }

    // 🆕 簡單的 Toast 提示
    function showToast(message, type = 'info') {
        const toast = document.createElement('div');
        toast.style.cssText = `
            position: fixed;
            top: 80px;
            left: 50%;
            transform: translateX(-50%) translateY(-20px);
            padding: 12px 24px;
            background: ${type === 'success' ? '#10b981' : '#6366f1'};
            color: white;
            border-radius: 10px;
            box-shadow: 0 4px 12px rgba(0,0,0,0.15);
            z-index: 10001;
            font-weight: 600;
            opacity: 0;
            transition: all 0.3s ease;
        `;
        toast.textContent = message;
        document.body.appendChild(toast);

        requestAnimationFrame(() => {
            toast.style.opacity = '1';
            toast.style.transform = 'translateX(-50%) translateY(0)';
        });

        setTimeout(() => {
            toast.style.opacity = '0';
            toast.style.transform = 'translateX(-50%) translateY(-20px)';
            setTimeout(() => toast.remove(), 300);
        }, 2500);
    }

    /* ========================= 愛心狀態管理 ========================= */
    function setMiniHeart(btn, on) {
        btn.classList.toggle('on', !!on);
        btn.title = on ? '已加入群組（點擊管理）' : '加入群組';
        const i = btn.querySelector('i');
        if (!i) return;
        i.className = on ? 'fa-solid fa-heart' : 'fa-regular fa-heart';
    }

    /* ========================= 優化的預覽顯示 ========================= */
    function ytCommand(iframe, func, args = []) {
        safePost(iframe, { event: "command", func, args });
    }

    function calculatePreviewPosition(rect, desiredWidth) {
        const vw = window.innerWidth, vh = window.innerHeight;
        const margin = 10;

        const minW = CONFIG.PREVIEW_MIN_W || 320;
        const maxW = Math.min(CONFIG.PREVIEW_MAX_W || 480, Math.floor(vw * 0.95));

        const baseW = Math.max(rect.width * (CONFIG.PREVIEW_SCALE || 1.35), rect.width * 1.05);
        const w = Math.min(maxW, Math.max(minW, baseW));

        const META_H     = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--preview-meta-h')) || 60;
        const PROGRESS_H = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--preview-progress-h')) || 56;
        const FOOTER_H   = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--preview-footer-h')) || 56;

        const videoH = Math.round(w * 9 / 16);
        const totalH = videoH + META_H + PROGRESS_H + FOOTER_H;
        const h = Math.min(Math.floor(vh * 0.92), totalH);

        const centerX = rect.left + rect.width / 2;
        const centerY = rect.top  + rect.height / 2;

        let left = centerX - w / 2;
        let top  = centerY - h / 2;

        left = Math.max(margin, Math.min(left, vw - w - margin));
        top  = Math.max(margin, Math.min(top,  vh - h - margin));

        return { left, top, width: w, height: h };
    }

    function showPreview(card, previewEl) {
        previewEl._ver = (previewEl._ver || 0) + 1;
        const myVer = previewEl._ver;

        if (previewEl._clearTimer) {
            clearTimeout(previewEl._clearTimer);
            previewEl._clearTimer = null;
        }

        if (currentPreviewUid === card.dataset.songuid && previewEl.classList.contains('show')) {
            return;
        }

        if (typeof previewEl._cleanup === 'function') {
            previewEl._cleanup();
            previewEl._cleanup = null;
        }

        currentPreviewUid = card.dataset.songuid || '';
        previewEl.innerHTML = '';
        previewEl.dataset.songuid = currentPreviewUid;

        const vid            = card.dataset.videoId;
        const songUid        = card.dataset.songuid;
        const channelThumb   = card.dataset.channelThumbnail;
        const lyricsHref     = card.dataset.lyricshref;
        const inCurrentGroup = card.dataset.inCurrentGroup === 'true';
        const titleText      = card.dataset.title    || (card.querySelector('.card-title')?.textContent ?? '');
        const subText        = card.dataset.subtitle || (card.querySelector('.card-subtitle')?.textContent ?? '');
        const performerText  = card.dataset.performer || (card.querySelector('.card-performer')?.textContent.replace(/^演唱者:?\s*/i, '') ?? '');

        let iframe = null;
        let pwrap  = null;

        try {
            const fragment = document.createDocumentFragment();

            // 視頻區域
            const videoSection = document.createElement('div');
            videoSection.className = 'video-section';

            const videoWrap = document.createElement('div');
            videoWrap.className = 'video-wrap';

            if (vid) {
                iframe = document.createElement('iframe');
                iframe.allow = 'accelerometer; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share; autoplay';
                iframe.src = `https://www.youtube.com/embed/${vid}?autoplay=1&mute=1&playsinline=1&controls=0&rel=0&modestbranding=1&enablejsapi=1&origin=${encodeURIComponent(location.origin)}`;
                videoWrap.appendChild(iframe);
            }
            videoSection.appendChild(videoWrap);

            // 頂部控制列
            const topbar = document.createElement('div');
            topbar.className = 'preview-topbar';
            const speakerBtn = document.createElement('button');
            speakerBtn.className = 'speaker-btn';
            speakerBtn.setAttribute('aria-pressed', 'true');
            speakerBtn.innerHTML = '<i class="fa-solid fa-volume-xmark"></i>';
            speakerBtn.addEventListener('click', (e) => {
                e.stopPropagation();
                e.preventDefault();
                const isMuted = speakerBtn.getAttribute('aria-pressed') === 'true';
                if (iframe) {
                    ytCommand(iframe, isMuted ? 'unMute' : 'mute');
                    if (isMuted) ytCommand(iframe, 'playVideo');
                }
                speakerBtn.setAttribute('aria-pressed', isMuted ? 'false' : 'true');
                speakerBtn.innerHTML = isMuted ? '<i class="fa-solid fa-volume-high"></i>' : '<i class="fa-solid fa-volume-xmark"></i>';
            });
            topbar.appendChild(speakerBtn);
            videoSection.appendChild(topbar);

            // 覆蓋連結
            const overlay = document.createElement('a');
            overlay.className = 'preview-link-overlay';
            overlay.href = lyricsHref || '#';
            overlay.setAttribute('aria-label', '前往歌曲頁');
            overlay.addEventListener('pointerdown', (e) => e.stopPropagation(), { capture: true });
            overlay.addEventListener('click', (e) => e.stopPropagation());
            videoSection.appendChild(overlay);

            fragment.appendChild(videoSection);

            // Meta
            const meta = document.createElement('a');
            meta.className = 'preview-meta preview-meta-link';
            meta.href = lyricsHref || '#';
            meta.setAttribute('aria-label', `前往歌曲頁：${titleText}`);
            meta.addEventListener('pointerdown', (e) => e.stopPropagation(), { capture: true });
            meta.addEventListener('click', (e) => e.stopPropagation());
            const metaRows = [
                performerText ? `<div class="meta-row primary home-song-performer clamp-1"><span class="meta-label">演唱者</span><span class="meta-value">${escapeHtml(performerText)}</span></div>` : '',
                `<div class="meta-row subtitle clamp-1"><span class="meta-label">原唱</span><span class="meta-value">${escapeHtml(subText)}</span></div>`
            ].filter(Boolean).join('');
            meta.innerHTML = `<div class="title clamp-1">${escapeHtml(titleText)}</div><div class="meta-list">${metaRows}</div>`;
            fragment.appendChild(meta);

            // 進度條
            pwrap = document.createElement('div');
            pwrap.className = 'preview-progress';
            pwrap.innerHTML = `

                <div class="pp-range"><input type="range" min="0" max="1000" value="0" step="1" aria-label="seek"></div>`;
            fragment.appendChild(pwrap);

            // 🆕 群組面板
            const groupPanel = createGroupPanel(songUid, inCurrentGroup);
            fragment.appendChild(groupPanel);

            // Footer
            const footer = document.createElement('div');
            footer.className = 'preview-footer';

            const leftBox = document.createElement('div');
            leftBox.className = 'preview-left';
            if (channelThumb) {
                const av = document.createElement('img');
                av.className = 'preview-avatar';
                av.src = channelThumb;
                av.alt = '創作者頭像';
                av.loading = 'lazy';
                leftBox.appendChild(av);
            }

            const rightBox = document.createElement('div');
            rightBox.className = 'preview-right';

            // 🆕 愛心按鈕改為切換群組面板
            const heart = document.createElement('button');
            heart.className = 'round-btn heart';
            heart.title = '加入/管理群組';
            heart.innerHTML = '<i class="fas fa-heart"></i>';
            if (inCurrentGroup || locallyFaved.has(songUid) || joinedUids.has(songUid)) {
                heart.classList.add('active');
            }

            heart.addEventListener('pointerdown', (e) => { e.stopPropagation(); e.preventDefault(); }, { capture: true });
            heart.addEventListener('click', async (e) => {
                e.stopPropagation();
                e.preventDefault();

                if (!(isLoggedIn === true || isLoggedIn === 'true')) {
                    alert('請先登入');
                    return;
                }

                // 🆕 切換群組面板
                const panel = previewEl.querySelector('.preview-group-panel');
                if (panel) {
                    if (panel.classList.contains('expanded')) {
                        collapseGroupPanel(previewEl);
                    } else {
                        expandGroupPanel(previewEl, songUid);
                    }
                }
            });
            rightBox.appendChild(heart);

            if (hasGroupId === "true" && currentGroupId) {
                const trash = document.createElement('button');
                trash.className = 'round-btn';
                trash.title = '從目前群組移除';
                trash.innerHTML = '<i class="fa-solid fa-trash-can"></i>';
                trash.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    e.preventDefault();
                    await ajaxRemoveAndUpdate(currentGroupId, songUid);
                    heart.classList.remove('active');
                });
                rightBox.appendChild(trash);
            }

            footer.appendChild(leftBox);
            footer.appendChild(rightBox);
            fragment.appendChild(footer);

            previewEl.appendChild(fragment);
        } catch (err) {
            previewEl.innerHTML = `
                <div style="padding:16px;min-width:260px;min-height:120px;display:flex;align-items:center;justify-content:center;">
                    <span>預覽載入失敗</span>
                </div>`;
            console.error('preview build failed:', err);
        }

        const rect = card.getBoundingClientRect();
        const position = calculatePreviewPosition(rect, rect.width * 2);

        rafUpdate(() => {
            previewEl.style.left   = `${position.left}px`;
            previewEl.style.top    = `${position.top}px`;
            previewEl.style.width  = `${position.width}px`;
            previewEl.style.height = `${position.height}px`;
            previewEl.classList.add('show');
        });

        // 初始化影片控制
        if (iframe && pwrap) {
            const curEl = pwrap.querySelector('.pp-cur');
            const durEl = pwrap.querySelector('.pp-dur');
            const range = pwrap.querySelector('input[type="range"]');

            let duration = 0, timer = null;

            function fmtTime(sec) {
                sec = Math.max(0, Math.floor(sec));
                const m = Math.floor(sec / 60), s = sec % 60;
                return `${m}:${s.toString().padStart(2, '0')}`;
            }

            function onMsg(e) {
                if ((previewEl._ver || 0) !== myVer) return;
                try {
                    const data = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                    if (!data) return;

                    if (data.info && typeof data.info.duration === 'number') {
                        duration = data.info.duration || 0;
                        if (durEl) durEl.textContent = fmtTime(duration);
                        if (timer) clearInterval(timer);
                        timer = setInterval(() => {
                            safePost(iframe, { event: "command", func: "getCurrentTime", args: [] });
                        }, 500);
                    }

                    if (typeof data.info === 'number' && data.id === 1) {
                        const cur = data.info;
                        if (curEl) curEl.textContent = fmtTime(cur);
                        if (duration > 0 && range) range.value = Math.round(cur / duration * 1000);
                    }

                    if (data.info && typeof data.info.currentTime === 'number') {
                        const cur = data.info.currentTime;
                        if (curEl) curEl.textContent = fmtTime(cur);
                        if (duration > 0 && range) range.value = Math.round(cur / duration * 1000);
                    }
                } catch {}
            }

            const initIfSameVersion = () => {
                if ((previewEl._ver || 0) !== myVer) return;
                safePost(iframe, { event: "listening", id: 1, channel: "widget" });
                safePost(iframe, { event: "command", func: "getDuration", args: [] });
            };

            iframe.addEventListener('load', initIfSameVersion);
            window.addEventListener('message', onMsg);

            const seekHandler = throttle(() => {
                if ((previewEl._ver || 0) !== myVer) return;
                if (!iframe || duration <= 0 || !range) return;
                const target = (range.value / 1000) * duration;
                ytCommand(iframe, 'seekTo', [target, true]);
            }, 100);

            if (range) range.addEventListener('input', seekHandler);

            previewEl._cleanup = function () {
                try { window.removeEventListener('message', onMsg); } catch {}
                try { iframe.removeEventListener('load', initIfSameVersion); } catch {}
                if (timer) { clearInterval(timer); timer = null; }
            };
        }
    }

    function hidePreview(previewEl) {
        currentPreviewUid = null;

        if (typeof previewEl._cleanup === 'function') {
            previewEl._cleanup();
            previewEl._cleanup = null;
        }

        previewEl.querySelectorAll('iframe').forEach(f => { try { f.src = ''; } catch {} });

        const myVer = previewEl._ver || 0;

        rafUpdate(() => {
            previewEl.classList.remove('show');
            previewEl._clearTimer = setTimeout(() => {
                if ((previewEl._ver || 0) === myVer && !previewEl.classList.contains('show')) {
                    previewEl.innerHTML = '';
                }
                previewEl._clearTimer = null;
            }, 140);
        });
    }

    /* ========================= 🆕 群組面板功能 ========================= */
    function createGroupPanel(songUid, inCurrentGroup) {
        const panel = document.createElement('div');
        panel.className = 'preview-group-panel';
        panel.innerHTML = `
            <div class="pgp-header">
                <div class="pgp-title">加入到群組</div>
                <button type="button" class="pgp-close" aria-label="關閉"><i class="fa-solid fa-chevron-down"></i></button>
            </div>
            <div class="pgp-inner">
                <div class="pgp-row">
                    <div class="pgp-input-wrap">
                        <i class="fa-solid fa-layer-group pgp-icon-left"></i>
                        <input type="text" class="pgp-input" placeholder="新群組名稱 (Enter建立)" maxlength="40" />
                        <i class="fa-regular fa-lightbulb pgp-icon-right" title="Enter 建立"></i>
                    </div>
                    <button type="button" class="pgp-create-btn"><i class="fa-solid fa-plus"></i> 新增</button>
                </div>
                <div class="pgp-error" aria-live="polite"></div>
            </div>
            <ul class="pgp-list" role="listbox" aria-label="選擇群組"></ul>`;
        panel.querySelector('.pgp-close').addEventListener('click', e => { e.stopPropagation(); collapseGroupPanel(panel.closest('.hover-card-preview')); });
        return panel;
    }

    async function expandGroupPanel(previewEl, songUid) {
        const panel = previewEl.querySelector('.preview-group-panel');
        if (!panel) return;
        const list = panel.querySelector('.pgp-list');
        const err = panel.querySelector('.pgp-error');
        const input = panel.querySelector('.pgp-input');
        const btn = panel.querySelector('.pgp-create-btn');

        let containingIds = [];
        // 使用 Url.Action 產生 API 基底路徑，避免 IIS 應用程式名稱問題
        const groupsContainingSongUrl = HOME_CONFIG.urls.groupsContainingSong;
        try {
            const resp = await fetch(groupsContainingSongUrl + '?songUid=' + encodeURIComponent(songUid), { credentials: 'include' });
            if (resp.ok) containingIds = await resp.json();
        } catch {}

        function paintList(filter = '') {
            list.innerHTML = '';
            const rows = (cachedGroups || []).filter(g => !filter || (g.groupName || g.GroupName || '').toLowerCase().includes(filter.toLowerCase()));
            if (!rows.length) { list.innerHTML = `<li class="pgp-empty">尚未建立任何群組</li>`; return; }
            rows.forEach(g => {
                const gid = g.groupId || g.GroupId;
                const name = g.groupName || g.GroupName;
                const already = containingIds.includes(gid);
                const li = document.createElement('li');
                li.className = 'pgp-item';
                li.tabIndex = 0;
                li.setAttribute('role','option');
                li.innerHTML = `<span class="name">${escapeHtml(name)}</span><span class="pgp-chip ${already ? 'success' : ''}">${already ? '已加入 (點擊移除)' : '加入'}</span>`;
                const handleToggle = async () => {
                    const isAlready = containingIds.includes(gid);
                    if (isAlready) { // remove
                        const ok = await ajaxRemoveAndUpdate(gid, songUid);
                        if (ok) {
                            containingIds = containingIds.filter(id => id !== gid);
                            paintMiniHeartFor(songUid, containingIds.length > 0);
                            const heart = previewEl.querySelector('.round-btn.heart');
                            if (heart && !containingIds.length) heart.classList.remove('active');
                            paintList(input.value); // re-render list to refresh states
                        }
                    } else { // add
                        const ok = await addSongToGroupById(gid, songUid);
                        if (ok) {
                            containingIds.push(gid);
                            joinedUids.add(songUid);
                            paintMiniHeartFor(songUid, true);
                            const heart = previewEl.querySelector('.round-btn.heart');
                            if (heart) heart.classList.add('active');
                            paintList(input.value);
                        }
                    }
                };
                li.addEventListener('click', e => { e.stopPropagation(); handleToggle(); });
                li.addEventListener('keydown', e => {
                    if (e.key==='Enter'){ e.preventDefault(); handleToggle(); }
                    else if (e.key==='Escape'){ collapseGroupPanel(previewEl); }
                    else if (e.key==='ArrowDown'){ e.preventDefault(); li.nextElementSibling?.focus(); }
                    else if (e.key==='ArrowUp'){ e.preventDefault(); li.previousElementSibling?.focus(); }
                });
                list.appendChild(li);
            });
        }
        paintList();

        async function create() {
            const name = (input.value||'').trim();
            err.classList.remove('show'); err.textContent='';
            if (!name){ err.textContent='請輸入群組名稱'; err.classList.add('show'); input.focus(); return; }
            if (name.length < 2){ err.textContent='群組名稱至少 2 個字'; err.classList.add('show'); input.focus(); return; }
            if (cachedGroups.some(g => (g.groupName||g.GroupName||'').toLowerCase() === name.toLowerCase())) { err.textContent='已有相同名稱的群組'; err.classList.add('show'); input.select(); return; }
            const old = btn.innerHTML; btn.disabled=true; btn.innerHTML='<span class="spinner-border spinner-border-sm me-1"></span>建立中…';
            try {
                const fd = new FormData(); fd.append('groupName', name);
                const resp = await fetch(HOME_CONFIG.urls.createGroup, { method:'POST', body:fd, credentials:'include' });
                if (!resp.ok) throw new Error('建立群組失敗');
                const data = await resp.json(); if (!data || !data.groupId) throw new Error('建立群組失敗');
                await loadGroups(); refreshGroupButtons();
                // 自動加入新群組
                containingIds.push(data.groupId);
                joinedUids.add(songUid);
                paintMiniHeartFor(songUid, true);
                const heart = previewEl.querySelector('.round-btn.heart'); if (heart) heart.classList.add('active');
                paintList(input.value);
            } catch(ex){ err.textContent = ex.message || '發生錯誤'; err.classList.add('show'); }
            finally { btn.disabled=false; btn.innerHTML=old; }
        }
        btn.addEventListener('click', e => { e.stopPropagation(); create(); });
        input.addEventListener('keydown', e => { if (e.key==='Enter'){ e.preventDefault(); create(); } else if (e.key==='Escape'){ collapseGroupPanel(previewEl);} else if (e.key==='ArrowDown'){ e.preventDefault(); list.querySelector('.pgp-item')?.focus(); } });
        input.addEventListener('input', () => paintList(input.value));
        panel.classList.add('expanded');
        requestAnimationFrame(()=>{ input.focus(); input.select(); });
    }

    function collapseGroupPanel(previewEl){ const panel = previewEl?.querySelector('.preview-group-panel'); if (!panel) return; panel.classList.remove('expanded'); const input = panel.querySelector('.pgp-input'); const err = panel.querySelector('.pgp-error'); if (input) input.value=''; if (err){ err.classList.remove('show'); err.textContent=''; } }

    /* ========================= API 載入 ========================= */
    async function loadGroups() {
        try {
            const r = await fetch(HOME_CONFIG.urls.getGroups, { credentials: 'include' });
            cachedGroups = await r.json();
        } catch {
            cachedGroups = [];
        }
    }

    /* ========================= 🆕 重新渲染群組按鈕 ========================= */
    function refreshGroupButtons() {
        const container = document.getElementById('group-buttons-container');
        if (!container) return;

        // 找到固定按鈕（全部、周排行、月排行、本站新上架）
        const fixedButtons = container.querySelectorAll('a[href*="type="]');

        // 移除所有舊的群組按鈕
        container.querySelectorAll('.btn-purple').forEach(btn => btn.remove());

        // 根據 cachedGroups 重新建立群組按鈕
        if (cachedGroups && cachedGroups.length > 0) {
            cachedGroups.forEach(group => {
                const btn = document.createElement('a');
                btn.href = `${HOME_CONFIG.urls.home}?groupId=${encodeURIComponent(group.groupId || group.GroupId)}`;
                btn.className = 'btn btn-purple px-4 py-2 rounded-pill group-btn';
                btn.dataset.groupId = (group.groupId || group.GroupId);
                btn.dataset.groupName = group.GroupName || group.groupName;
                btn.dataset.groupUid = group.GroupUid || group.groupUid;

                // 檢查是否為當前群組
                if (currentGroupId && currentGroupId === String(group.groupId || group.GroupId)) {
                    btn.classList.add('active');
                }

                // 添加群組名稱和刪除圖標
                btn.innerHTML = `
                    <span class="group-label">${escapeHtml(group.GroupName || group.groupName)}</span>
                    <span class="group-actions">
                        <span class="group-action-btn play" role="button" title="播放群組" aria-label="播放群組"><i class="fa-solid fa-play"></i></span>
                        <span class="group-action-btn delete group-delete-icon" role="button" title="刪除群組" aria-label="刪除群組"><i class="fa-solid fa-trash-can"></i></span>
                    </span>`;

                container.appendChild(btn);
            });
        }

        // 🆕 新增按鈕加上淡入動畫
        requestAnimationFrame(() => {
            container.querySelectorAll('.btn-purple').forEach((btn, index) => {
                btn.style.opacity = '0';
                btn.style.transform = 'translateY(-10px)';
                btn.style.transition = 'opacity 0.3s ease, transform 0.3s ease';

                setTimeout(() => {
                    btn.style.opacity = '1';
                    btn.style.transform = 'translateY(0)';
                }, index * 50);
            });
        });
    }

    async function loadJoinedUids() {
        try {
            // 🆕 清空舊資料
            joinedUids.clear();

            const resp = await fetch(HOME_CONFIG.urls.getJoinedUids, { credentials: 'include' });
            if (resp.ok) {
                const arr = await resp.json();
                if (Array.isArray(arr)) arr.forEach(uid => joinedUids.add(uid));
            }
        } catch {}
    }

    function paintMiniHearts() {
        document.querySelectorAll('.card-hover').forEach(card => {
            const uid = card.dataset.songuid;
            const btn = card.querySelector('.mini-heart-btn');
            if (!btn) return;

            if (!(isLoggedIn === true || isLoggedIn === 'true')) {
                btn.style.display = 'none';
                return;
            }

            btn.style.display = 'flex';

            // 🆕 檢查是否在任何群組中（不包含 locallyFaved，因為那只是 UI 暫存）
            const on = joinedUids.has(uid) || card.dataset.inCurrentGroup === 'true';
            setMiniHeart(btn, on);
        });
    }

    function paintMiniHeartFor(songUid, on) {
        const card = document.querySelector(`.card-hover[data-songuid="${songUid}"]`);
        if (!card) return;

        const btn = card.querySelector('.mini-heart-btn');
        if (!btn) return;

        if (!(isLoggedIn === true || isLoggedIn === 'true')) {
            btn.style.display = 'none';
            return;
        }

        btn.style.display = 'flex';

        // 🆕 如果 on 參數為 undefined，則自動檢查狀態
        if (on === undefined) {
            on = joinedUids.has(songUid) || card.dataset.inCurrentGroup === 'true';
        }

        setMiniHeart(btn, !!on);
    }

    /* ========================= API 操作 ========================= */
    async function addSongToGroupById(groupId, songUid) {
        const fd = new FormData();
        fd.append('groupId', groupId);
        fd.append('songUid', songUid);

        try {
            const r = await fetch(HOME_CONFIG.urls.addSongToGroup, {
                method: 'POST',
                body: fd,
                credentials: 'include'
            });

            if (r.ok) return true;
            if (r.status === 409) return true;

            alert('加入群組失敗');
            return false;
        } catch {
            alert('加入群組失敗');
            return false;
        }
    }

    async function ajaxRemoveAndUpdate(groupId, songUid) {
        const fd = new FormData();
        fd.append('groupId', groupId);
        fd.append('songUid', songUid);

        try {
            const r = await fetch(HOME_CONFIG.urls.removeSongFromGroup, {
                method: 'POST',
                body: fd,
                credentials: 'include'
            });

            if (!r.ok) {
                alert('移除失敗');
                return false;
            }

            paintMiniHeartFor(songUid, false);

            const card = document.querySelector(`.card-hover[data-songuid="${songUid}"]`);
            if (card && card.dataset.inCurrentGroup === 'true') {
                const col = card.closest('.col');
                if (col) {
                    col.style.transition = 'opacity .18s ease';
                    col.style.opacity = '0';
                    setTimeout(() => col.remove(), 200);
                }
            }

            return true;
        } catch {
            alert('移除失敗');
            return false;
        }
    }
