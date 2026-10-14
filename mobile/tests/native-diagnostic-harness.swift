import Foundation

enum MPRemoteCommandHandlerStatus { case success, commandFailed }
class MPRemoteCommandEvent {}
class MPChangePlaybackPositionCommandEvent: MPRemoteCommandEvent {
    let positionTime: Double
    init(_ seconds: Double) { positionTime = seconds }
}
class MPRemoteCommand {
    var isEnabled = false
    var handlers: [(MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus] = []
    func addTarget(handler: @escaping (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus) -> Any {
        handlers.append(handler); return handlers.count
    }
    func removeTarget(_ target: Any) { handlers.removeAll() }
    func send(_ event: MPRemoteCommandEvent = MPRemoteCommandEvent()) -> MPRemoteCommandHandlerStatus {
        handlers.first?(event) ?? .commandFailed
    }
}
class MPRemoteCommandCenter {
    static let center = MPRemoteCommandCenter()
    static func shared() -> MPRemoteCommandCenter { center }
    let playCommand = MPRemoteCommand(), pauseCommand = MPRemoteCommand()
    let togglePlayPauseCommand = MPRemoteCommand(), changePlaybackPositionCommand = MPRemoteCommand()
}
class MPNowPlayingInfoCenter {
    static let center = MPNowPlayingInfoCenter()
    static func `default`() -> MPNowPlayingInfoCenter { center }
    var nowPlayingInfo: [String: Any]?
}
let AVAudioSessionInterruptionTypeKey = "type"
let AVAudioSessionInterruptionOptionKey = "options"
class AVAudioSession {
    enum InterruptionType: UInt { case ended = 0, began = 1 }
    struct InterruptionOptions: OptionSet {
        let rawValue: UInt
        static let shouldResume = Self(rawValue: 1)
    }
    enum Category { case playback }
    enum Mode { case `default` }
    struct Options: OptionSet {
        let rawValue: UInt
        static let notifyOthersOnDeactivation = Self(rawValue: 1)
    }
    var failActivation = false
    func setCategory(_ category: Category, mode: Mode, options: Options) throws {}
    func setActive(_ active: Bool, options: Options = []) throws {
        if active && failActivation { throw NSError(domain: "session", code: 1) }
    }
}
struct CMTime {
    let seconds: Double
    init(seconds: Double, preferredTimescale: Int32) { self.seconds = seconds }
    static let zero = Self(seconds: 0, preferredTimescale: 600)
}
enum PlayerStatus { case ready, failed }
class PlayerItem { var status = PlayerStatus.ready }
class AVPlayer {
    var rate: Float = 0
    var status = PlayerStatus.ready
    var currentItem: PlayerItem? = PlayerItem()
    var position = 0.0
    func currentTime() -> CMTime { CMTime(seconds: position, preferredTimescale: 600) }
    func play() { rate = 1 }
    func pause() { rate = 0 }
    func replaceCurrentItem(with item: PlayerItem?) { currentItem = item }
    func removeTimeObserver(_ token: Any) {}
    func seek(to time: CMTime, toleranceBefore: CMTime, toleranceAfter: CMTime, completionHandler: @escaping (Bool) -> Void) {
        position = time.seconds; completionHandler(true)
    }
}
class Subject {
    let session = AVAudioSession()
    var player: AVPlayer? = AVPlayer()
    var generation = 0, revision = 0
    var desiredPlaying = false, interrupted = false, resumeAfterInterruption = false
    var status = "paused", errorMessage = ""
    let duration = 180.0
    var timeObserver: Any?, statusObserver: Any?, rateObserver: Any?
    var endObserver: NSObjectProtocol?, fixtureURL: URL?
    var commands: [(MPRemoteCommand, Any)] = []
    func publish() { revision += 1 }
METHODS_UNDER_TEST
    func run() throws {
        let center = MPRemoteCommandCenter.shared()
        installCommands()
        precondition(center.playCommand.send() == .success && player?.rate == 1)
        precondition(center.togglePlayPauseCommand.send() == .success && player?.rate == 0)
        precondition(center.togglePlayPauseCommand.send() == .success && player?.rate == 1)
        precondition(center.changePlaybackPositionCommand.send(MPChangePlaybackPositionCommandEvent(180)) == .success)
        precondition(player?.position == 180)
        precondition(center.changePlaybackPositionCommand.send(MPChangePlaybackPositionCommandEvent(180.1)) == .commandFailed)
        precondition(center.changePlaybackPositionCommand.send(MPChangePlaybackPositionCommandEvent(.nan)) == .commandFailed)
        player?.status = .failed
        precondition(center.playCommand.send() == .commandFailed)
        player?.status = .ready; player?.currentItem?.status = .failed
        precondition(center.playCommand.send() == .commandFailed)
        player?.currentItem?.status = .ready
        session.failActivation = true
        precondition(center.playCommand.send() == .commandFailed && !desiredPlaying)
        session.failActivation = false
        player?.position = 1
        try play()
        let began = Notification(name: Notification.Name("interruption"), userInfo: [AVAudioSessionInterruptionTypeKey: UInt(1)])
        let ended = Notification(name: Notification.Name("interruption"), userInfo: [AVAudioSessionInterruptionTypeKey: UInt(0), AVAudioSessionInterruptionOptionKey: UInt(1)])
        handleInterruption(began)
        precondition(center.playCommand.send() == .commandFailed)
        precondition(center.pauseCommand.send() == .commandFailed)
        precondition(center.togglePlayPauseCommand.send() == .commandFailed)
        precondition(center.changePlaybackPositionCommand.send(MPChangePlaybackPositionCommandEvent(2)) == .commandFailed)
        pausePlayback(); handleInterruption(ended)
        precondition(player?.rate == 0 && !desiredPlaying)
        try play(); handleInterruption(began); handleInterruption(ended)
        precondition(player?.rate == 1)
        handleInterruption(began)
        let stale = center.playCommand.handlers[0]
        stopPlayback()
        precondition(interrupted && player == nil)
        precondition(stale(MPRemoteCommandEvent()) == .commandFailed)
        precondition(commands.isEmpty && center.togglePlayPauseCommand.handlers.isEmpty && !center.togglePlayPauseCommand.isEnabled)
        handleInterruption(ended)
        precondition(!interrupted)
        player = AVPlayer(); try play()
        precondition(player?.rate == 1)
        precondition(stale(MPRemoteCommandEvent()) == .commandFailed)
        installCommands()
        let current = center.playCommand.handlers[0]
        player = nil
        precondition(current(MPRemoteCommandEvent()) == .commandFailed)
        player = AVPlayer()
        let url = try Self.generateFixture()
        defer { try? FileManager.default.removeItem(at: url) }
        let data = try Data(contentsOf: url)
        precondition(data.count == 44 + 8000 * 180 * 2)
        precondition(String(data: data.prefix(4), encoding: .utf8) == "RIFF")
        precondition(data[24] == 0x40 && data[25] == 0x1f)
    }
    func runOffMain() -> MPRemoteCommandHandlerStatus { remoteCommand(generation) { try self.play() } }
}
let subject = Subject()
try subject.run()
var finished = false
DispatchQueue.global().async {
    precondition(!Thread.isMainThread)
    precondition(subject.runOffMain() == .success)
    DispatchQueue.main.async { finished = true }
}
let deadline = Date().addingTimeInterval(10)
while !finished && Date() < deadline { RunLoop.main.run(until: Date().addingTimeInterval(0.01)) }
precondition(finished)
print("native method harness passed")
