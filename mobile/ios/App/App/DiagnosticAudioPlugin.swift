import AVFoundation
import Capacitor
import MediaPlayer

class LearnMoreBridgeViewController: CAPBridgeViewController {
    override func capacitorDidLoad() {
        bridge?.registerPluginInstance(DiagnosticAudioPlugin())
    }
}

@objc(DiagnosticAudioPlugin)
public class DiagnosticAudioPlugin: CAPPlugin, CAPBridgedPlugin {
    public let identifier = "DiagnosticAudioPlugin"
    public let jsName = "DiagnosticAudio"
    public let pluginMethods: [CAPPluginMethod] = [
        CAPPluginMethod(name: "start", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "pause", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "stop", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "seek", returnType: CAPPluginReturnPromise),
        CAPPluginMethod(name: "getState", returnType: CAPPluginReturnPromise)
    ]
    private let session = AVAudioSession.sharedInstance()
    private var player: AVPlayer?
    private var timeObserver: Any?
    private var statusObserver: NSKeyValueObservation?
    private var rateObserver: NSKeyValueObservation?
    private var observers: [NSObjectProtocol] = []
    private var endObserver: NSObjectProtocol?
    private var commands: [(MPRemoteCommand, Any)] = []
    private var fixtureURL: URL?
    private var revision = 0
    private var generation = 0
    private var status = "stopped"
    private var errorMessage = ""
    private var desiredPlaying = false
    private var interrupted = false
    private var resumeAfterInterruption = false
    private let duration = 180.0
    private var enabled: Bool {
        guard let url = Bundle.main.url(forResource: "native-audio-diagnostic", withExtension: "json", subdirectory: "public"),
              let data = try? Data(contentsOf: url),
              let config = try? JSONSerialization.jsonObject(with: data) as? [String: Bool] else { return false }
        return config["enabled"] == true
    }

