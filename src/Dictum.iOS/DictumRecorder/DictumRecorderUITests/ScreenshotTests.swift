import XCTest

/// Screenshots of the demo path (no experimental transcript) in light and then dark, as kept attachments in the
/// .xcresult. Each pass records and seals one short real meeting on the device, which then shows on the home screen.
@MainActor
final class ScreenshotTests: XCTestCase {
    override func setUp() {
        continueAfterFailure = false
    }

    func testScreenshots() {
        run(appearance: "light", prefix: "")
        run(appearance: "dark", prefix: "dark-")
    }

    private func run(appearance: String, prefix: String) {
        let app = XCUIApplication()
        // The stored transcript switch is overridden off for this launch (UserDefaults argument domain), so the test
        // never turns the experimental transcript on.
        app.launchArguments = ["-DictumAppearance", appearance, "-settings.transcribeOnDevice", "NO"]
        // A permission answered "Don't Allow" by an earlier interrupted run would block recording; ask afresh each pass.
        app.resetAuthorizationStatus(for: .microphone)
        let monitor = addUIInterruptionMonitor(withDescription: "Microphone permission") { alert in
            // firstMatch: the microphone alert can expose more than one "Allow" element (seen on the test simulator, 19/11/2026).
            for label in ["Allow", "OK"] where alert.buttons[label].firstMatch.exists {
                alert.buttons[label].firstMatch.tap()
                return true
            }
            return false
        }
        defer { removeUIInterruptionMonitor(monitor) }
        app.launch()

        let newMeeting = app.buttons["home.newMeeting"]
        XCTAssertTrue(newMeeting.waitForExistence(timeout: 10))
        shot(prefix + "01-home")

        app.buttons["home.settings"].tap()
        let back = app.buttons["settings.back"]
        XCTAssertTrue(back.waitForExistence(timeout: 5))
        shot(prefix + "02-settings")
        app.swipeUp()
        shot(prefix + "02b-settings-more")
        back.tap()

        XCTAssertTrue(newMeeting.waitForExistence(timeout: 5))
        newMeeting.tap()
        let start = app.buttons["consent.start"]
        XCTAssertTrue(start.waitForExistence(timeout: 5))
        shot(prefix + "03-consent")

        start.tap()
        let stop = app.buttons["recording.stop"]
        if !stop.waitForExistence(timeout: 3) {
            // The permission alert belongs to SpringBoard; an interaction lets the monitor handle it, else tap it here.
            app.tap()
            let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
            let allow = springboard.alerts.buttons["Allow"].firstMatch
            if allow.waitForExistence(timeout: 2) { allow.tap() }
            if !stop.waitForExistence(timeout: 3), start.exists { start.tap() }
        }
        XCTAssertTrue(stop.waitForExistence(timeout: 10))
        sleep(4) // so the level meter moves
        shot(prefix + "04-recording")

        stop.tap()
        let done = app.buttons["sealed.done"]
        let review = app.buttons["review.continue"]
        let deadline = Date().addingTimeInterval(20)
        while Date() < deadline, !done.exists, !review.exists { _ = done.waitForExistence(timeout: 0.5) }
        if review.exists {
            shot(prefix + "05-review")
            review.tap()
        }
        XCTAssertTrue(done.waitForExistence(timeout: 20))
        shot(prefix + "05-sealed")

        done.tap()
        XCTAssertTrue(newMeeting.waitForExistence(timeout: 5))
        shot(prefix + "06-home-after")
        app.terminate()
    }

    /// Records a short meeting, taps Stop, then closes the app WITHOUT tapping Done. The meeting's six words are kept as
    /// a text attachment named "code", so a check outside the test can open the kept record and see that the review was
    /// saved at Stop (scratch tool check-stop-without-done.sh). Until 27/11/2026 the record was written only at Done.
    func testStopWithoutDone() {
        let app = XCUIApplication()
        app.launchArguments = ["-DictumAppearance", "light", "-settings.transcribeOnDevice", "NO"]
        app.resetAuthorizationStatus(for: .microphone)
        let monitor = addUIInterruptionMonitor(withDescription: "Microphone permission") { alert in
            for label in ["Allow", "OK"] where alert.buttons[label].firstMatch.exists {
                alert.buttons[label].firstMatch.tap()
                return true
            }
            return false
        }
        defer { removeUIInterruptionMonitor(monitor) }
        app.launch()

        let newMeeting = app.buttons["home.newMeeting"]
        XCTAssertTrue(newMeeting.waitForExistence(timeout: 10))
        newMeeting.tap()
        let start = app.buttons["consent.start"]
        XCTAssertTrue(start.waitForExistence(timeout: 5))
        start.tap()
        let stop = app.buttons["recording.stop"]
        if !stop.waitForExistence(timeout: 3) {
            app.tap()
            let springboard = XCUIApplication(bundleIdentifier: "com.apple.springboard")
            let allow = springboard.alerts.buttons["Allow"].firstMatch
            if allow.waitForExistence(timeout: 2) { allow.tap() }
            if !stop.waitForExistence(timeout: 3), start.exists { start.tap() }
        }
        XCTAssertTrue(stop.waitForExistence(timeout: 10))
        sleep(3)
        stop.tap()

        let code = app.staticTexts["sealed.code"]
        XCTAssertTrue(code.waitForExistence(timeout: 20))
        let words = (code.value as? String) ?? ""
        XCTAssertEqual(words.split(separator: " ").count, 6, "the six words are readable on the sealed screen")
        let attachment = XCTAttachment(string: words)
        attachment.name = "code"
        attachment.lifetime = .keepAlways
        add(attachment)
        // No Done: the app is closed on the sealed screen.
        app.terminate()
    }

    private func shot(_ name: String) {
        let attachment = XCTAttachment(screenshot: XCUIScreen.main.screenshot())
        attachment.name = name
        attachment.lifetime = .keepAlways
        add(attachment)
    }
}
