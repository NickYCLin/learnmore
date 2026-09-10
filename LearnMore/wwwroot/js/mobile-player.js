(function () {
    const parents = new Set(['capacitor://localhost', 'http://localhost:5173', 'http://127.0.0.1:5173']);
    let player;
    let parentOrigin = '';
    let ready = false;
    function status(error) {
        if (!parentOrigin) return;
        window.parent.postMessage({ type: 'learnmore-player-status', ready,
            time: ready ? player.getCurrentTime() : 0,
            duration: ready ? player.getDuration() : 0,
            state: ready ? player.getPlayerState() : -1,
            error: error || '' }, parentOrigin);
    }
    window.onYouTubeIframeAPIReady = function () {
        player = new YT.Player('player', {
            videoId: document.body.dataset.videoId, width: '100%', height: '100%',
            playerVars: { playsinline: 1, controls: 1, origin: location.origin, widget_referrer: 'https://tw.learnmore.app' },
            events: { onReady: function () { ready = true; status(); }, onError: function (event) { status(String(event.data)); } }
        });
    };
    window.addEventListener('message', function (event) {
        if (event.source !== window.parent || !parents.has(event.origin) || event.data?.type !== 'learnmore-player-command') return;
        parentOrigin = event.origin;
        if (event.data.command === 'status') { status(); return; }
        if (!ready) return;
        if (event.data.command === 'pause') player.pauseVideo();
        if (event.data.command === 'play') player.playVideo();
        if (event.data.command === 'seek' && Number.isFinite(event.data.time) && event.data.time >= 0)
            player.seekTo(Math.min(event.data.time, player.getDuration()), true);
    });
    document.addEventListener('visibilitychange', function () { if (document.hidden && ready) player.pauseVideo(); });
    window.setInterval(function () { status(); }, 150);
})();
