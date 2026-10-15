import SwiftUI
import AuthenticationServices
import CryptoKit
import GoogleSignInSwift

struct AccountView: View {
    @Environment(AccountModel.self) private var account
    @State private var action: AccountAction?
    @State private var confirmDelete = false
    @State private var confirmLogout = false

    var body: some View {
        NavigationStack {
            Form {
                if let member = account.member {
                    Section("我的帳號") {
                        Label(member.name, systemImage: "person.crop.circle")
                        Text(member.email).foregroundStyle(.secondary).textSelection(.enabled)
                        Text("已連結：" + member.providers.map { $0 == "apple" ? "Apple" : "Google" }.joined(separator: "、"))
                        Button("連結其他登入方式") { action = .link }
                            .disabled(account.busy || !account.providers.accountMutations)
                    }
                    Section {
                        Button("登出") { confirmLogout = true }.disabled(account.busy)
                        Button("刪除帳號", role: .destructive) { confirmDelete = true }
                            .disabled(account.busy || !account.providers.accountMutations)
                    } footer: {
                        Text("此帳號與網站共用。連結須先驗證原帳號，再驗證目標登入方式；刪除須重新驗證已連結的帳號。服務商未提供新鮮驗證時間時會拒絕操作，不會以電子郵件代替身分證明。")
                        Link("帳號與隱私問題", destination: Server.url("Mobile/Support"))
                    }
                } else {
                    if let notice = account.deletionNotice { Section { Text(notice) } }
                    Section {
                        Label("把喜歡的歌曲帶著走", systemImage: "heart.text.clipboard")
                            .font(.headline)
                        Text("登入後即可同步網站的收藏與歌單。歌曲搜尋和練習不需要登入。")
                        Button("登入或建立帳號") { action = .login }
                            .disabled(account.restoring)
                    }
                }
                if account.restoring || account.busy { ProgressView("處理中…") }
                if let error = account.error {
                    Section {
                        Text(error).foregroundStyle(.secondary)
                        Button("重新連線") { Task { await account.start() } }
                    }
                }
                Section("LearnMore") {
                    Text("用日文歌曲練習聽力、閱讀與跟唱。")
                    NavigationLink("隱私權政策") { PrivacyView() }
                    Link("支援與意見回饋", destination: Server.url("Mobile/Support"))
                    Text("版本 \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0")")
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle("帳號與設定")
            .sheet(item: $action, onDismiss: { account.resetProofs() }) { LoginView(action: $0) }
            .confirmationDialog("要登出這台裝置嗎？", isPresented: $confirmLogout, titleVisibility: .visible) {
                Button("登出", role: .destructive) { Task { await account.logout() } }
            }
            .alert("永久刪除 LearnMore 帳號？", isPresented: $confirmDelete) {
                Button("取消", role: .cancel) {}
                Button("繼續驗證身分", role: .destructive) { action = .delete }
            } message: {
                Text("這會刪除網站及 App 共用的帳號、所有歌單收藏、留言、個人資料與回報，並撤銷登入。你上傳的歌曲、歌詞與相關音軌也會刪除，其他人的歌單將不再能播放這些歌曲。下一步需以已連結的帳號重新登入；完成驗證後即執行刪除。")
            }
        }
    }
}

struct LoginView: View {
    let action: AccountAction
    @Environment(AccountModel.self) private var account
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var colorScheme
    @State private var nonce = ""
    @State private var authorizing = false
    @State private var localError: String?

    private func allows(_ provider: String) -> Bool {
        switch action {
        case .login: return true
        case .link:
            let linked = account.member?.providers.contains(provider) ?? false
            return account.awaitingTargetProof ? !linked : linked
        case .delete: return account.member?.providers.contains(provider) ?? false
        }
    }

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    Image(systemName: action == .delete ? "person.crop.circle.badge.minus" : "music.note.house")
                        .font(.system(size: 48)).foregroundStyle(.indigo).accessibilityHidden(true)
                    Text(action.title).font(.largeTitle.bold())
                    Text(action == .delete ? "請以此帳號已連結的登入方式重新驗證。驗證成功後，帳號與收藏將永久刪除。" :
                         action == .link ? (account.awaitingTargetProof ? "步驟 2/2：已取得原帳號授權，尚待伺服器驗證。請取得目標登入方式的授權。" : "步驟 1/2：先使用此帳號已連結的登入方式重新驗證。") :
                         "舊網站帳號若尚無服務商識別碼，需由支援確認原帳號所有權並核准一次性遷移；相同電子郵件不會自動合併。")
                        .foregroundStyle(.secondary)
                    if account.providers.apple && allows("apple") {
                        SignInWithAppleButton(.continue) { request in
                            nonce = UUID().uuidString + UUID().uuidString
                            request.requestedScopes = [.email, .fullName]
                            request.nonce = SHA256.hash(data: Data(nonce.utf8)).map { String(format: "%02x", $0) }.joined()
                            authorizing = true; localError = nil
                        } onCompletion: { result in
                            Task { await handleApple(result) }
                        }
                        .signInWithAppleButtonStyle(colorScheme == .dark ? .white : .black)
                        .frame(height: 50)
                        .disabled(authorizing || account.busy)
                    }
                    if account.providers.google && allows("google") {
                        GoogleSignInButton(scheme: colorScheme == .dark ? .dark : .light, style: .wide, state: .normal) {
                            Task {
                                authorizing = true; localError = nil
                                defer { authorizing = false }
                                do {
                                    let proof = try await account.googleProof()
                                    if await account.complete(proof, action: action) { dismiss() }
                                } catch {
                                    if (error as NSError).code != -5 { localError = error.localizedDescription }
                                }
                            }
                        }.frame(height: 50).disabled(authorizing || account.busy)
                    }
                    if !account.providers.google && !account.providers.apple {
                        Text("登入服務暫時無法使用，仍可瀏覽歌曲與練習。")
                        Button("重新連線") { Task { await account.start() } }
                    }
                    if action == .link && account.member?.providers.count == 2 {
                        Text("已連結 Apple 與 Google。")
                    }
                    if authorizing || account.busy { ProgressView("驗證中…") }
                    if let error = localError ?? account.error { Text(error).foregroundStyle(.red) }
                    NavigationLink("隱私權政策") { PrivacyView() }
                }.padding(24)
            }
            .toolbar { ToolbarItem(placement: .cancellationAction) { Button("關閉") { dismiss() }.disabled(account.busy) } }
            .interactiveDismissDisabled(account.busy)
        }
    }

    private func handleApple(_ result: Result<ASAuthorization, Error>) async {
        defer { authorizing = false }
        do {
            let authorization = try result.get()
            guard let credential = authorization.credential as? ASAuthorizationAppleIDCredential,
                  let data = credential.authorizationCode, let code = String(data: data, encoding: .utf8) else {
                throw MemberError.message("登入授權不完整，請重試。")
            }
            let name = credential.fullName.map { PersonNameComponentsFormatter().string(from: $0) }
            if await account.complete(LoginProof(provider: "apple", code: code, nonce: nonce, name: name), action: action) { dismiss() }
        } catch {
            if (error as? ASAuthorizationError)?.code != .canceled { localError = error.localizedDescription }
        }
    }
}

