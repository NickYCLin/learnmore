import SwiftUI

struct FavoriteGroup: Decodable, Identifiable, Hashable {
    let id: Int
    let name: String
    let songCount: Int
    var containsSong: Bool
}

struct FavoritesView: View {
    @Environment(AccountModel.self) private var account
    @State private var groups: [FavoriteGroup] = []
    @State private var error: String?
    @State private var loading = false
    @State private var showLogin = false
    @State private var showCreate = false
    @State private var newName = ""
    @State private var deleting: FavoriteGroup?

    var body: some View {
        NavigationStack {
            Group {
                if account.member == nil {
                    ContentUnavailableView {
                        Label("收藏你想練習的歌曲", systemImage: "heart")
                    } description: {
                        Text("登入後，網站與 App 的歌單會自動同步。")
                    } actions: {
                        Button("登入") { showLogin = true }
                    }
                } else {
                    List {
                        NavigationLink { FavoriteSongsView(group: nil) } label: {
                            Label("所有收藏歌曲", systemImage: "heart.fill")
                        }
                        Section("我的歌單") {
                            ForEach(groups) { group in
                                NavigationLink { FavoriteSongsView(group: group) } label: {
                                    HStack {
                                        Label(group.name, systemImage: "music.note.list")
                                        Spacer()
                                        Text("\(group.songCount) 首").foregroundStyle(.secondary)
                                    }
                                }
                                .swipeActions {
                                    Button("刪除", role: .destructive) { deleting = group }
                                }
                            }
                            if groups.isEmpty && !loading && error == nil { Text("建立歌單，整理想練習的歌曲。") }
                            Button("建立歌單", systemImage: "plus") { newName = ""; showCreate = true }
                                .disabled(loading)
                        }
                        if loading { ProgressView("載入歌單…") }
                        if let error {
                            Text(error).foregroundStyle(.secondary)
                            Button("重試") { Task { await load() } }
                        }
                    }.refreshable { await load() }
                }
            }
            .navigationTitle("我的收藏")
            .task(id: account.revision) { await load() }
            .sheet(isPresented: $showLogin) { LoginView(action: .login) }
            .alert("建立歌單", isPresented: $showCreate) {
                TextField("歌單名稱", text: $newName)
                Button("取消", role: .cancel) {}
                Button("建立") { Task { await create() } }.disabled(newName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .alert("刪除歌單？", isPresented: Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })) {
                Button("取消", role: .cancel) { deleting = nil }
                Button("刪除", role: .destructive) {
                    if let group = deleting { Task { await delete(group) } }
                }
            } message: { Text("歌單中的收藏關聯也會從網站移除。歌曲本身及其他歌單不受影響。") }
        }
    }

    private func load() async {
        guard let member = account.member else { groups = []; return }
        loading = true; error = nil
        defer { loading = false }
        do {
            let result: [FavoriteGroup] = try await account.request("favorites/groups")
            guard account.member?.id == member.id, !Task.isCancelled else { return }
            groups = result
        } catch { if !Task.isCancelled { self.error = error.localizedDescription } }
    }

    private func create() async {
        guard !loading else { return }
        loading = true
        do {
            let data = try JSONEncoder().encode(["name": newName.trimmingCharacters(in: .whitespacesAndNewlines)])
            let _: FavoriteGroup = try await account.request("favorites/groups", method: "POST", body: data)
            await load()
        } catch { self.error = error.localizedDescription }
        loading = false
    }

    private func delete(_ group: FavoriteGroup) async {
        deleting = nil; loading = true
        do { try await account.send("favorites/groups/\(group.id)", method: "DELETE"); await load() }
        catch { self.error = error.localizedDescription }
        loading = false
    }
}

struct FavoriteSongsView: View {
    let group: FavoriteGroup?
    @Environment(AccountModel.self) private var account
    @State private var songs: [Song] = []
    @State private var page = 0
    @State private var more = false
    @State private var loading = false
    @State private var error: String?
    @State private var generation = UUID()

    var body: some View {
        List {
            ForEach(songs) { song in
                NavigationLink(value: song) { SongSummary(song: song) }
            }
            if loading { ProgressView("載入收藏…") }
            if let error {
                Text(error).foregroundStyle(.secondary)
                Button("重試") { Task { await load(reset: songs.isEmpty) } }
            } else if more {
                Button("載入更多歌曲") { Task { await load(reset: false) } }.disabled(loading)
            }
        }
        .overlay {
            if songs.isEmpty && !loading && error == nil {
                ContentUnavailableView("還沒有收藏歌曲", systemImage: "heart", description: Text("在歌曲練習頁點選愛心，將歌曲加入歌單。"))
            }
        }
        .navigationTitle(group?.name ?? "所有收藏")
        .navigationDestination(for: Song.self) { SongView(song: $0) }
        .task(id: account.revision) { await load(reset: true) }
        .refreshable { await load(reset: true) }
    }

