import XCTest

final class LearnMoreUITests: XCTestCase {
    override func setUpWithError() throws { continueAfterFailure = false }

    private func launch(_ args: [String] = []) -> XCUIApplication {
        let app = XCUIApplication()
        app.launchArguments = ["--ui-testing", "-AppleLanguages", "(zh-Hant)", "-AppleLocale", "zh_TW",
                               "-showChinese", "YES", "-showRoman", "YES", "-followLyrics", "YES"] + args
        app.launch()
        return app
    }

    func testGuestCanReadLyricsAndAdjustReadingWithoutLogin() {
        let app = launch()
        let song = app.staticTexts["練習用原創例句"]
        XCTAssertTrue(song.waitForExistence(timeout: 20))
        song.tap()
        XCTAssertTrue(app.navigationBars["歌曲練習"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.switches["歌詞自動捲動"].exists)
        let chinese = app.switches["顯示中文翻譯"]
        XCTAssertTrue(chinese.exists)
        chinese.tap()
        app.swipeUp()
        XCTAssertTrue(app.buttons.matching(NSPredicate(format: "label CONTAINS %@", "こんにちは")).firstMatch.waitForExistence(timeout: 5))
        let capture = XCTAttachment(screenshot: app.screenshot())
        capture.name = "Internal QA — guest lyrics (test fixtures)"; capture.lifetime = .keepAlways
        add(capture)
    }

    func testFavoritesRequireLoginButPrivacyDoesNot() {
        let app = launch()
        app.tabBars.buttons["收藏"].tap()
        XCTAssertTrue(app.staticTexts["收藏你想練習的歌曲"].waitForExistence(timeout: 10))
        app.buttons["登入"].tap()
        XCTAssertTrue(app.staticTexts["登入 LearnMore"].waitForExistence(timeout: 10))
        app.buttons["關閉"].tap()
        app.tabBars.buttons["帳號"].tap()
        XCTAssertFalse(app.buttons["刪除帳號"].exists)
        app.buttons["隱私權政策"].tap()
        XCTAssertTrue(app.navigationBars["隱私權政策"].waitForExistence(timeout: 10))
        XCTAssertTrue(app.staticTexts["帳號與收藏"].exists)
    }

    func testCatalogFailureOffersRetryAndKeepsNavigationUsable() {
        let app = launch(["--ui-test-catalog-failure"])
        XCTAssertTrue(app.buttons["重試"].waitForExistence(timeout: 20))
        app.buttons["重試"].tap()
        XCTAssertTrue(app.buttons["重試"].waitForExistence(timeout: 10))
        app.tabBars.buttons["帳號"].tap()
        XCTAssertTrue(app.buttons["登入或建立帳號"].waitForExistence(timeout: 10))
    }
}
