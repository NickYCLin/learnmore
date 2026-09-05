import SwiftUI
import GoogleSignIn

enum Server {
    // Keep the deployment subpath and trailing slash. No database credentials belong in the app.
    static let base = URL(string: Bundle.main.object(forInfoDictionaryKey: "LearnMoreServerURL") as? String
        ?? "https://magicplus-design.serveirc.com/LearnMore/")!
    static func url(_ path: String) -> URL { base.appendingPathComponent(path) }
}

struct Song: Decodable, Identifiable, Hashable {
    let id: String
    let title: String
    let artist: String
    let thumbnailURL: String
    let videoURL: String?
    var videoID: String? { videoURL.flatMap(YouTubeVideoID.parse) }
    var webURL: URL { Server.url("Lyrics").appendingPathComponent(id) }
}

struct SongPage: Decodable {
    let songs: [Song]
    let hasMore: Bool
}

struct Lyric: Decodable, Identifiable {
    let id: Int
    let seconds: Double
    let japanese: String
    let chinese: String
    let roman: String
    var time: String { LyricTimeline.timeLabel(seconds) }
}

enum CatalogError: LocalizedError {
    case unavailable, missingAPI, invalidResponse
    var errorDescription: String? {
        switch self {
        case .unavailable: "目前無法取得資料，請稍後再試。"
        case .missingAPI: "歌曲服務尚未開放，請稍後再試。"
        case .invalidResponse: "無法讀取歌曲資料，請稍後再試。"
        }
    }
}

struct CatalogAPI {
    func get<T: Decodable>(_ path: String, query: [URLQueryItem] = []) async throws -> T {
        var components = URLComponents(url: Server.url(path), resolvingAgainstBaseURL: false)!
        components.queryItems = query.isEmpty ? nil : query
        var request = URLRequest(url: components.url!)
        request.timeoutInterval = 20
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (data, response) = try await AppNetwork.catalogSession.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw CatalogError.invalidResponse }
        if response.statusCode == 404 { throw CatalogError.missingAPI }
        guard response.statusCode == 200 else { throw CatalogError.unavailable }
        guard response.mimeType == "application/json" else { throw CatalogError.invalidResponse }
        return try JSONDecoder().decode(T.self, from: data)
    }

    func songs(query: String, page: Int) async throws -> SongPage {
        try await get("api/mobile/v1/songs", query: [
            URLQueryItem(name: "q", value: query),
            URLQueryItem(name: "page", value: String(page))
        ])
    }

    func lyrics(songID: String) async throws -> [Lyric] {
        try await get("api/mobile/v1/songs/\(songID)/lyrics")
    }
}

@MainActor
@Observable
final class CatalogModel {
    var songs: [Song] = []
    var loading = false
    var error: String?
    var hasMore = false
    private var query = ""
    private var page = 0
    private var generation = UUID()

    func search(_ query: String) async {
        let token = UUID()
        generation = token
        self.query = query
        songs = []
        page = 0
        hasMore = false
        loading = true
        error = nil
        do {
            try await Task.sleep(for: .milliseconds(300))
            let result = try await CatalogAPI().songs(query: query, page: 1)
            guard generation == token, !Task.isCancelled else { return }
            songs = result.songs
            hasMore = result.hasMore
            page = 1
        } catch {
            guard generation == token else { return }
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
        if generation == token { loading = false }
    }

    func loadMore() async {
        guard !loading, hasMore else { return }
        let token = generation
        loading = true
        error = nil
        do {
            let result = try await CatalogAPI().songs(query: query, page: page + 1)
            guard generation == token, !Task.isCancelled else { return }
            let known = Set(songs.map(\.id))
            songs += result.songs.filter { !known.contains($0.id) }
            page += 1
            hasMore = result.hasMore
        } catch {
            guard generation == token else { return }
            if !Task.isCancelled { self.error = error.localizedDescription }
        }
        if generation == token { loading = false }
    }
}

@main
struct LearnMoreApp: App {
    @State private var account = AccountModel()

    init() {
        #if DEBUG
        if UITestURLProtocol.enabled {
            // Set stored defaults, not launch-argument overrides, so UI tests can change them.
            for key in ["showChinese", "showRoman", "followLyrics"] {
                UserDefaults.standard.set(true, forKey: key)
            }
        }
        #endif
    }

    var body: some Scene {
        WindowGroup {
            TabView {
                CatalogView().tabItem { Label("歌曲", systemImage: "music.note.list") }
                FavoritesView().tabItem { Label("收藏", systemImage: "heart") }
                AccountView().tabItem { Label("帳號", systemImage: "person.crop.circle") }
            }
            .tint(.indigo)
            .environment(account)
            .task { await account.start() }
            .onOpenURL { GIDSignIn.sharedInstance.handle($0) }
        }
    }
}

struct CatalogView: View {
    @State private var model = CatalogModel()
    @State private var query = ""

    var body: some View {
        NavigationStack {
            List {
                ForEach(model.songs) { song in
                    NavigationLink(value: song) {
                        SongSummary(song: song)
                    }
                }
                if let error = model.error {
                    VStack(alignment: .leading, spacing: 12) {
                        Text(error).foregroundStyle(.secondary)
                        Button("重試") {
                            Task {
                                if model.songs.isEmpty { await model.search(query) }
                                else { await model.loadMore() }
                            }
                        }
                    }
                }
                if model.loading { ProgressView("載入歌曲…").frame(maxWidth: .infinity) }
                else if model.hasMore {
                    Button("載入更多歌曲") { Task { await model.loadMore() } }
                }
            }
            .overlay {
                if !model.loading && model.error == nil && model.songs.isEmpty {
                    ContentUnavailableView("沒有找到歌曲", systemImage: "magnifyingglass", description: Text("試試其他歌名或歌手。"))
                }
            }
            .navigationTitle("探索歌曲")
            .searchable(text: $query, prompt: "搜尋歌名或歌手")
            .task(id: query) { await model.search(query) }
            .refreshable { await model.search(query) }
            .navigationDestination(for: Song.self) { SongView(song: $0) }
        }
    }
}