    private func load(reset: Bool) async {
        guard account.member != nil else { songs = []; return }
        if reset { generation = UUID(); songs = []; page = 0; more = false }
        else if loading { return }
        let current = generation
        loading = true; error = nil
        defer { if generation == current { loading = false } }
        do {
            var path = "favorites/songs?page=\(page + 1)"
            if let group { path += "&groupId=\(group.id)" }
            let result: SongPage = try await account.request(path)
            guard current == generation, account.member != nil, !Task.isCancelled else { return }
            let known = Set(songs.map(\.id))
            songs += result.songs.filter { !known.contains($0.id) }
            page += 1; more = result.hasMore
        } catch { if current == generation && !Task.isCancelled { self.error = error.localizedDescription } }
    }
}

struct FavoritePicker: View {
    let song: Song
    @Environment(AccountModel.self) private var account
    @Environment(\.dismiss) private var dismiss
    @State private var groups: [FavoriteGroup] = []
    @State private var loading = false
    @State private var error: String?
    @State private var showCreate = false
    @State private var name = ""

    var body: some View {
        NavigationStack {
            List {
                Section {
                    Text(song.title).font(.headline)
                    Text("選擇歌單即可同步到網站。再次點選可移除此歌單中的收藏。")
                        .font(.footnote).foregroundStyle(.secondary)
                }
                ForEach(groups) { group in
                    Button { Task { await toggle(group) } } label: {
                        HStack {
                            Text(group.name)
                            Spacer()
                            Image(systemName: group.containsSong ? "checkmark.circle.fill" : "circle")
                        }
                    }
                    .accessibilityValue(group.containsSong ? "已收藏" : "未收藏")
                    .disabled(loading)
                }
                Button("建立新歌單", systemImage: "plus") { name = ""; showCreate = true }.disabled(loading)
                if loading { ProgressView("同步中…") }
                if let error {
                    Text(error).foregroundStyle(.secondary)
                    Button("重試") { Task { await load() } }
                }
            }
            .navigationTitle("加入收藏")
            .toolbar { Button("完成") { dismiss() }.disabled(loading) }
            .task { await load() }
            .alert("建立歌單", isPresented: $showCreate) {
                TextField("歌單名稱", text: $name)
                Button("取消", role: .cancel) {}
                Button("建立並收藏") { Task { await create() } }
                    .disabled(name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
        }
        .interactiveDismissDisabled(loading)
    }

    private func load() async {
        loading = true; error = nil
        defer { loading = false }
        do { groups = try await account.request("favorites/groups?songId=" + song.id) }
        catch { self.error = error.localizedDescription }
    }

    private func toggle(_ group: FavoriteGroup) async {
        loading = true; error = nil
        defer { loading = false }
        do {
            try await account.send("favorites/groups/\(group.id)/songs/\(song.id)", method: group.containsSong ? "DELETE" : "PUT")
            account.revision = UUID()
            await load()
        } catch { self.error = error.localizedDescription }
    }

    private func create() async {
        loading = true; error = nil
        defer { loading = false }
        do {
            let data = try JSONEncoder().encode(["name": name.trimmingCharacters(in: .whitespacesAndNewlines)])
            let group: FavoriteGroup = try await account.request("favorites/groups", method: "POST", body: data)
            await toggle(group)
        } catch { self.error = error.localizedDescription }
    }
}

struct SongSummary: View {
    let song: Song
    var body: some View {
        HStack(spacing: 14) {
            AsyncImage(url: URL(string: song.thumbnailURL, relativeTo: Server.base)) { image in
                image.resizable().scaledToFill()
            } placeholder: {
                Image(systemName: "music.note").foregroundStyle(.secondary)
            }
            .frame(width: 56, height: 56).background(.quaternary)
            .clipShape(RoundedRectangle(cornerRadius: 10)).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 5) {
                Text(song.title).font(.headline)
                Text(song.artist).font(.subheadline).foregroundStyle(.secondary)
            }.padding(.vertical, 6)
        }
    }
}
