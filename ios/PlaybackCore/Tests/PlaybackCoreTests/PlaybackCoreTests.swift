import XCTest
@testable import PlaybackCore

final class PlaybackCoreTests: XCTestCase {
    func testSupportedVideoLinks() {
        let id = "OHAjc-ayhus"
        for value in [id, "https://www.youtube.com/watch?v=\(id)&list=123", "https://youtu.be/\(id)?si=abc",
                      "https://www.youtube-nocookie.com/embed/\(id)", "https://m.youtube.com/shorts/\(id)",
                      "https://youtube.com/live/\(id)"] {
            XCTAssertEqual(YouTubeVideoID.parse(value), id, value)
        }
    }

    func testUntrustedAndMalformedVideoLinks() {
        for value in ["", "short", "OHAjc-ayhus\n", "https://youtube.com.evil.example/watch?v=OHAjc-ayhus",
                      "https://evil.example/watch?v=OHAjc-ayhus", "https://youtube.com@evil.example/watch?v=OHAjc-ayhus",
                      "javascript:alert(1)", "https://youtube.com/watch?v=</script>", "https://youtu.be/OHAjc-ayhus/extra"] {
            XCTAssertNil(YouTubeVideoID.parse(value), value)
        }
    }

    func testSeekingForwardAndBackwardAcrossBoundaries() {
        let times: [Double] = [2, 4.5, 9]
        XCTAssertNil(LyricTimeline.activeIndex(at: 1.99, timestamps: times))
        XCTAssertEqual(LyricTimeline.activeIndex(at: 2, timestamps: times), 0)
        XCTAssertEqual(LyricTimeline.activeIndex(at: 10, timestamps: times), 2)
        XCTAssertEqual(LyricTimeline.activeIndex(at: 4.5, timestamps: times), 1)
        XCTAssertEqual(LyricTimeline.activeIndex(at: 3, timestamps: times), 0)
    }

    func testEmptyDuplicateAndInvalidPlaybackTimes() {
        XCTAssertNil(LyricTimeline.activeIndex(at: 0, timestamps: []))
        XCTAssertEqual(LyricTimeline.activeIndex(at: 2, timestamps: [0, 2, 2, 3]), 2)
        for value in [Double.nan, .infinity, -1] {
            XCTAssertNil(LyricTimeline.activeIndex(at: value, timestamps: [0, 2]))
            XCTAssertEqual(LyricTimeline.timeLabel(value), "–:––")
        }
        XCTAssertEqual(LyricTimeline.timeLabel(65.9), "1:05")
    }
}