struct PrivacyView: View {
    var body: some View {
        List {
            Section("帳號與收藏") {
                Text("LearnMore 使用你授權提供的名稱、電子郵件及登入服務識別碼建立帳號，並將收藏與歌單儲存在與網站共用的伺服器。登入憑證保存在此裝置的 Keychain；登出會撤銷此裝置的登入。")
            }
            Section("YouTube 與第三方服務") {
                Text("影片透過 YouTube 官方播放器提供。載入播放器、封面或登入服務時，第三方可能取得 IP 位址、裝置及使用資訊，依其隱私政策處理。App 不下載影片或音訊，也不要求通訊錄、定位、相機或麥克風權限。")
                Link("Google 隱私權政策", destination: URL(string: "https://policies.google.com/privacy")!)
                Link("Apple 隱私權政策", destination: URL(string: "https://www.apple.com/legal/privacy/")!)
            }
            Section("刪除與聯絡") {
                Text("帳號連結與刪除需要服務商提供新鮮的重新驗證證明；缺少證明時會拒絕操作。刪除成功後，Apple 授權撤銷與檔案清理由背景佇列重試，不代表第三方已即時完成。若遇到限制請聯絡支援；聯絡或登出不等於資料已刪除。")
                Link("完整隱私權政策", destination: Server.url("Mobile/Privacy"))
                Link("支援與隱私問題", destination: Server.url("Mobile/Support"))
            }
        }.navigationTitle("隱私權政策").navigationBarTitleDisplayMode(.inline)
    }
}
