import SwiftUI

struct SongView: View {
    let song: Song
    @State private var lyrics: [Lyric] = []
    @State private var loading = true
    @State private var error: String?
    @AppStorage("showChinese") private var showChinese = true
    @AppStorage("showRoman") private var showRoman = true
    @Environment(AccountModel.self) private var account
    @State private var showFavorites = false
    @State private var showLogin = false
    @AppStorage("followLyrics") private var followLyrics = true
    @State private var playback = PlaybackModel()
    @Environment(\.scenePhase) private var scenePhase
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private var activeLyricID: Int? {
        let timestamps = lyrics.map(\.seconds)
        guard let index = LyricTimeline.activeIndex(at: playback.seconds, timestamps: timestamps) else { return nil }
        return lyrics[index].id
    }

    var body: some View {
        ScrollViewReader { reader in
            List {
                Section {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(song.title).font(.title2.bold())
                        Text(song.artist).foregroundStyle(.secondary)
                    }
                    if let notice = playback.notice {
                        Label(notice, systemImage: "info.circle")
                            .font(.footnote)
                            .foregroundStyle(.secondary)
                    }
                }
                Section("閱讀設定") {
                    Toggle("歌詞自動捲動", isOn: $followLyrics)
                    Toggle("顯示中文翻譯", isOn: $showChinese)
                    Toggle("顯示羅馬拼音", isOn: $showRoman)
                }
                Section("歌詞") {
                    if loading {
                        ProgressView("載入歌詞…")
                    } else if let error {
                        ContentUnavailableView("無法載入歌詞", systemImage: "exclamationmark.triangle", description: Text(error))
                        Button("重試") { Task { await load() } }
                    } else if lyrics.isEmpty {
                        ContentUnavailableView("歌詞正在整理中", systemImage: "text.quote", description: Text("請稍後再試。"))
                    }
                    ForEach(lyrics) { lyric in
                        Button {
                            playback.seek(to: lyric.seconds)
                        } label: {
                            LyricRow(lyric: lyric, isActive: lyric.id == activeLyricID,
                                     showChinese: showChinese, showRoman: showRoman)
                        }
                        .buttonStyle(.plain)
                        .id(lyric.id)
                        .accessibilityHint("跳到 \(lyric.time)")
                    }
                }
            }
            .animation(reduceMotion ? nil : .easeInOut(duration: 0.15), value: activeLyricID)
            .onChange(of: activeLyricID) { _, id in
                guard let id, followLyrics, playback.ready else { return }
                withAnimation(reduceMotion ? nil : .easeInOut(duration: 0.25)) { reader.scrollTo(id, anchor: .center) }
            }
        }
        .safeAreaInset(edge: .top, spacing: 0) {
            player.frame(maxWidth: 640).padding(.horizontal).padding(.bottom, 8)
                .frame(maxWidth: .infinity).background(.background)
        }
        .navigationTitle("歌曲練習")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $showFavorites) { FavoritePicker(song: song) }
        .sheet(isPresented: $showLogin) { LoginView(action: .login) }
        .toolbar {
            Button("收藏", systemImage: "heart") {
                playback.pause()
                if account.member == nil { showLogin = true } else { showFavorites = true }
            }
            ShareLink(item: song.webURL, subject: Text(song.title), message: Text(song.artist))
                .simultaneousGesture(TapGesture().onEnded { playback.pause() })
        }
        .task { await load() }
        .onChange(of: scenePhase) { _, phase in
            if phase != .active { playback.pause() }
        }
        .onDisappear { playback.pause() }
        .onChange(of: account.member?.id) { _, id in
            if id == nil { showFavorites = false }
        }
    }

    @ViewBuilder
    private var player: some View {
        if let videoID = song.videoID {
            VStack(spacing: 8) {
                if let message = playback.error {
                    PlayerFailure(message: message, retry: playback.retry,
                                  videoURL: URL(string: "https://www.youtube.com/watch?v=\(videoID)"))
                        .frame(minHeight: 200)
                } else {
                    YouTubePlayer(videoID: videoID, playback: playback)
                        .id(playback.revision)
                        .frame(height: 220)
                    if !playback.ready { ProgressView("載入播放器…").font(.footnote) }
                }
            }

        } else {
            VStack(spacing: 12) {
                Image(systemName: "play.slash").font(.largeTitle).accessibilityHidden(true)
                Text("這首歌目前無法播放").font(.headline)
                Text("找不到有效的 YouTube 影片，仍可閱讀下方歌詞。")
                    .font(.footnote).foregroundStyle(.secondary)
            }
                .multilineTextAlignment(.center)
                .padding()
                .frame(minHeight: 200)
        }
    }

    private func load() async {
        loading = true
        error = nil
        defer { loading = false }
        do {
            let response = try await CatalogAPI().lyrics(songID: song.id)
            lyrics = response.sorted { ($0.seconds, $0.id) < ($1.seconds, $1.id) }
        } catch is CancellationError {
        } catch {
            self.error = error.localizedDescription
        }
    }
}

private struct LyricRow: View {
    let lyric: Lyric
    let isActive: Bool
    let showChinese: Bool
    let showRoman: Bool

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            Text(lyric.time)
                .font(.caption.monospacedDigit())
                .foregroundStyle(isActive ? Color.accentColor : Color.secondary)
                .frame(width: 38, alignment: .leading)
            VStack(alignment: .leading, spacing: 5) {
                Text(lyric.japanese).font(.title3)
                if showRoman && !lyric.roman.isEmpty {
                    Text(lyric.roman).font(.subheadline).foregroundStyle(.secondary)
                }
                if showChinese && !lyric.chinese.isEmpty {
                    Text(lyric.chinese).foregroundStyle(.secondary)
                }
            }
        }
        .padding(10)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(isActive ? Color.accentColor.opacity(0.15) : .clear,
                    in: RoundedRectangle(cornerRadius: 10))
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

private struct PlayerFailure: View {
    let message: String
    let retry: () -> Void
    let videoURL: URL?

    var body: some View {
        VStack(spacing: 12) {
            Label("播放器無法使用", systemImage: "play.slash").font(.headline)
            Text(message).font(.footnote).foregroundStyle(.secondary)
            HStack {
                Button("重試", action: retry)
                if let videoURL { Link("在 YouTube 開啟", destination: videoURL) }
            }.buttonStyle(.bordered)
        }
        .padding()
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
        .multilineTextAlignment(.center)
    }
}
