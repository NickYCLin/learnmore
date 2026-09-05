// Compiled out of Release. Never used for App Store screenshots or review credentials.
import Foundation
#if DEBUG

final class UITestURLProtocol: URLProtocol {
    static var enabled: Bool { ProcessInfo.processInfo.arguments.contains("--ui-testing") }
    override class func canInit(with request: URLRequest) -> Bool { enabled }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let path = request.url?.path ?? ""
        var status = 200
        let payload: String
        if path.hasSuffix("/auth/providers") {
            payload = #"{"google":false,"apple":false}"#
        } else if path.hasSuffix("/lyrics") {
            payload = #"[{"id":1,"seconds":0,"japanese":"こんにちは","chinese":"你好","roman":"konnichiwa"},{"id":2,"seconds":5,"japanese":"また明日","chinese":"明天見","roman":"mata ashita"}]"#
        } else if path.hasSuffix("/songs") {
            if ProcessInfo.processInfo.arguments.contains("--ui-test-catalog-failure") {
                status = 503; payload = #"{"error":"Test service unavailable"}"#
            } else {
                payload = #"{"songs":[{"id":"test-original","title":"練習用原創例句","artist":"LearnMore 測試資料","thumbnailURL":"","videoURL":""}],"hasMore":false}"#
            }
        } else { status = 404; payload = "{}" }
        let response = HTTPURLResponse(url: request.url!, statusCode: status, httpVersion: "HTTP/1.1", headerFields: ["Content-Type":"application/json"])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(payload.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
#endif

enum AppNetwork {
    static func configuration() -> URLSessionConfiguration {
        let config = URLSessionConfiguration.ephemeral
        #if DEBUG
        if UITestURLProtocol.enabled { config.protocolClasses = [UITestURLProtocol.self] }
        #endif
        return config
    }
    static let catalogSession = URLSession(configuration: configuration())
}
