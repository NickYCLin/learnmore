(function () {
    const panel = document.querySelector('[data-mika-avatar-panel]');
    if (!panel) {
        return;
    }

    const statusEl = panel.querySelector('[data-mika-avatar-status]');
    const messageEl = panel.querySelector('[data-mika-avatar-message]');
    const connectButton = panel.querySelector('[data-mika-avatar-connect]');
    const form = panel.querySelector('[data-mika-avatar-form]');
    const input = panel.querySelector('[data-mika-avatar-input]');
    const live2dFrame = panel.querySelector('[data-mika-avatar-live2d]');
    const avatarSwitcher = panel.querySelector('[data-mika-avatar-switcher]');
    let avatarChoiceButtons = Array.from(panel.querySelectorAll('[data-mika-avatar-choice]'));
    const wsUrl = panel.dataset.avatarWsUrl;
    const avatarBaseUrl = (panel.dataset.avatarBaseUrl || 'https://ycspace.myvnc.com/mika-avatar').replace(/\/$/, '');
    const avatarStorageKey = 'learnmore:mika-avatar-id';
    const avatarPreferredStorageKey = 'learnmore:mika-avatar-preferred-id';
    const fallbackAvatarConfigs = {
        mao_pro: { id: 'mao_pro', runtime: 'live2d', displayName: 'Mao Pro', switchLabel: '2D' }
    };
    let avatarConfigs = { ...fallbackAvatarConfigs };
    let preferredAvatarId = 'mao_pro';
    const avatarOrigin = (() => {
        try {
            return new URL(avatarBaseUrl).origin;
        } catch {
            return '';
        }
    })();
    const live2dStatusTimeoutMs = 10000;
    const vrmStatusTimeoutMs = 45000;

    let socket = null;
    let clientUid = '';
    let currentState = 'idle';
    let lastLyricKey = '';
    let reconnectTimer = 0;
    let beatTimer = 0;
    let musicWasPlaying = false;
    let lastBeatAt = 0;
    let lastVocalLevelAt = 0;
    let currentLyricDetail = null;
    let songDanceProfile = null;
    let songDanceProfileContextKey = '';
    let songContextOverride = null;
    let songProfileSentKey = '';
    let choreographyCatalog = null;
    let choreographyCatalogStatus = 'idle';
    let choreographyCatalogPromise = null;
    let rhythmState = {
        beatIntervalMs: 560,
        emaLevel: 0,
        lastLevel: 0,
        density: 0.5,
        onsetScore: 0,
        beatCount: 0
    };
    let vocalAudioContext = null;
    let vocalAudioBuffer = null;
    let vocalAudioDecodePromise = null;
    let vocalAudioUrl = '';
    let vocalWarmupTimer = 0;
    let vocalDecodeStatus = 'idle';
    let vocalDecodeStartedAt = 0;
    let vocalDecodeFinishedAt = 0;
    let vocalLevel = 0;
    let vocalAnalysisFailed = false;
    let lyricSpeechActive = false;
    let lyricSpeechTimer = 0;
    let lyricSpeechStartedAt = 0;
    let lyricSpeechText = '';
    let lyricSpeechDetail = null;
    let smoothedVowels = { a: 1, i: 0, u: 0, e: 0, o: 0 };
    let ttsAudio = null;
    let ttsAudioUrl = '';
    let ttsAudioContext = null;
    let ttsAnimationFrame = 0;
    let live2dStatusTimer = 0;
    let avatarSwitchSerial = 0;

    function setState(state, message) {
        currentState = state;
        panel.dataset.mikaState = state;
        if (statusEl) {
            statusEl.dataset.state = state;
        }
        if (messageEl && message) {
            messageEl.textContent = message;
        }
    }

    function canSend() {
        return socket && socket.readyState === WebSocket.OPEN;
    }

    function send(payload) {
        if (!canSend()) {
            return false;
        }
        socket.send(JSON.stringify(payload));
        return true;
    }

    function clearLive2dStatusTimer() {
        window.clearTimeout(live2dStatusTimer);
        live2dStatusTimer = 0;
    }

    function scheduleLive2dStatusTimeout() {
        clearLive2dStatusTimer();
        if (panel.dataset.live2dState === 'ready') {
            return;
        }

        panel.dataset.live2dState = 'loading';
        panel.dataset.live2dError = '';
        const timeoutMs = panel.dataset.avatarRuntime === 'vrm' ? vrmStatusTimeoutMs : live2dStatusTimeoutMs;
        live2dStatusTimer = window.setTimeout(function () {
            if (panel.dataset.live2dState === 'ready' || panel.dataset.live2dState === 'error') {
                return;
            }
            panel.dataset.live2dState = 'error';
            panel.dataset.live2dError = 'live2d-status-timeout';
            panel.dataset.live2dModelLoaded = 'false';
        }, timeoutMs);
    }

    function getStoredAvatarId() {
        try {
            const stored = window.localStorage?.getItem(avatarStorageKey) || '';
            const storedPreferred = window.localStorage?.getItem(avatarPreferredStorageKey) || '';
            return avatarConfigs[stored] && storedPreferred === preferredAvatarId ? stored : preferredAvatarId;
        } catch {
            return preferredAvatarId;
        }
    }

    function persistAvatarId(avatarId) {
        try {
            window.localStorage?.setItem(avatarStorageKey, avatarId);
            window.localStorage?.setItem(avatarPreferredStorageKey, preferredAvatarId);
        } catch {
            // localStorage can be unavailable in private browsing or locked-down iframes.
        }
    }

    function normalizeAvatarConfig(avatar) {
        const id = String(avatar?.id || '').trim();
        const runtime = String(avatar?.runtime || '').trim().toLowerCase();
        if (!id || !['live2d', 'vrm'].includes(runtime) || avatar?.available !== true) {
            return null;
        }

        return {
            id,
            runtime,
            displayName: String(avatar.displayName || id).trim(),
            switchLabel: getAvatarSwitchLabel(id, runtime, avatar.displayName)
        };
    }

    function getAvatarSwitchLabel(id, runtime, displayName) {
        if (id === 'mao_pro') {
            return '2D';
        }
        return String(displayName || (runtime === 'vrm' ? '3D' : '2D')).trim();
    }

    function bindAvatarChoiceButtons() {
        avatarChoiceButtons = Array.from(panel.querySelectorAll('[data-mika-avatar-choice]'));
        avatarChoiceButtons.forEach(function (button) {
            button.addEventListener('click', function () {
                setActiveAvatar(button.dataset.mikaAvatarChoice, { persist: true });
            });
        });
    }

    function renderAvatarChoices(avatars) {
        if (!avatarSwitcher || !Array.isArray(avatars) || avatars.length === 0) {
            bindAvatarChoiceButtons();
            return;
        }

        avatarSwitcher.replaceChildren();
        avatars.forEach(function (avatar) {
            const button = document.createElement('button');
            button.type = 'button';
            button.dataset.mikaAvatarChoice = avatar.id;
            button.textContent = avatar.switchLabel;
            button.title = avatar.displayName || avatar.id;
            avatarSwitcher.appendChild(button);
        });
        bindAvatarChoiceButtons();
    }

    async function loadAvatarCatalog() {
        const integrationResponse = await fetch(`${avatarBaseUrl}/api/integration/learnmore`, {
            cache: 'no-store',
            mode: 'cors'
        });
        if (!integrationResponse.ok) {
            throw new Error(`Mika integration HTTP ${integrationResponse.status}`);
        }

        const integration = await integrationResponse.json();
        const allowedAvatarIds = new Set(
            (Array.isArray(integration?.availableAvatarIds) ? integration.availableAvatarIds : [])
                .map(id => String(id || '').trim())
                .filter(Boolean)
        );
        const nextPreferredAvatarId = String(integration?.preferredAvatarId || integration?.defaultAvatarId || 'mao_pro');

        const response = await fetch(`${avatarBaseUrl}/api/avatars`, {
            cache: 'no-store',
            mode: 'cors'
        });
        if (!response.ok) {
            throw new Error(`avatar catalog HTTP ${response.status}`);
        }

        const catalog = await response.json();
        const dynamicConfigs = {};
        const dynamicAvatars = [];
        (Array.isArray(catalog?.avatars) ? catalog.avatars : []).forEach(function (avatar) {
            if (!allowedAvatarIds.has(String(avatar?.id || '').trim())) {
                return;
            }
            const config = normalizeAvatarConfig(avatar);
            if (!config) {
                return;
            }
            dynamicConfigs[config.id] = config;
            dynamicAvatars.push(config);
        });

        const previewAvatar = buildFormalExternalPreviewAvatar(integration);
        if (previewAvatar) {
            dynamicConfigs[previewAvatar.id] = previewAvatar;
            const existingIndex = dynamicAvatars.findIndex(avatar => avatar.id === previewAvatar.id);
            if (existingIndex >= 0) {
                dynamicAvatars[existingIndex] = previewAvatar;
            } else {
                dynamicAvatars.push(previewAvatar);
            }
        }

        if (dynamicAvatars.length === 0) {
            return;
        }

        avatarConfigs = dynamicConfigs;
        const previewPreferredId = integration?.formalMikaExternalPreviewPreferred === true
            ? String(integration?.formalMikaExternalPreviewAvatarId || 'mika_formal_vrm')
            : '';
        preferredAvatarId = avatarConfigs[previewPreferredId]
            ? previewPreferredId
            : avatarConfigs[nextPreferredAvatarId]
                ? nextPreferredAvatarId
                : 'mao_pro';
        renderAvatarChoices(dynamicAvatars);
        panel.dataset.avatarCatalogStatus = 'ready';
        panel.dataset.avatarCatalogCount = String(dynamicAvatars.length);
        panel.dataset.avatarPreferredId = preferredAvatarId;
        panel.dataset.avatarPreferredRuntime = String(avatarConfigs[preferredAvatarId]?.runtime || integration?.preferredRuntime || '');
        panel.dataset.avatarFormalPreviewAvailable = previewAvatar ? 'true' : 'false';
    }

    function buildFormalExternalPreviewAvatar(integration) {
        if (integration?.formalMikaExternalPreviewAvailable !== true) {
            return null;
        }

        const id = String(integration.formalMikaExternalPreviewAvatarId || 'mika_formal_vrm').trim();
        const runtime = String(integration.formalMikaExternalPreviewRuntime || 'vrm').trim().toLowerCase();
        const embedUrl = String(integration.formalMikaExternalPreviewEmbedUrl || '').trim();
        if (!id || runtime !== 'vrm' || !embedUrl) {
            return null;
        }

        return {
            id,
            runtime,
            displayName: 'Mika Formal VRM',
            switchLabel: '3D',
            embedUrl,
            externalPreview: true
        };
    }

    function buildAvatarEmbedUrl(avatarId) {
        const config = avatarConfigs[avatarId] || fallbackAvatarConfigs.mao_pro;
        const url = new URL(config.embedUrl || `${avatarBaseUrl}/embed`, avatarBaseUrl);
        if (!url.searchParams.has('framing')) {
            url.searchParams.set('framing', panel.dataset.avatarFraming || 'lyrics-rail');
        }
        if (!url.searchParams.has('avatar')) {
            url.searchParams.set('avatar', config.id);
        }
        if (!url.searchParams.has('runtime')) {
            url.searchParams.set('runtime', config.runtime);
        }
        return url.toString();
    }

    function updateAvatarChoiceButtons(avatarId) {
        avatarChoiceButtons.forEach(function (button) {
            button.dataset.active = button.dataset.mikaAvatarChoice === avatarId ? 'true' : 'false';
        });
    }

    function setActiveAvatar(avatarId, options) {
        const config = avatarConfigs[avatarId] || fallbackAvatarConfigs.mao_pro;
        const shouldPersist = options?.persist !== false;
        if (shouldPersist) {
            persistAvatarId(config.id);
        }

        panel.dataset.avatarId = config.id;
        panel.dataset.avatarRuntime = config.runtime;
        panel.dataset.live2dRuntime = config.runtime;
        panel.dataset.live2dState = 'loading';
        panel.dataset.live2dError = '';
        panel.dataset.live2dModelLoaded = 'false';
        updateAvatarChoiceButtons(config.id);
        songProfileSentKey = '';

        const embedUrl = buildAvatarEmbedUrl(config.id);
        if (live2dFrame && live2dFrame.src !== embedUrl) {
            const switchSerial = ++avatarSwitchSerial;
            live2dFrame.src = 'about:blank';
            window.setTimeout(function () {
                if (switchSerial !== avatarSwitchSerial) {
                    return;
                }
                live2dFrame.src = embedUrl;
            }, 0);
        } else {
            scheduleLive2dStatusTimeout();
            sendLive2dSongProfile(true);
        }
    }

    function cleanText(value) {
        const container = document.createElement('div');
        container.innerHTML = value || '';
        return (container.textContent || '').replace(/\s+/g, ' ').trim();
    }

    function getSpeakableJapaneseText(value) {
        const container = document.createElement('div');
        container.innerHTML = value || '';
        container.querySelectorAll('ruby').forEach(function (ruby) {
            const reading = Array.from(ruby.querySelectorAll('rt'))
                .map(rt => rt.textContent || '')
                .join('');
            if (reading.trim()) {
                ruby.replaceWith(document.createTextNode(reading));
            }
        });

        return (container.textContent || '')
            .replace(/[\p{Script=Han}々〆ヵヶ]+[（(]([ぁ-ゖァ-ヺー・\s]+)[）)]/gu, '$1')
            .replace(/[（(]([ぁ-ゖァ-ヺー・\s]+)[）)]/gu, '$1')
            .replace(/\s+/g, ' ')
            .trim();
    }

    function connect() {
        if (!wsUrl || currentState === 'connecting' || canSend()) {
            return;
        }

        clearTimeout(reconnectTimer);
        setState('connecting', '連線中');

        socket = new WebSocket(wsUrl);

        socket.addEventListener('open', function () {
            setState('connected', 'Mika 已準備好');
        });

        socket.addEventListener('message', function (event) {
            let data = null;
            try {
                data = JSON.parse(event.data);
            } catch {
                return;
            }

            if (data.client_uid) {
                clientUid = data.client_uid;
            }

            if (messageEl && data.type === 'full-text' && typeof data.text === 'string') {
                messageEl.textContent = data.text;
            }

            if (messageEl && data.type === 'sentence' && data.display_text) {
                messageEl.textContent = data.display_text.text || '';
                sendLive2dAction(data.actions);
            }

            if (messageEl && data.type === 'audio' && data.display_text) {
                messageEl.textContent = data.display_text.text || data.transcript || '';
                sendLive2dAction(data.actions);
            }
        });

        socket.addEventListener('close', function () {
            socket = null;
            clientUid = '';
            setState('idle', 'Mika 離線');
        });

        socket.addEventListener('error', function () {
            setState('error', 'Mika 連線失敗');
        });
    }

    function sendText(text, metadata) {
        const trimmed = (text || '').trim();
        if (!trimmed) {
            return;
        }

        if (!canSend()) {
            connect();
            reconnectTimer = window.setTimeout(function () {
                sendText(trimmed, metadata);
            }, 700);
            return;
        }

        send({
            type: 'text-input',
            text: trimmed,
            metadata: metadata || {}
        });
    }

    function sendLive2dAction(actions) {
        const expression = actions?.expressions?.[0];
        if (!Number.isInteger(expression) || !live2dFrame?.contentWindow) {
            return;
        }

        live2dFrame.contentWindow.postMessage({
            type: 'mika-avatar-action',
            expression: expression
        }, '*');
    }

    function sendLive2dBeat(playing, currentTime, intensity, rhythm) {
        if (!live2dFrame?.contentWindow) {
            return;
        }

        live2dFrame.contentWindow.postMessage({
            type: 'mika-avatar-music-beat',
            playing: playing,
            currentTime: Number.isFinite(currentTime) ? currentTime : 0,
            intensity: intensity,
            rhythm: rhythm || {
                beatIntervalMs: rhythmState.beatIntervalMs,
                density: rhythmState.density
            }
        }, '*');
    }

    function sendLive2dMusicState(playing, currentTime) {
        if (!live2dFrame?.contentWindow) {
            return;
        }

        live2dFrame.contentWindow.postMessage({
            type: 'mika-avatar-music-state',
            playing: playing,
            currentTime: Number.isFinite(currentTime) ? currentTime : getMusicTime()
        }, '*');
    }

    function sendLive2dSongProfile(force) {
        if (!live2dFrame?.contentWindow) {
            return;
        }

        const profile = getSongDanceProfile();
        const key = [
            profile.songUid,
            profile.choreographyId,
            profile.hasChoreography ? '1' : '0',
            profile.style,
            profile.tempo,
            profile.energy.toFixed(2),
            profile.groove.toFixed(2),
            profile.motionBias
        ].join(':');
        if (!force && key === songProfileSentKey) {
            return;
        }

        songProfileSentKey = key;
        live2dFrame.contentWindow.postMessage({
            type: 'mika-avatar-song-profile',
            profile
        }, '*');
    }

    function sendLive2dVocalLevel(playing, currentTime, features) {
        if (!live2dFrame?.contentWindow) {
            return;
        }

        live2dFrame.contentWindow.postMessage({
            type: 'mika-avatar-vocal-level',
            playing: playing,
            currentTime: Number.isFinite(currentTime) ? currentTime : 0,
            level: Math.min(1, Math.max(0, Number(features?.level) || 0)),
            bands: features?.bands || { low: 0, mid: 0, high: 0 },
            vowels: features?.vowels || { a: 1, i: 0, u: 0, e: 0, o: 0 }
        }, '*');
    }

    function getJapaneseSpeechVoice() {
        const speech = window.speechSynthesis;
        if (!speech || typeof speech.getVoices !== 'function') {
            return null;
        }

        const voices = speech.getVoices();
        const scoredVoices = voices
            .map(voice => {
                const name = String(voice.name || '').toLowerCase();
                const lang = String(voice.lang || '').toLowerCase();
                const label = `${name} ${lang}`;
                let score = 0;

                if (/^ja(-|_)?/i.test(voice.lang || '')) score += 90;
                if (/japanese|日本|nihongo/.test(label)) score += 55;
                if (/nanami|haruka|kyoko|sayaka|mizuki|female|woman|girl|少女|女の子|女性/.test(label)) score += 40;
                if (/natural|premium|online/.test(label)) score += 8;
                if (/google/.test(label)) score -= 30;
                if (/male|男/.test(label)) score -= 40;

                return { voice, score };
            })
            .filter(item => item.score > 0)
            .sort((left, right) => right.score - left.score);

        return scoredVoices[0]?.voice || null;
    }

    function stopLyricSpeechMouth() {
        if (lyricSpeechTimer) {
            window.clearInterval(lyricSpeechTimer);
            lyricSpeechTimer = 0;
        }

        lyricSpeechActive = false;
        sendLive2dVocalLevel(false, getMusicTime(), {
            level: 0,
            bands: { low: 0, mid: 0, high: 0 },
            vowels: buildVowelShape('', null)
        });
        sendLive2dMusicState(false);
    }

    function stopTtsAudio() {
        if (ttsAnimationFrame) {
            window.cancelAnimationFrame(ttsAnimationFrame);
            ttsAnimationFrame = 0;
        }

        if (ttsAudio) {
            ttsAudio.onended = null;
            ttsAudio.onerror = null;
            ttsAudio.pause();
            ttsAudio.removeAttribute('src');
            ttsAudio.load();
            ttsAudio = null;
        }

        if (ttsAudioUrl) {
            URL.revokeObjectURL(ttsAudioUrl);
            ttsAudioUrl = '';
        }

        stopLyricSpeechMouth();
    }

    function startLyricSpeechMouth(detail) {
        stopLyricSpeechMouth();
        lyricSpeechActive = true;
        lyricSpeechStartedAt = window.performance.now();
        lyricSpeechDetail = detail;
        lyricSpeechText = getLyricSpeechText(detail);
        currentLyricDetail = {
            ...currentLyricDetail,
            ...detail
        };
        sendLive2dMusicState(true);

        const vowels = getLyricVowels(detail);
        const durationMs = Math.max(900, lyricSpeechText.length * 180);
        lyricSpeechTimer = window.setInterval(function () {
            const elapsedMs = window.performance.now() - lyricSpeechStartedAt;
            const progress = Math.min(0.999, Math.max(0, elapsedMs / durationMs));
            const vowel = vowels.length
                ? vowels[Math.min(vowels.length - 1, Math.floor(progress * vowels.length))]
                : '';
            const pulse = 0.55
                + Math.max(0, Math.sin(elapsedMs / 48)) * 0.3
                + Math.max(0, Math.sin(elapsedMs / 83)) * 0.15;
            const bands = {
                low: vowel === 'o' || vowel === 'u' ? 0.68 : 0.34,
                mid: vowel === 'a' || vowel === 'e' ? 0.7 : 0.48,
                high: vowel === 'i' || vowel === 'e' ? 0.78 : 0.42
            };

            sendLive2dVocalLevel(true, Number(detail?.timeStamp) || 0, {
                level: Math.min(0.92, pulse),
                bands,
                vowels: buildVowelShape(vowel, bands)
            });
        }, 70);
    }

    async function fetchMikaTtsAudio(text) {
        const response = await fetch(`${avatarBaseUrl}/api/tts/lyric`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ text })
        });

        if (!response.ok) {
            throw new Error(`Mika TTS failed: ${response.status}`);
        }

        return response.blob();
    }

    function ensureTtsAudioContext() {
        if (ttsAudioContext) {
            return ttsAudioContext;
        }

        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextClass) {
            return null;
        }

        ttsAudioContext = new AudioContextClass();
        return ttsAudioContext;
    }

    function readTtsAudioFeatures(analyser, data, audio, detail) {
        analyser.getByteFrequencyData(data);

        let total = 0;
        let low = 0;
        let mid = 0;
        let high = 0;
        const lowEnd = Math.max(2, Math.floor(data.length * 0.12));
        const midEnd = Math.max(lowEnd + 1, Math.floor(data.length * 0.36));

        for (let i = 0; i < data.length; i += 1) {
            const value = data[i] / 255;
            total += value;
            if (i < lowEnd) {
                low += value;
            } else if (i < midEnd) {
                mid += value;
            } else {
                high += value;
            }
        }

        const bands = {
            low: Math.min(1, low / lowEnd * 1.45),
            mid: Math.min(1, mid / (midEnd - lowEnd) * 1.8),
            high: Math.min(1, high / (data.length - midEnd) * 2.3)
        };
        const rawLevel = total / data.length;
        const level = Math.min(1, Math.max(0, rawLevel * 4.6));
        const duration = Number.isFinite(audio.duration) && audio.duration > 0
            ? audio.duration
            : Math.max(0.9, cleanText(detail?.speechText || detail?.japanese || '').length * 0.18);
        const vowels = getLyricVowels(detail);
        const progress = Math.min(0.999, Math.max(0, (audio.currentTime || 0) / duration));
        const vowel = vowels.length
            ? vowels[Math.min(vowels.length - 1, Math.floor(progress * vowels.length))]
            : '';

        return {
            level,
            bands,
            vowels: buildVowelShape(vowel, bands)
        };
    }

    function startTtsAudioMouth(audio, detail) {
        const audioContext = ensureTtsAudioContext();
        if (!audioContext) {
            startLyricSpeechMouth(detail);
            return;
        }
        if (audioContext.state === 'suspended' && typeof audioContext.resume === 'function') {
            audioContext.resume().catch(() => {});
        }

        const analyser = audioContext.createAnalyser();
        analyser.fftSize = 512;
        analyser.smoothingTimeConstant = 0.58;
        const source = audioContext.createMediaElementSource(audio);
        const lowpassFilter = audioContext.createBiquadFilter();
        lowpassFilter.type = 'lowpass';
        lowpassFilter.frequency.value = 7600;
        lowpassFilter.Q.value = 0.35;
        const compressor = audioContext.createDynamicsCompressor();
        compressor.threshold.value = -20;
        compressor.knee.value = 18;
        compressor.ratio.value = 3.5;
        compressor.attack.value = 0.004;
        compressor.release.value = 0.18;
        const outputGain = audioContext.createGain();
        outputGain.gain.value = 0.72;
        source.connect(analyser);
        analyser.connect(lowpassFilter);
        lowpassFilter.connect(compressor);
        compressor.connect(outputGain);
        outputGain.connect(audioContext.destination);
        const data = new Uint8Array(analyser.frequencyBinCount);

        lyricSpeechActive = true;
        lyricSpeechStartedAt = window.performance.now();
        lyricSpeechDetail = detail;
        lyricSpeechText = cleanText(detail?.speechText || detail?.japanese || '');
        currentLyricDetail = {
            ...currentLyricDetail,
            ...detail
        };
        sendLive2dMusicState(true);

        function tick() {
            if (!ttsAudio || ttsAudio !== audio || audio.ended) {
                return;
            }

            if (!audio.paused) {
                sendLive2dVocalLevel(true, Number(detail?.timeStamp) || 0, readTtsAudioFeatures(analyser, data, audio, detail));
            }
            ttsAnimationFrame = window.requestAnimationFrame(tick);
        }

        tick();
    }

    function speakLyricWithBrowserVoice(detail, text) {
        if (!window.speechSynthesis || typeof SpeechSynthesisUtterance === 'undefined') {
            if (messageEl) {
                messageEl.textContent = text;
            }
            startLyricSpeechMouth(detail);
            window.setTimeout(stopLyricSpeechMouth, Math.max(900, text.length * 180));
            return false;
        }

        window.speechSynthesis.cancel();
        const utterance = new SpeechSynthesisUtterance(text);
        const voice = getJapaneseSpeechVoice();
        if (voice) {
            utterance.voice = voice;
        }
        utterance.lang = voice?.lang || 'ja-JP';
        utterance.rate = 0.92;
        utterance.pitch = 1.55;
        utterance.volume = 0.85;
        utterance.onstart = function () {
            if (messageEl) {
                messageEl.textContent = text;
            }
            sendLive2dAction({ expressions: [Number(detail?.index || 0) % 8] });
            startLyricSpeechMouth(detail);
        };
        utterance.onend = stopLyricSpeechMouth;
        utterance.onerror = stopLyricSpeechMouth;
        window.speechSynthesis.speak(utterance);
        return true;
    }

    function speakLyric(detail) {
        const displayText = cleanText(detail?.japanese || '');
        const speechText = getSpeakableJapaneseText(detail?.japanese || '') || displayText;
        if (!speechText) {
            return false;
        }

        stopTtsAudio();
        window.speechSynthesis?.cancel?.();

        const speechDetail = {
            ...detail,
            speechText
        };

        fetchMikaTtsAudio(speechText)
            .then(function (audioBlob) {
                stopTtsAudio();
                ttsAudioUrl = URL.createObjectURL(audioBlob);
                ttsAudio = new Audio(ttsAudioUrl);
                ttsAudio.preload = 'auto';
                ttsAudio.volume = 0.88;
                ttsAudio.onended = stopTtsAudio;
                ttsAudio.onerror = function () {
                    stopTtsAudio();
                    speakLyricWithBrowserVoice(speechDetail, speechText);
                };
                if (messageEl) {
                    messageEl.textContent = displayText || speechText;
                }
                sendLive2dAction({ expressions: [Number(speechDetail?.index || 0) % 8] });
                startTtsAudioMouth(ttsAudio, speechDetail);
                return ttsAudio.play();
            })
            .catch(function () {
                stopTtsAudio();
                speakLyricWithBrowserVoice(speechDetail, speechText);
            });

        if (messageEl) {
            messageEl.textContent = displayText || speechText;
        }
        return true;
    }

    function isMusicPlaying() {
        if (typeof window.isPlaybackPaused === 'function') {
            return !window.isPlaybackPaused();
        }

        return false;
    }

    function getMusicTime() {
        if (typeof window.getPlaybackTime === 'function') {
            return Number(window.getPlaybackTime()) || 0;
        }

        return 0;
    }

    function getSongContext() {
        if (songContextOverride) {
            return songContextOverride;
        }

        try {
            if (typeof mikaAvatarSongContext !== 'undefined' && mikaAvatarSongContext) {
                return mikaAvatarSongContext;
            }
        } catch {
            return {};
        }

        return {};
    }

    function getSongContextKey(context) {
        return [
            context?.songUid || '',
            context?.title || '',
            context?.artist || '',
            context?.performer || ''
        ].map(value => String(value)).join('\u001f');
    }

    function estimateLyricDensity() {
        const lines = getLyricsData()
            .map(line => Number(line.TimeStamp))
            .filter(time => Number.isFinite(time))
            .sort((left, right) => left - right);
        if (lines.length < 3) {
            return 0.5;
        }

        const gaps = [];
        for (let i = 1; i < lines.length; i += 1) {
            const gap = lines[i] - lines[i - 1];
            if (gap >= 0.35 && gap <= 12) {
                gaps.push(gap);
            }
        }
        if (!gaps.length) {
            return 0.5;
        }

        const averageGap = gaps.reduce((sum, gap) => sum + gap, 0) / gaps.length;
        return Math.min(1, Math.max(0, (4.8 - averageGap) / 4.2));
    }

    const builtInChoreographedSongs = [
        {
            songUid: 'ea3d8e96-fb5c-4bff-bf47-a9683e844eff',
            titlePattern: /アイドル|idol/i,
            artistPattern: /yoasobi/i,
            choreographyId: 'yoasobi-idol-001',
            style: 'idol',
            tempo: 'medium',
            energy: 1.13,
            groove: 1.05,
            motionBias: 'balanced'
        }
    ];

    function compileCatalogPattern(value) {
        if (!value) {
            return null;
        }

        try {
            return new RegExp(String(value), 'i');
        } catch {
            return null;
        }
    }

    function normalizeCatalogSong(item) {
        if (!item || !item.choreographyId) {
            return null;
        }

        return {
            songUid: String(item.songUid || ''),
            titlePattern: item.titlePattern instanceof RegExp ? item.titlePattern : compileCatalogPattern(item.titlePattern),
            artistPattern: item.artistPattern instanceof RegExp ? item.artistPattern : compileCatalogPattern(item.artistPattern),
            choreographyId: String(item.choreographyId || ''),
            style: String(item.style || 'idol'),
            tempo: ['slow', 'medium', 'fast'].includes(item.tempo) ? item.tempo : 'medium',
            energy: Math.min(1.8, Math.max(0.55, Number(item.energy) || 1)),
            groove: Math.min(1.7, Math.max(0.45, Number(item.groove) || 1)),
            motionBias: String(item.motionBias || 'balanced')
        };
    }

    function getChoreographedSongs() {
        return choreographyCatalog?.length ? choreographyCatalog : builtInChoreographedSongs;
    }

    function loadChoreographyCatalog() {
        if (choreographyCatalogPromise || choreographyCatalogStatus === 'ready') {
            return choreographyCatalogPromise;
        }

        choreographyCatalogStatus = 'loading';
        choreographyCatalogPromise = fetch(`${avatarBaseUrl}/static/choreographies/catalog.json`, { cache: 'force-cache' })
            .then(response => {
                if (!response.ok) {
                    throw new Error(`HTTP ${response.status}`);
                }
                return response.json();
            })
            .then(data => {
                const songs = Array.isArray(data?.songs)
                    ? data.songs.map(normalizeCatalogSong).filter(Boolean)
                    : [];
                choreographyCatalog = songs;
                choreographyCatalogStatus = songs.length ? 'ready' : 'fallback';
                songDanceProfile = null;
                songDanceProfileContextKey = '';
                songProfileSentKey = '';
                sendLive2dSongProfile(true);
                return choreographyCatalog;
            })
            .catch(() => {
                choreographyCatalog = null;
                choreographyCatalogStatus = 'fallback';
                return null;
            });

        return choreographyCatalogPromise;
    }

    function findChoreographedSong(context, title, artist) {
        return getChoreographedSongs().find(item => {
            if (item.songUid && item.songUid === context.songUid) {
                return true;
            }

            const titleMatched = item.titlePattern ? item.titlePattern.test(title) : true;
            const artistMatched = item.artistPattern ? item.artistPattern.test(artist) : true;
            return titleMatched && artistMatched;
        }) || null;
    }

    function getSongDanceProfile() {
        const context = getSongContext();
        const contextKey = getSongContextKey(context);
        if (songDanceProfile && contextKey === songDanceProfileContextKey) {
            return songDanceProfile;
        }

        const title = String(context.title || '');
        const artist = String(context.artist || context.performer || '');
        const label = `${title} ${artist}`.toLowerCase();
        const density = estimateLyricDensity();
        const choreography = findChoreographedSong(context, title, artist);
        const tempo = density > 0.72 || /fast|疾走|dance|踊|party/.test(label)
            ? 'fast'
            : density < 0.28 || /ballad|バラード|slow|piano/.test(label)
                ? 'slow'
                : 'medium';

        songDanceProfile = {
            songUid: String(context.songUid || ''),
            title,
            artist,
            performer: String(context.performer || ''),
            choreographyId: choreography?.choreographyId || '',
            hasChoreography: Boolean(choreography),
            style: choreography?.style || 'native',
            tempo: choreography?.tempo || tempo,
            energy: choreography?.energy || 1,
            groove: choreography?.groove || 1,
            motionBias: choreography?.motionBias || 'native',
            lyricDensity: density
        };
        songDanceProfileContextKey = contextKey;
        rhythmState.density = density;
        rhythmState.beatIntervalMs = songDanceProfile.tempo === 'fast' ? 430 : songDanceProfile.tempo === 'slow' ? 720 : 560;
        return songDanceProfile;
    }

    function getVocalAnalysisAudioUrl() {
        const vocalsPlayer = document.getElementById('karaoke-audio-player-vocals');
        return vocalsPlayer?.currentSrc || vocalsPlayer?.src || '';
    }

    function ensureVocalAudioContext() {
        if (vocalAudioContext) {
            return vocalAudioContext;
        }

        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (!AudioContextClass) {
            vocalAnalysisFailed = true;
            return null;
        }

        vocalAudioContext = new AudioContextClass();
        return vocalAudioContext;
    }

    function ensureVocalBuffer() {
        const vocalsUrl = getVocalAnalysisAudioUrl();
        if (!vocalsUrl) {
            return null;
        }

        if (vocalAudioUrl && vocalAudioUrl !== vocalsUrl) {
            vocalAudioBuffer = null;
            vocalAudioDecodePromise = null;
            vocalDecodeStatus = 'idle';
            vocalAnalysisFailed = false;
        }

        if (vocalAnalysisFailed) {
            return null;
        }

        if (vocalAudioBuffer) {
            return vocalAudioBuffer;
        }

        if (vocalAudioDecodePromise) {
            return null;
        }

        const audioContext = ensureVocalAudioContext();
        if (!audioContext) {
            return null;
        }

        vocalDecodeStatus = 'loading';
        vocalAudioUrl = vocalsUrl;
        vocalDecodeStartedAt = window.performance.now();
        vocalAudioDecodePromise = fetch(vocalsUrl, { cache: 'force-cache' })
            .then(response => {
                if (!response.ok) {
                    throw new Error(`人聲音軌讀取失敗：${response.status}`);
                }
                return response.arrayBuffer();
            })
            .then(buffer => audioContext.decodeAudioData(buffer))
            .then(buffer => {
                vocalAudioBuffer = buffer;
                vocalDecodeStatus = 'ready';
                vocalDecodeFinishedAt = window.performance.now();
                return buffer;
            })
            .catch(error => {
                vocalAnalysisFailed = true;
                vocalDecodeStatus = 'error';
                vocalDecodeFinishedAt = window.performance.now();
                console.warn('Mika 人聲音量分析無法啟用: ', error);
                return null;
            });

        return null;
    }

    function warmVocalBuffer() {
        if (vocalAnalysisFailed || vocalAudioBuffer || vocalAudioDecodePromise) {
            return;
        }

        ensureVocalBuffer();
    }

    function scheduleVocalWarmup() {
        if (!getVocalAnalysisAudioUrl() || vocalWarmupTimer) {
            return;
        }

        vocalWarmupTimer = window.setTimeout(function () {
            vocalWarmupTimer = 0;
            warmVocalBuffer();
        }, 350);
    }

    function retryVocalWarmupAfterUserGesture() {
        const audioContext = vocalAudioContext;
        if (audioContext && audioContext.state === 'suspended' && typeof audioContext.resume === 'function') {
            audioContext.resume().catch(() => {});
        }

        warmVocalBuffer();
    }

    function getLyricsData() {
        try {
            if (typeof lyrics !== 'undefined' && Array.isArray(lyrics)) {
                return lyrics;
            }
        } catch {
            return [];
        }

        return [];
    }

    function getLyricDetailAtTime(currentTime) {
        const lines = getLyricsData();
        if (!lines.length || !Number.isFinite(currentTime)) {
            return currentLyricDetail;
        }

        let index = -1;
        for (let i = 0; i < lines.length; i += 1) {
            const time = Number(lines[i].TimeStamp);
            if (Number.isFinite(time) && time <= currentTime) {
                index = i;
            } else {
                break;
            }
        }

        if (index < 0) {
            return currentLyricDetail;
        }

        const line = lines[index] || {};
        const next = lines[index + 1] || null;
        return {
            ...currentLyricDetail,
            index,
            timeStamp: Number(line.TimeStamp) || 0,
            nextTimeStamp: next ? Number(next.TimeStamp) || 0 : 0,
            roman: line.Roman || '',
            japanese: line.Japanese || ''
        };
    }

    function extractKanaVowels(text) {
        const vowels = [];
        const kanaGroups = {
            a: 'あかがさざただなはばぱまやゃらわぁアカガサザタダナハバパマヤャラワァ',
            i: 'いきぎしじちぢにひびぴみりゐぃイキギシジチヂニヒビピミリヰィ',
            u: 'うくぐすずつづぬふぶぷむゆゅるゔぅウクグスズツヅヌフブプムユュルヴゥ',
            e: 'えけげせぜてでねへべぺめれゑぇエケゲセゼテデネヘベペメレヱェ',
            o: 'おこごそぞとどのほぼぽもよょろをぉオコゴソゾトドノホボポモヨョロヲォ'
        };
        let previous = '';

        for (const char of text || '') {
            if (char === 'ー' && previous) {
                vowels.push(previous);
                continue;
            }

            const vowel = Object.keys(kanaGroups).find(key => kanaGroups[key].includes(char));
            if (vowel) {
                vowels.push(vowel);
                previous = vowel;
            }
        }

        return vowels;
    }

    function extractRomanVowels(text) {
        return ((text || '').toLowerCase().match(/[aiueo]/g) || []);
    }

    function getLyricSpeechText(detail) {
        return detail?.speechText
            || getSpeakableJapaneseText(detail?.japanese || '')
            || cleanText(detail?.japanese || '');
    }

    function getLyricVowels(detail) {
        const kanaVowels = extractKanaVowels(getLyricSpeechText(detail));
        if (kanaVowels.length) {
            return kanaVowels;
        }

        return extractRomanVowels(detail?.roman || '');
    }

    function pickLyricVowel(currentTime) {
        const detail = getLyricDetailAtTime(currentTime);
        if (!detail) {
            return '';
        }

        const vowels = getLyricVowels(detail);
        if (!vowels.length) {
            return '';
        }

        const start = Number(detail.timeStamp) || 0;
        const end = Number(detail.nextTimeStamp) || start + 4;
        const span = Math.max(0.6, end - start);
        const progress = Math.min(0.999, Math.max(0, (currentTime - start) / span));
        return vowels[Math.min(vowels.length - 1, Math.floor(progress * vowels.length))];
    }

    function buildVowelShape(vowel, bands) {
        const shape = { a: 0, i: 0, u: 0, e: 0, o: 0 };
        if (shape[vowel] !== undefined) {
            shape[vowel] = 1;
        } else if (bands) {
            shape.a = Math.max(0.12, bands.mid * 0.75);
            shape.i = bands.high * 0.7;
            shape.u = bands.low * 0.45;
            shape.e = bands.high * 0.32 + bands.mid * 0.32;
            shape.o = bands.low * 0.75;
        } else {
            shape.a = 1;
        }

        const total = shape.a + shape.i + shape.u + shape.e + shape.o || 1;
        Object.keys(shape).forEach(key => {
            shape[key] /= total;
            smoothedVowels[key] += (shape[key] - smoothedVowels[key]) * 0.42;
        });

        return { ...smoothedVowels };
    }

    function fallbackSingingFeatures(currentTime) {
        const vowel = pickLyricVowel(currentTime);
        const phase = Number.isFinite(currentTime) ? currentTime : 0;
        const pulse = 0.5 + Math.max(0, Math.sin(phase * 11.5)) * 0.32 + Math.max(0, Math.sin(phase * 17.7)) * 0.18;
        const level = Math.min(0.82, Math.max(0.36, pulse));
        const bands = {
            low: vowel === 'o' || vowel === 'u' ? 0.72 : 0.42,
            mid: vowel === 'a' || vowel === 'e' ? 0.72 : 0.52,
            high: vowel === 'i' || vowel === 'e' ? 0.76 : 0.48
        };

        return {
            level,
            bands,
            vowels: buildVowelShape(vowel, bands)
        };
    }

    function readDecodedVocalFeatures(playing, currentTime) {
        if (!playing || !Number.isFinite(currentTime)) {
            vocalLevel *= 0.72;
            return {
                level: vocalLevel,
                bands: { low: 0, mid: 0, high: 0 },
                vowels: buildVowelShape('', null)
            };
        }

        const buffer = ensureVocalBuffer();
        if (!buffer) {
            const fallback = fallbackSingingFeatures(currentTime);
            vocalLevel += (fallback.level - vocalLevel) * 0.42;
            return {
                ...fallback,
                level: vocalLevel
            };
        }

        const channel = buffer.getChannelData(0);
        const sampleRate = buffer.sampleRate;
        const center = Math.max(0, Math.min(channel.length - 1, Math.floor(currentTime * sampleRate)));
        const windowSize = Math.min(2048, channel.length);
        const start = Math.max(0, Math.min(channel.length - windowSize, center - Math.floor(windowSize / 2)));
        let totalEnergy = 0;
        let lowEnergy = 0;
        let highEnergy = 0;
        let previous = channel[start] || 0;
        let lowAccumulator = previous;

        for (let i = 0; i < windowSize; i += 1) {
            const sample = channel[start + i] || 0;
            totalEnergy += sample * sample;
            lowAccumulator += (sample - lowAccumulator) * 0.08;
            lowEnergy += lowAccumulator * lowAccumulator;
            const diff = sample - previous;
            highEnergy += diff * diff;
            previous = sample;
        }

        const rms = Math.sqrt(totalEnergy / windowSize);
        const lowRms = Math.sqrt(lowEnergy / windowSize);
        const highRms = Math.sqrt(highEnergy / windowSize);
        const low = Math.min(1, lowRms * 7.5);
        const high = Math.min(1, highRms * 18);
        const mid = Math.min(1, Math.max(0, rms * 6.4 - low * 0.28 - high * 0.16));
        const target = Math.min(1, Math.sqrt(Math.max(0, rms - 0.012)) * 1.35);
        const smoothing = target > vocalLevel ? 0.5 : 0.22;
        vocalLevel += (target - vocalLevel) * smoothing;

        const bands = { low, mid, high };
        return {
            level: vocalLevel,
            bands,
            vowels: buildVowelShape(pickLyricVowel(currentTime), bands)
        };
    }

    function readVocalLevel(playing, currentTime) {
        try {
            return readDecodedVocalFeatures(playing, currentTime);
        } catch (error) {
            vocalAnalysisFailed = true;
            console.warn('Mika 人聲音量分析無法啟用: ', error);
            return {
                level: 0,
                bands: { low: 0, mid: 0, high: 0 },
                vowels: { a: 1, i: 0, u: 0, e: 0, o: 0 }
            };
        }
    }

    function startBeatLoop() {
        if (beatTimer) {
            return;
        }

        sendLive2dSongProfile(true);
        beatTimer = window.setInterval(function () {
            if (lyricSpeechActive) {
                return;
            }

            const playing = isMusicPlaying();
            const currentTime = getMusicTime();
            sendLive2dSongProfile(false);
            if (playing !== musicWasPlaying) {
                musicWasPlaying = playing;
                sendLive2dMusicState(playing, currentTime);
            }

            const now = window.performance.now();
            const features = readVocalLevel(playing, currentTime);
            if (now - lastVocalLevelAt >= 90) {
                lastVocalLevelAt = now;
                sendLive2dVocalLevel(playing, currentTime, features);
            }

            if (!playing) {
                return;
            }

            const profile = getSongDanceProfile();
            const targetInterval = profile.tempo === 'fast' ? 430 : profile.tempo === 'slow' ? 720 : 560;
            const levelDelta = features.level - rhythmState.emaLevel;
            rhythmState.emaLevel += (features.level - rhythmState.emaLevel) * 0.18;
            rhythmState.onsetScore = Math.max(0, levelDelta);
            rhythmState.lastLevel = features.level;
            rhythmState.beatIntervalMs += (targetInterval - rhythmState.beatIntervalMs) * 0.08;

            const elapsedSinceBeat = now - lastBeatAt;
            const onsetBeat = rhythmState.onsetScore > 0.075 && elapsedSinceBeat > rhythmState.beatIntervalMs * 0.52;
            const fallbackBeat = elapsedSinceBeat > rhythmState.beatIntervalMs * 1.05;
            if (!onsetBeat && !fallbackBeat) {
                return;
            }

            lastBeatAt = now;
            rhythmState.beatCount += 1;
            sendLive2dBeat(true, currentTime, Math.min(1.8, 0.82 + features.level * 0.48 + rhythmState.onsetScore * 1.8), {
                beatIntervalMs: rhythmState.beatIntervalMs,
                density: rhythmState.density,
                onset: rhythmState.onsetScore,
                beatCount: rhythmState.beatCount,
                tempo: profile.tempo,
                style: profile.style
            });
        }, 90);
    }

    function onLyricChange(detail) {
        if (!detail || typeof detail.index !== 'number') {
            return;
        }

        const key = `${detail.index}:${detail.timeStamp}`;
        if (key === lastLyricKey) {
            return;
        }
        lastLyricKey = key;

        currentLyricDetail = detail;

        const japanese = cleanText(detail.japanese);
        const chinese = cleanText(detail.chinese);
        const roman = cleanText(detail.roman);

        if (!japanese && !chinese && !roman) {
            return;
        }

        sendLive2dSongProfile(false);
        sendLive2dBeat(true, detail.timeStamp, 1.35, {
            beatIntervalMs: rhythmState.beatIntervalMs,
            density: rhythmState.density,
            phrase: 'lyric',
            beatCount: rhythmState.beatCount
        });
        sendLive2dAction({
            expressions: [detail.index % 8]
        });
    }

    connectButton?.addEventListener('click', function () {
        connect();
    });

    form?.addEventListener('submit', function (event) {
        event.preventDefault();
        const text = input?.value || '';
        if (input) {
            input.value = '';
        }
        sendText(text, { source: 'learnmore-user-input', clientUid: clientUid });
    });

    live2dFrame?.addEventListener('load', function () {
        songProfileSentKey = '';
        scheduleLive2dStatusTimeout();
    });

    window.addEventListener('message', function (event) {
        const data = event.data || {};
        const isLive2dStatus = data.type === 'mika-live2d-status';
        const isVrmStatus = data.type === 'mika-vrm-status';
        const isRuntimeStatus = data.type === 'mika-avatar-runtime-status';
        if (!isLive2dStatus && !isVrmStatus && !isRuntimeStatus) {
            return;
        }
        if (live2dFrame?.contentWindow && event.source !== live2dFrame.contentWindow) {
            return;
        }
        if (avatarOrigin && event.origin !== avatarOrigin) {
            return;
        }

        const expectedRuntime = panel.dataset.avatarRuntime || '';
        if (
            (isRuntimeStatus && data.runtime && expectedRuntime && data.runtime !== expectedRuntime)
            || (isLive2dStatus && expectedRuntime === 'vrm')
            || (isVrmStatus && expectedRuntime === 'live2d')
        ) {
            return;
        }

        if (isRuntimeStatus) {
            panel.dataset.live2dRuntime = data.runtime || '';
            panel.dataset.live2dAvatarId = data.avatarId || panel.dataset.avatarId || '';
            if (data.status) {
                panel.dataset.live2dState = data.status;
            }
            return;
        }

        clearLive2dStatusTimer();
        panel.dataset.live2dRuntime = isVrmStatus ? 'vrm' : 'live2d';
        panel.dataset.live2dState = isVrmStatus && data.status === 'stub' ? 'ready' : (data.status || 'unknown');
        panel.dataset.live2dVersion = data.version || '';
        panel.dataset.live2dFraming = data.framing || panel.dataset.avatarFraming || '';
        panel.dataset.live2dModelLoaded = isVrmStatus ? (data.modelReady ? 'true' : 'false') : (data.modelLoaded ? 'true' : 'false');
        panel.dataset.live2dFrameMode = data.modelFrame?.mode || '';
        panel.dataset.live2dFrameScale = Number.isFinite(Number(data.modelFrame?.scale))
            ? String(data.modelFrame.scale)
            : '';
        panel.dataset.live2dError = data.status === 'error' ? (data.message || 'unknown') : '';
        panel.dataset.live2dMusicPlaying = data.musicPlaying ? 'true' : 'false';
        panel.dataset.live2dSongTime = Number.isFinite(Number(data.songTime)) ? String(data.songTime) : '';
        const currentProfile = getSongDanceProfile();
        const choreography = data.choreography || {};
        panel.dataset.live2dSongTitle = data.songProfile?.title || currentProfile.title || '';
        panel.dataset.live2dSongArtist = data.songProfile?.artist || currentProfile.artist || '';
        panel.dataset.live2dChoreographyId = data.songProfile?.choreographyId || data.choreographyId || currentProfile.choreographyId || '';
        panel.dataset.live2dHasChoreography = data.songProfile?.hasChoreography || data.choreographyId ? 'true' : 'false';
        panel.dataset.live2dChoreographyLoadState = isVrmStatus ? (data.vrmClipLoadState || '') : (choreography.loadState || '');
        panel.dataset.live2dChoreographyLoadError = choreography.loadError || '';
        panel.dataset.live2dChoreographyTimelineId = isVrmStatus ? (data.vrmClipId || '') : (choreography.timelineId || '');
        panel.dataset.live2dChoreographyTimelineLoaded = isVrmStatus ? (data.vrmClipLoadState === 'ready' ? 'true' : 'false') : (choreography.timelineLoaded ? 'true' : 'false');
        panel.dataset.live2dExternalPreview = isVrmStatus && data.externalPreview ? 'true' : 'false';
        panel.dataset.live2dSignatureLoadState = data.signatureLoadState || choreography.signatureLoadState || '';
        panel.dataset.live2dSignatureLoadError = choreography.signatureLoadError || '';
        panel.dataset.live2dSongSection = isVrmStatus ? (data.section || '') : (choreography.section || '');
        panel.dataset.live2dSongSectionProgress = Number.isFinite(Number(data.choreography?.sectionProgress))
            ? String(data.choreography.sectionProgress)
            : '';
        panel.dataset.live2dSongPhraseAccent = choreography.phraseAccent || '';
        panel.dataset.live2dSongPhrasePlanType = choreography.phrasePlanType || '';
        panel.dataset.live2dSongCue = isVrmStatus ? (data.cue || '') : (choreography.cue || '');
        panel.dataset.live2dSongCueProgress = Number.isFinite(Number(data.choreography?.cueProgress))
            ? String(data.choreography.cueProgress)
            : '';
        panel.dataset.live2dSongLastCue = choreography.lastCue || '';
        panel.dataset.live2dSongLastCueTime = Number.isFinite(Number(data.choreography?.lastCueTime))
            ? String(data.choreography.lastCueTime)
            : '';
        panel.dataset.live2dSongCueExpression = Number.isInteger(Number(data.choreography?.cueExpression))
            ? String(data.choreography.cueExpression)
            : '';
        panel.dataset.live2dSongLastCueExpressionKey = data.choreography?.lastCueExpression?.key || '';
        panel.dataset.live2dSongLastCueExpressionIndex = Number.isInteger(Number(data.choreography?.lastCueExpression?.index))
            ? String(data.choreography.lastCueExpression.index)
            : '';
        panel.dataset.live2dSongCueMotionIndex = Number.isInteger(Number(data.choreography?.cueMotionIndex))
            ? String(data.choreography.cueMotionIndex)
            : '';
        panel.dataset.live2dSongLastCueMotionKey = data.choreography?.lastCueMotion?.key || '';
        panel.dataset.live2dSongLastCueMotionName = data.choreography?.lastCueMotion?.name || '';
        panel.dataset.live2dSongLastCueMotionIndex = Number.isInteger(Number(data.choreography?.lastCueMotion?.index))
            ? String(data.choreography.lastCueMotion.index)
            : '';
        panel.dataset.live2dSongSectionPose = data.choreography?.sectionPose || '';
        panel.dataset.live2dSongSectionPoseProgress = Number.isFinite(Number(data.choreography?.sectionPoseProgress))
            ? String(data.choreography.sectionPoseProgress)
            : '';
        panel.dataset.live2dNativeMotionSection = data.choreography?.nativeMotionSection || '';
        panel.dataset.live2dNativeMotionProfile = data.choreography?.nativeMotionProfile || '';
        panel.dataset.live2dNativeMotionSwitches = Number.isFinite(Number(data.choreography?.nativeMotionSwitches))
            ? String(data.choreography.nativeMotionSwitches)
            : '';
        panel.dataset.live2dNativeMotionIntervalMs = Number.isFinite(Number(data.choreography?.nativeMotionIntervalMs))
            ? String(data.choreography.nativeMotionIntervalMs)
            : '';
        panel.dataset.live2dCurrentNativeMotion = data.choreography?.currentNativeMotion || '';
        panel.dataset.live2dNativeMotionError = data.choreography?.nativeMotionError || '';
        if (data.status === 'ready' || data.status === 'stub') {
            const expectedProfile = getSongDanceProfile();
            const reportedProfile = data.songProfile || {};
            const profileMismatch = reportedProfile.songUid !== expectedProfile.songUid
                || reportedProfile.choreographyId !== expectedProfile.choreographyId
                || Boolean(reportedProfile.hasChoreography) !== Boolean(expectedProfile.hasChoreography);
            sendLive2dSongProfile(profileMismatch);
        }
    });

    window.learnMoreMikaAvatar = {
        connect,
        sendText,
        onLyricChange,
        speakLyric,
        refreshSongProfile: function (context) {
            if (context && typeof context === 'object') {
                songContextOverride = {
                    songUid: String(context.songUid || ''),
                    title: String(context.title || ''),
                    artist: String(context.artist || ''),
                    performer: String(context.performer || '')
                };
            } else if (context === null) {
                songContextOverride = null;
            }

            songDanceProfile = null;
            songDanceProfileContextKey = '';
            songProfileSentKey = '';
            sendLive2dSongProfile(true);
            return getSongDanceProfile();
        },
        switchAvatar: function (avatarId) {
            setActiveAvatar(avatarId, { persist: true });
        },
        stopLyricSpeech: function () {
            window.speechSynthesis?.cancel?.();
            stopLyricSpeechMouth();
        },
        warmVocalBuffer,
        getVocalDecodeState: function () {
            return {
                status: vocalDecodeStatus,
                hasBuffer: Boolean(vocalAudioBuffer),
                hasPromise: Boolean(vocalAudioDecodePromise),
                failed: vocalAnalysisFailed,
                startedAt: vocalDecodeStartedAt,
                finishedAt: vocalDecodeFinishedAt,
                audioUrl: vocalAudioUrl
            };
        },
        getDanceState: function () {
            return {
                songProfile: getSongDanceProfile(),
                choreographyCatalogStatus,
                choreographyCatalogCount: choreographyCatalog?.length || 0,
                rhythm: { ...rhythmState },
                musicPlaying: musicWasPlaying,
                lastBeatAt,
                lastVocalLevelAt,
                live2d: {
                    runtime: panel.dataset.live2dRuntime || '',
                    avatarId: panel.dataset.avatarId || '',
                    state: panel.dataset.live2dState || '',
                    version: panel.dataset.live2dVersion || '',
                    modelLoaded: panel.dataset.live2dModelLoaded || '',
                    choreographyId: panel.dataset.live2dChoreographyId || '',
                    hasChoreography: panel.dataset.live2dHasChoreography || '',
                    choreographyLoadState: panel.dataset.live2dChoreographyLoadState || '',
                    choreographyTimelineLoaded: panel.dataset.live2dChoreographyTimelineLoaded || '',
                    signatureLoadState: panel.dataset.live2dSignatureLoadState || '',
                    songSection: panel.dataset.live2dSongSection || '',
                    songPhraseAccent: panel.dataset.live2dSongPhraseAccent || '',
                    songCue: panel.dataset.live2dSongCue || '',
                    songCueProgress: panel.dataset.live2dSongCueProgress || '',
                    songLastCue: panel.dataset.live2dSongLastCue || '',
                    songLastCueTime: panel.dataset.live2dSongLastCueTime || '',
                    songCueExpression: panel.dataset.live2dSongCueExpression || '',
                    songLastCueExpressionKey: panel.dataset.live2dSongLastCueExpressionKey || '',
                    songLastCueExpressionIndex: panel.dataset.live2dSongLastCueExpressionIndex || '',
                    songCueMotionIndex: panel.dataset.live2dSongCueMotionIndex || '',
                    songLastCueMotionKey: panel.dataset.live2dSongLastCueMotionKey || '',
                    songLastCueMotionName: panel.dataset.live2dSongLastCueMotionName || '',
                    songLastCueMotionIndex: panel.dataset.live2dSongLastCueMotionIndex || '',
                    songSectionPose: panel.dataset.live2dSongSectionPose || '',
                    nativeMotionSection: panel.dataset.live2dNativeMotionSection || '',
                    nativeMotionProfile: panel.dataset.live2dNativeMotionProfile || '',
                    nativeMotionSwitches: panel.dataset.live2dNativeMotionSwitches || '',
                    nativeMotionIntervalMs: panel.dataset.live2dNativeMotionIntervalMs || '',
                    currentNativeMotion: panel.dataset.live2dCurrentNativeMotion || '',
                    nativeMotionError: panel.dataset.live2dNativeMotionError || ''
                }
            };
        }
    };

    bindAvatarChoiceButtons();
    loadAvatarCatalog()
        .catch(function (error) {
            panel.dataset.avatarCatalogStatus = 'error';
            panel.dataset.avatarCatalogError = error.message || String(error);
        })
        .finally(function () {
            setActiveAvatar(getStoredAvatarId(), { persist: false });
        });
    connect();
    loadChoreographyCatalog();
    scheduleVocalWarmup();
    window.addEventListener('pointerdown', retryVocalWarmupAfterUserGesture, { once: true, passive: true });
    window.addEventListener('keydown', retryVocalWarmupAfterUserGesture, { once: true });
    startBeatLoop();
})();