    public override func load() {
        observers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.interruptionNotification, object: session, queue: .main) { [weak self] note in
            self?.handleInterruption(note)
        })
        observers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.routeChangeNotification, object: session, queue: .main) { [weak self] note in
            guard self?.player != nil,
                  let reason = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt,
                  reason == AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue else { return }
            self?.pausePlayback()
        })
        observers.append(NotificationCenter.default.addObserver(forName: AVAudioSession.mediaServicesWereResetNotification, object: session, queue: .main) { [weak self] _ in
            guard self?.player != nil else { return }
            self?.stopPlayback()
        })
    }

    deinit {
        if let timeObserver = timeObserver { player?.removeTimeObserver(timeObserver) }
        observers.forEach { NotificationCenter.default.removeObserver($0) }
        if let endObserver = endObserver { NotificationCenter.default.removeObserver(endObserver) }
        commands.forEach { $0.0.removeTarget($0.1) }
        player?.pause()
        if player != nil {
            try? session.setActive(false, options: .notifyOthersOnDeactivation)
            MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        }
        if let fixtureURL = fixtureURL { try? FileManager.default.removeItem(at: fixtureURL) }
    }

    private func perform(_ call: CAPPluginCall, resolveImmediately: Bool = true, action: @escaping () throws -> Void) {
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { call.reject("診斷播放器已釋放", "DISPOSED"); return }
            guard self.enabled else { call.reject("原生音訊診斷建置旗標未開啟", "DISABLED"); return }
            do { try action(); if resolveImmediately { call.resolve(self.snapshot()) } }
            catch { call.reject(error.localizedDescription, "DIAGNOSTIC_AUDIO") }
        }
    }

    @objc public func start(_ call: CAPPluginCall) {
        perform(call, resolveImmediately: false) { [weak self] in
            guard let self = self else { return }
            guard call.getString("testId") == "tone-220-180s" else { throw self.failure("僅接受內建測試 ID") }
            guard !self.interrupted else { throw self.failure("音訊中斷中") }
            guard self.player != nil else {
                self.stopPlayback()
                let currentGeneration = self.generation
                self.status = "loading"; self.publish()
                DispatchQueue.global(qos: .userInitiated).async {
                    let result = Result { try Self.generateFixture() }
                    DispatchQueue.main.async { [weak self] in
                        guard let self = self, self.generation == currentGeneration else {
                            if case .success(let url) = result { try? FileManager.default.removeItem(at: url) }
                            call.reject("測試訊號載入已取消", "CANCELLED"); return
                        }
                        do {
                            self.fixtureURL = try result.get()
                            try self.prepare()
                            try self.play()
                            call.resolve(self.snapshot())
                        } catch {
                            self.stopPlayback(); self.status = "error"
                            self.errorMessage = error.localizedDescription; self.publish()
                            call.reject(error.localizedDescription, "DIAGNOSTIC_AUDIO")
                        }
                    }
                }
                return
            }
            try self.play()
            call.resolve(self.snapshot())
        }
    }

    @objc public func pause(_ call: CAPPluginCall) { perform(call) { [weak self] in self?.pausePlayback() } }
    @objc public func stop(_ call: CAPPluginCall) { perform(call) { [weak self] in self?.stopPlayback() } }
    @objc public func getState(_ call: CAPPluginCall) { perform(call) { [weak self] in self?.publish() } }
    @objc public func seek(_ call: CAPPluginCall) {
        perform(call) { [weak self] in
            guard let self = self, let seconds = call.getDouble("seconds"), seconds.isFinite,
                  seconds >= 0, seconds <= self.duration, self.player != nil else { throw NSError(domain: "DiagnosticAudio", code: 1, userInfo: [NSLocalizedDescriptionKey: "進度必須介於 0 至 180 秒且已載入測試訊號"]) }
            self.seekPlayback(seconds)
        }
    }

    private func failure(_ message: String) -> NSError {
        NSError(domain: "DiagnosticAudio", code: 1, userInfo: [NSLocalizedDescriptionKey: message])
    }

    private static func generateFixture() throws -> URL {
        let duration = 180.0
        let sampleRate = 8000, sampleCount = sampleRate * Int(duration)
        var data = Data()
        data.reserveCapacity(44 + sampleCount * 2)
        func text(_ value: String) { data.append(contentsOf: value.utf8) }
        func integer<Value: FixedWidthInteger>(_ value: Value) {
            var littleEndian = value.littleEndian
            withUnsafeBytes(of: &littleEndian) { data.append(contentsOf: $0) }
        }
        text("RIFF"); integer(UInt32(36 + sampleCount * 2)); text("WAVEfmt ")
        integer(UInt32(16)); integer(UInt16(1)); integer(UInt16(1)); integer(UInt32(sampleRate))
        integer(UInt32(sampleRate * 2)); integer(UInt16(2)); integer(UInt16(16))
        text("data"); integer(UInt32(sampleCount * 2))
        for sample in 0..<sampleCount {
            let seconds = Double(sample) / Double(sampleRate)
            let fade = min(1.0, min(seconds / 0.1, (duration - seconds) / 0.1))
            integer(Int16(sin(2 * Double.pi * 220 * seconds) * 32767 * 0.025 * fade))
        }
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("learnmore-diagnostic-\(UUID().uuidString).wav")
        try data.write(to: url, options: .atomic)
        return url
    }

    private func prepare() throws {
        guard let fixtureURL = fixtureURL else { throw failure("無法產生測試訊號") }
        let item = AVPlayerItem(url: fixtureURL)
        let currentPlayer = AVPlayer(playerItem: item)
        currentPlayer.volume = 0.3
        player = currentPlayer; status = "loading"; errorMessage = ""
        let currentGeneration = generation
        timeObserver = currentPlayer.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.25, preferredTimescale: 600), queue: .main) { [weak self] _ in
            guard let self = self, self.generation == currentGeneration else { return }
            self.publish()
        }
        statusObserver = item.observe(\.status, options: [.initial, .new]) { [weak self] item, _ in
            DispatchQueue.main.async {
                guard let self = self, self.generation == currentGeneration else { return }
                if item.status == .failed {
                    let message = item.error?.localizedDescription ?? "測試訊號無法播放"
                    self.stopPlayback(); self.status = "error"; self.errorMessage = message; self.publish()
                }
            }
        }
        rateObserver = currentPlayer.observe(\.timeControlStatus, options: [.new]) { [weak self] currentPlayer, _ in
            DispatchQueue.main.async {
                guard let self = self, self.generation == currentGeneration else { return }
                if self.desiredPlaying && !self.interrupted {
                    self.status = currentPlayer.timeControlStatus == .playing ? "playing" : "loading"
                }
                self.publish()
            }
        }
        endObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            guard let self = self, self.generation == currentGeneration else { return }
            self.pausePlayback(); self.status = "ended"; self.publish()
        }
        installCommands()
    }

    private func play() throws {
        guard !interrupted, let player = player else { throw failure("音訊中斷中或未載入") }
        guard player.status != .failed, player.currentItem?.status != .failed else { throw failure("測試訊號無法播放") }
        try session.setCategory(.playback, mode: .default, options: [])
        try session.setActive(true)
        desiredPlaying = true; resumeAfterInterruption = false; status = "loading"
        if player.currentTime().seconds >= duration - 0.05 { seekPlayback(0, resume: true) }
        else { player.play() }
        publish()
    }

    private func pausePlayback() {
        if player == nil && status == "loading" { generation += 1; status = "stopped" }
        desiredPlaying = false; resumeAfterInterruption = false
        player?.pause()
        if player != nil { status = "paused" }
        try? session.setActive(false, options: .notifyOthersOnDeactivation)
        publish()
    }

    private func stopPlayback() {
        generation += 1
        pausePlayback()
        if let timeObserver = timeObserver { player?.removeTimeObserver(timeObserver) }
        timeObserver = nil; statusObserver = nil; rateObserver = nil
        if let endObserver = endObserver { NotificationCenter.default.removeObserver(endObserver) }
        endObserver = nil
        player?.replaceCurrentItem(with: nil); player = nil
        commands.forEach { $0.0.removeTarget($0.1); $0.0.isEnabled = false }; commands.removeAll()
        if let fixtureURL = fixtureURL { try? FileManager.default.removeItem(at: fixtureURL) }
        fixtureURL = nil; status = "stopped"; errorMessage = ""
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        publish()
    }

    private func seekPlayback(_ seconds: Double, resume: Bool = false) {
        let currentGeneration = generation
        player?.seek(to: CMTime(seconds: seconds, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            DispatchQueue.main.async {
                guard let self = self, self.generation == currentGeneration else { return }
                if resume && self.desiredPlaying && !self.interrupted { self.player?.play() }
                if !self.desiredPlaying { self.status = "paused" }
                self.publish()
            }
        }
    }

    private func handleInterruption(_ note: Notification) {
        guard let value = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt,
              let type = AVAudioSession.InterruptionType(rawValue: value) else { return }
        if type == .began {
            resumeAfterInterruption = desiredPlaying && (player?.rate ?? 0) > 0
            interrupted = true; player?.pause()
            if player != nil { status = "interrupted" }; publish()
        } else {
            interrupted = false
            let options = AVAudioSession.InterruptionOptions(rawValue: note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0)
            let resume = resumeAfterInterruption && desiredPlaying && options.contains(.shouldResume)
            resumeAfterInterruption = false
            if resume {
                do { try play() } catch { pausePlayback(); errorMessage = error.localizedDescription; publish() }
            } else { pausePlayback() }
        }
    }

    private func remoteCommand(_ currentGeneration: Int, action: @escaping () throws -> Void) -> MPRemoteCommandHandlerStatus {
        let execute = { () -> MPRemoteCommandHandlerStatus in
            guard self.generation == currentGeneration, self.player != nil, !self.interrupted else { return .commandFailed }
            do { try action(); return .success }
            catch {
                self.pausePlayback(); self.errorMessage = error.localizedDescription; self.publish()
                return .commandFailed
            }
        }
        if Thread.isMainThread { return execute() }
        return DispatchQueue.main.sync(execute: execute)
    }

    private func installCommands() {
        let center = MPRemoteCommandCenter.shared()
        let currentGeneration = generation
        let playTarget = center.playCommand.addTarget { [weak self] _ in
            guard let self = self else { return .commandFailed }
            return self.remoteCommand(currentGeneration) { try self.play() }
        }
        let pauseTarget = center.pauseCommand.addTarget { [weak self] _ in
            guard let self = self else { return .commandFailed }
            return self.remoteCommand(currentGeneration) { self.pausePlayback() }
        }
        let toggleTarget = center.togglePlayPauseCommand.addTarget { [weak self] _ in
            guard let self = self else { return .commandFailed }
            return self.remoteCommand(currentGeneration) {
                if self.desiredPlaying { self.pausePlayback() }
                else { try self.play() }
            }
        }
        let seekTarget = center.changePlaybackPositionCommand.addTarget { [weak self] event in
            guard let self = self, let event = event as? MPChangePlaybackPositionCommandEvent, event.positionTime.isFinite,
                  event.positionTime >= 0, event.positionTime <= self.duration else { return .commandFailed }
            return self.remoteCommand(currentGeneration) {
                self.seekPlayback(event.positionTime)
            }
        }
        commands = [(center.playCommand, playTarget), (center.pauseCommand, pauseTarget), (center.togglePlayPauseCommand, toggleTarget), (center.changePlaybackPositionCommand, seekTarget)]
        commands.forEach { $0.0.isEnabled = true }
    }

    private func snapshot() -> [String: Any] {
        let seconds = player?.currentTime().seconds ?? 0
        return ["revision": revision, "status": status, "seconds": seconds.isFinite ? max(0, min(duration, seconds)) : 0,
                "duration": player == nil ? 0 : duration,
                "rate": player?.timeControlStatus == .playing ? (player?.rate ?? 0) : 0, "error": errorMessage]
    }

    private func publish() {
        revision += 1
        let state = snapshot()
        if player != nil {
            MPNowPlayingInfoCenter.default().nowPlayingInfo = [
                MPMediaItemPropertyTitle: "LearnMore 自產診斷訊號（非歌曲）",
                MPMediaItemPropertyPlaybackDuration: duration,
                MPNowPlayingInfoPropertyElapsedPlaybackTime: state["seconds"] ?? 0,
                MPNowPlayingInfoPropertyPlaybackRate: state["rate"] ?? 0
            ]
        }
        notifyListeners("stateChange", data: state)
    }
}
