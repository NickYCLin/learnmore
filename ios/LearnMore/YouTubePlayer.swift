import SwiftUI
import WebKit

@MainActor
@Observable
final class PlaybackModel {
    var seconds = 0.0
    var ready = false
    var error: String?
    var notice: String?
    var revision = UUID()
    @ObservationIgnored weak var webView: WKWebView?
    @ObservationIgnored private var timeout: Task<Void, Never>?

    func attach(_ view: WKWebView) {
        webView = view
        ready = false
        error = nil
        notice = nil
        seconds = 0
        timeout?.cancel()
        timeout = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(25)) } catch { return }
            guard let self, !self.ready else { return }
            self.fail("播放器載入逾時，請檢查網路後重試。")
        }
    }

    func receive(_ event: [String: Any]) {
        switch event["event"] as? String {
        case "ready":
            timeout?.cancel()
            ready = true
            error = nil
        case "time":
            if let value = event["value"] as? Double, value.isFinite, value >= 0 { seconds = value }
        case "blocked": notice = "請點影片中的播放按鈕開始播放。"
        case "error":
            switch event["value"] as? Int {
            case 100: fail("這部影片已移除或設為私人影片。")
            case 101, 150: fail("這部影片不允許在 App 內播放，可改用 YouTube 開啟。")
            case 153: fail("播放器無法驗證 App，請稍後再試。")
            default: fail("目前無法播放這部影片，請稍後再試。")
            }
        default: break
        }
    }

    func seek(to seconds: Double) {
        guard ready, seconds.isFinite, seconds >= 0 else { return }
        // Does not force playback: a paused video remains paused.
        webView?.evaluateJavaScript("player.seekTo(\(seconds), true);", completionHandler: nil)
    }

    func pause() {
        webView?.evaluateJavaScript("if (window.player && player.pauseVideo) player.pauseVideo();", completionHandler: nil)
    }

    func fail(_ message: String) {
        pause()
        timeout?.cancel()
        ready = false
        error = message
    }

    func detach(_ view: WKWebView) {
        guard webView === view else { return }
        timeout?.cancel()
        webView = nil
        ready = false
    }

    func retry() {
        pause()
        error = nil
        ready = false
        revision = UUID()
    }
}

struct YouTubePlayer: UIViewRepresentable {
    let videoID: String
    let playback: PlaybackModel

    func makeCoordinator() -> Coordinator { Coordinator(playback: playback) }

    func makeUIView(context: Context) -> WKWebView {
        let configuration = WKWebViewConfiguration()
        configuration.allowsInlineMediaPlayback = true
        configuration.allowsPictureInPictureMediaPlayback = false
        configuration.mediaTypesRequiringUserActionForPlayback = .all
        configuration.websiteDataStore = .nonPersistent()
        configuration.userContentController.add(context.coordinator, name: "playback")
        let view = WKWebView(frame: .zero, configuration: configuration)
        view.isOpaque = false
        view.backgroundColor = .black
        view.scrollView.isScrollEnabled = false
        view.navigationDelegate = context.coordinator
        view.uiDelegate = context.coordinator
        playback.attach(view)
        guard YouTubeVideoID.parse(videoID) == videoID,
              let bundleID = Bundle.main.bundleIdentifier?.lowercased(),
              let origin = URL(string: "https://\(bundleID)") else {
            playback.fail("無法辨識這首歌的影片。")
            return view
        }
        // YouTube requires the installed app's bundle ID as its client identity.
        let variables: [String: Any] = ["playsinline": 1, "controls": 1, "autoplay": 0, "origin": origin.absoluteString]
        guard let data = try? JSONSerialization.data(withJSONObject: variables),
              let encoded = String(data: data, encoding: .utf8) else {
            playback.fail("無法準備播放器。")
            return view
        }
        view.loadHTMLString("""
        <!doctype html><html><head>
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="strict-origin-when-cross-origin">
        <style>html,body,#player{margin:0;width:100%;height:100%;background:#000;overflow:hidden}</style>
        </head><body><div id="player"></div><script>
        var player, timer;
        function send(event,value){window.webkit.messageHandlers.playback.postMessage({event:event,value:value});}
        function onYouTubeIframeAPIReady(){
          player=new YT.Player('player',{width:'100%',height:'100%',videoId:'\(videoID)',
            playerVars:\(encoded),events:{
              onReady:function(){send('ready',0); timer=setInterval(function(){
                var t=player.getCurrentTime(); if(Number.isFinite(t)) send('time',t);
              },250);},
              onError:function(e){send('error',e.data);},
              onAutoplayBlocked:function(){send('blocked',0);}
            }});
        }
        document.addEventListener('visibilitychange',function(){
          if(document.hidden && player && player.pauseVideo) player.pauseVideo();
        });
        window.addEventListener('pagehide',function(){clearInterval(timer); if(player && player.destroy) player.destroy();});
        </script><script src="https://www.youtube.com/iframe_api" onerror="send('error',0)"></script></body></html>
        """, baseURL: origin)
        return view
    }

    func updateUIView(_ uiView: WKWebView, context: Context) {}

    static func dismantleUIView(_ view: WKWebView, coordinator: Coordinator) {
        view.evaluateJavaScript("clearInterval(timer); if(window.player && player.destroy) player.destroy();", completionHandler: nil)
        view.stopLoading()
        view.configuration.userContentController.removeScriptMessageHandler(forName: "playback")
        view.navigationDelegate = nil
        view.uiDelegate = nil
        coordinator.playback.detach(view)
    }

    final class Coordinator: NSObject, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate {
        let playback: PlaybackModel
        init(playback: PlaybackModel) { self.playback = playback }

        func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
            guard message.frameInfo.isMainFrame, message.webView === playback.webView,
                  let event = message.body as? [String: Any] else { return }
            playback.receive(event)
        }

        func webView(_ webView: WKWebView, decidePolicyFor navigationAction: WKNavigationAction,
                     decisionHandler: @escaping (WKNavigationActionPolicy) -> Void) {
            // The local wrapper must remain the only main-frame document with access to the bridge.
            let url = navigationAction.request.url
            let isWrapper = url?.host == Bundle.main.bundleIdentifier?.lowercased()
            if navigationAction.targetFrame?.isMainFrame == true,
               url?.absoluteString != "about:blank", !isWrapper {
                if navigationAction.navigationType == .linkActivated { openExternal(navigationAction.request.url) }
                decisionHandler(.cancel)
            } else { decisionHandler(.allow) }
        }

        func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                     for navigationAction: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
            openExternal(navigationAction.request.url)
            return nil
        }

        private func openExternal(_ url: URL?) {
            guard let url, url.scheme == "https" else { return }
            playback.pause()
            UIApplication.shared.open(url)
        }

        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
            if (error as NSError).code != NSURLErrorCancelled { playback.fail("播放器連線失敗，請重試。") }
        }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
            if (error as NSError).code != NSURLErrorCancelled { playback.fail("播放器連線中斷，請重試。") }
        }
        func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
            playback.fail("播放器已中斷，請重試。")
        }
    }
}
