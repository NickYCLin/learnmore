import Foundation

// Also compiled directly into the iOS target; the Swift package tests this exact source.
public enum YouTubeVideoID {
    public static func parse(_ value: String) -> String? {
        func valid(_ id: String) -> String? {
            id.range(of: #"\A[A-Za-z0-9_-]{11}\z"#, options: .regularExpression) == nil ? nil : id
        }
        if let id = valid(value) { return id }
        guard let url = URLComponents(string: value),
              ["https", "http"].contains(url.scheme?.lowercased() ?? ""),
              url.user == nil, url.password == nil,
              let host = url.host?.lowercased() else { return nil }
        let parts = url.path.split(separator: "/").map(String.init)
        if host == "youtu.be", parts.count == 1 { return valid(parts[0]) }
        guard ["youtube.com", "www.youtube.com", "m.youtube.com", "youtube-nocookie.com", "www.youtube-nocookie.com"].contains(host) else { return nil }
        if url.path == "/watch" {
            return url.queryItems?.first(where: { $0.name == "v" })?.value.flatMap(valid)
        }
        if parts.count == 2, ["embed", "shorts", "live"].contains(parts[0]) { return valid(parts[1]) }
        return nil
    }
}

public enum LyricTimeline {
    // Input must be sorted numerically. Duplicate timestamps highlight the last matching line.
    public static func activeIndex(at seconds: Double, timestamps: [Double]) -> Int? {
        guard seconds.isFinite, seconds >= 0 else { return nil }
        var low = 0
        var high = timestamps.count
        while low < high {
            let mid = (low + high) / 2
            if timestamps[mid] <= seconds { low = mid + 1 } else { high = mid }
        }
        return low == 0 ? nil : low - 1
    }

    public static func timeLabel(_ seconds: Double) -> String {
        guard seconds.isFinite, seconds >= 0, seconds < Double(Int.max) else { return "–:––" }
        let total = Int(seconds)
        return String(format: "%d:%02d", total / 60, total % 60)
    }
}
