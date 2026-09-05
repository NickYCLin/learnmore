import SwiftUI
import AuthenticationServices
import CryptoKit
import GoogleSignIn
import Security

struct Member: Codable, Equatable {
    let id: Int
    let name: String
    let email: String
    let providers: [String]
}

struct LoginProviders: Decodable {
    var google = false
    var apple = false
}

struct LoginProof: Encodable {
    let provider: String
    let code: String
    var nonce: String? = nil
    var name: String? = nil
}

private struct LoginSession: Decodable {
    let token: String
    let user: Member
}

enum MemberError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case let .message(text) = self { return text }; return nil }
}

enum AccountAction: String, Identifiable {
    case login, link, delete
    var id: String { rawValue }
    var title: String {
        switch self { case .login: "登入 LearnMore"; case .link: "連結登入方式"; case .delete: "確認刪除帳號" }
    }
}

enum SessionKeychain {
    private static var query: [String: Any] { [kSecClass as String: kSecClassGenericPassword,
        kSecAttrService as String: Bundle.main.bundleIdentifier ?? "LearnMore",
        kSecAttrAccount as String: "mobile-session-v1"] }

    static func read() throws -> String? {
        var request = query
        request[kSecReturnData as String] = true
        request[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        let status = SecItemCopyMatching(request as CFDictionary, &result)
        if status == errSecItemNotFound { return nil }
        guard status == errSecSuccess, let data = result as? Data, let token = String(data: data, encoding: .utf8) else {
            throw MemberError.message("無法讀取安全登入資料，請解鎖裝置後重試。")
        }
        return token
    }

    static func save(_ token: String) throws {
        let attributes: [String: Any] = [kSecValueData as String: Data(token.utf8),
            kSecAttrAccessible as String: kSecAttrAccessibleWhenUnlockedThisDeviceOnly]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            status = SecItemAdd(query.merging(attributes) { _, new in new } as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw MemberError.message("無法安全儲存登入資料，請重試。") }
    }

    static func clear() { SecItemDelete(query as CFDictionary) }
}

private final class NoRedirects: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

@MainActor
@Observable
final class AccountModel {
    var member: Member?
    var providers = LoginProviders()
    var busy = false
    var restoring = true
    var error: String?
    var revision = UUID()
    @ObservationIgnored private var token: String?
    @ObservationIgnored private let network = URLSession(configuration: AppNetwork.configuration(), delegate: NoRedirects(), delegateQueue: nil)

    func start() async {
        restoring = true
        defer { restoring = false }
        error = nil
        do {
            #if DEBUG
            if UITestURLProtocol.enabled { SessionKeychain.clear() }
            #endif
            token = try SessionKeychain.read()
            providers = try await request("auth/providers", authenticated: false)
            if token != nil { member = try await request("account") }
        } catch { self.error = error.localizedDescription }
    }

    func request<T: Decodable>(_ path: String, method: String = "GET", body: Data? = nil,
                                authenticated: Bool = true) async throws -> T {
        let data = try await send(path, method: method, body: body, authenticated: authenticated)
        do { return try JSONDecoder().decode(T.self, from: data) }
        catch { throw MemberError.message("無法讀取服務回應，請稍後重試。") }
    }

    @discardableResult
    func send(_ path: String, method: String, body: Data? = nil, authenticated: Bool = true) async throws -> Data {
        guard let url = URL(string: "api/mobile/v1/" + path, relativeTo: Server.base)?.absoluteURL,
              url.scheme == "https", url.host == Server.base.host else { throw CatalogError.invalidResponse }
        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 25
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if authenticated {
            guard let token else { throw MemberError.message("請先登入。") }
            request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        if let body { request.httpBody = body; request.setValue("application/json", forHTTPHeaderField: "Content-Type") }
        let (data, response) = try await network.data(for: request)
        guard let response = response as? HTTPURLResponse else { throw CatalogError.invalidResponse }
        if response.statusCode == 401 && authenticated {
            clear()
            throw MemberError.message("登入已過期，請重新登入。")
        }
        guard (200...299).contains(response.statusCode) else {
            let message = (try? JSONSerialization.jsonObject(with: data) as? [String: String])?["error"]
            throw MemberError.message(message ?? (response.statusCode == 429 ? "操作次數較多，請稍候一分鐘再試。" : "服務暫時無法使用，請稍後再試。"))
        }
        return data
    }

    func complete(_ proof: LoginProof, action: AccountAction) async -> Bool {
        guard !busy else { return false }
        busy = true; error = nil
        defer { busy = false }
        do {
            let data = try JSONEncoder().encode(proof)
            switch action {
            case .login:
                let session: LoginSession = try await request("auth", method: "POST", body: data, authenticated: false)
                do { try SessionKeychain.save(session.token) }
                catch {
                    // Revoke a newly issued session if secure storage fails.
                    token = session.token
                    _ = try? await send("account/logout", method: "POST")
                    clear()
                    throw error
                }
                token = session.token; member = session.user
            case .link:
                try await send("account/link", method: "POST", body: data)
                member = try await request("account")
            case .delete:
                try await send("account/delete", method: "POST", body: data)
                try? await GIDSignIn.sharedInstance.disconnect()
                clear()
            }
            revision = UUID()
            return true
        } catch { self.error = error.localizedDescription; return false }
    }

    func googleProof() async throws -> LoginProof {
        guard let clientID = Bundle.main.object(forInfoDictionaryKey: "GIDClientID") as? String,
              clientID.hasSuffix(".apps.googleusercontent.com"),
              let serverID = Bundle.main.object(forInfoDictionaryKey: "GIDServerClientID") as? String,
              serverID.hasSuffix(".apps.googleusercontent.com") else {
            throw MemberError.message("Google 登入暫時無法使用，請稍後再試。")
        }
        guard let scene = UIApplication.shared.connectedScenes.first(where: { $0.activationState == .foregroundActive }) as? UIWindowScene,
              var presenter = scene.windows.first(where: \.isKeyWindow)?.rootViewController else {
            throw MemberError.message("無法開啟登入畫面，請重試。")
        }
        while let presented = presenter.presentedViewController { presenter = presented }
        GIDSignIn.sharedInstance.configuration = GIDConfiguration(clientID: clientID, serverClientID: serverID)
        let result = try await GIDSignIn.sharedInstance.signIn(withPresenting: presenter)
        guard let code = result.serverAuthCode else { throw MemberError.message("登入授權不完整，請重新登入。") }
        return LoginProof(provider: "google", code: code)
    }

    func logout() async {
        guard !busy else { return }
        busy = true; error = nil
        defer { busy = false }
        do { try await send("account/logout", method: "POST"); clear() }
        catch { self.error = error.localizedDescription }
    }

    private func clear() {
        token = nil; member = nil; SessionKeychain.clear(); GIDSignIn.sharedInstance.signOut(); revision = UUID()
    }
}
