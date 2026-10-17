import XCTest
@testable import DikteCore

final class HotkeyMachineTests: XCTestCase {
    let lctrl = Key.lctrl, ropt = Key.ropt, lopt = Key.lopt, fn = Key.fn, space = Key.space, esc = Key.escape
    let d = 2, a = 0, f1 = 122, f8 = 100

    func fnMachine() -> HotkeyMachine { HotkeyMachine(spec: try! HotkeySpec.parse("fn"), lockKey: Key.space) }
    func ctrlOpt() -> HotkeyMachine { HotkeyMachine(spec: try! HotkeySpec.parse("ctrl+opt"), lockKey: Key.space) }

    func testHoldThenReleaseRecordsAndSends() {
        let m = fnMachine()
        let start = m.feed(fn, isDown: true)
        XCTAssertEqual(start.command, .start)
        XCTAssertFalse(start.swallow) // a modifier: apps keep seeing it
        XCTAssertTrue(m.isRecording)
        XCTAssertEqual(m.feed(fn, isDown: true).command, .none) // repeated flagsChanged
        XCTAssertEqual(m.feed(fn, isDown: false).command, .stopAndSend)
        XCTAssertFalse(m.isRecording)
    }

    func testEitherOrderAndEitherSideStarts() {
        let m = ctrlOpt()
        m.feed(ropt, isDown: true)
        XCTAssertEqual(m.feed(lctrl, isDown: true).command, .start)
        XCTAssertEqual(m.feed(lctrl, isDown: false).command, .stopAndSend)
    }

    func testAShortcutThatStartsWithTheHotkeyCancelsAndPassesThrough() {
        let m = fnMachine()
        m.feed(fn, isDown: true)
        let r = m.feed(f1, isDown: true) // fn+F1: the real F1
        XCTAssertEqual(r.command, .cancel)
        XCTAssertFalse(r.swallow)
        XCTAssertFalse(m.isRecording)
        // Still holding fn: no restart until it is released and pressed again.
        XCTAssertEqual(m.feed(f1, isDown: false).command, .none)
        XCTAssertEqual(m.feed(fn, isDown: false).command, .none)
        XCTAssertEqual(m.feed(fn, isDown: true).command, .start)
    }

    func testEscapeCancelsAndIsSwallowedBothWays() {
        let m = fnMachine()
        m.feed(fn, isDown: true)
        let down = m.feed(esc, isDown: true)
        XCTAssertEqual(down.command, .cancel)
        XCTAssertTrue(down.swallow)
        XCTAssertTrue(m.feed(esc, isDown: false).swallow)
        XCTAssertEqual(m.feed(fn, isDown: false).command, .none)
    }

    func testSpaceLocksHandsFreeAndTheNextPressSends() {
        let m = fnMachine()
        m.feed(fn, isDown: true)
        let lockDown = m.feed(space, isDown: true)
        XCTAssertEqual(lockDown.command, .lock)
        XCTAssertTrue(lockDown.swallow)
        XCTAssertTrue(m.feed(space, isDown: false).swallow)
        XCTAssertEqual(m.feed(fn, isDown: false).command, .none)
        XCTAssertTrue(m.isRecording)
        XCTAssertTrue(m.isLocked)

        // Typing while it listens goes through.
        XCTAssertEqual(m.feed(a, isDown: true), .pass)
        XCTAssertEqual(m.feed(a, isDown: false), .pass)

        XCTAssertEqual(m.feed(fn, isDown: true).command, .stopAndSend)
        XCTAssertFalse(m.isRecording)
        XCTAssertEqual(m.feed(fn, isDown: false).command, .none)
    }

    func testEscapeCancelsHandsFree() {
        let m = fnMachine()
        m.feed(fn, isDown: true)
        m.feed(space, isDown: true)
        m.feed(space, isDown: false)
        m.feed(fn, isDown: false)
        XCTAssertEqual(m.feed(esc, isDown: true).command, .cancel)
        XCTAssertFalse(m.isRecording)
    }

    func testAPlainKeyHotkeyIsSwallowed() {
        let m = HotkeyMachine(spec: try! HotkeySpec.parse("f8"), lockKey: space)
        let down = m.feed(f8, isDown: true)
        XCTAssertEqual(down.command, .start)
        XCTAssertTrue(down.swallow)
        XCTAssertTrue(m.feed(f8, isDown: true).swallow) // auto-repeat
        let up = m.feed(f8, isDown: false)
        XCTAssertEqual(up.command, .stopAndSend)
        XCTAssertTrue(up.swallow)
    }

    func testLostKeyUpIsRecoveredByResync() {
        let m = ctrlOpt()
        m.feed(lctrl, isDown: true)
        m.feed(lopt, isDown: true)
        // The screen locked: the key-ups never arrive.
        XCTAssertEqual(m.resync { _ in false }, .stopAndSend)
        XCTAssertFalse(m.isRecording)
        m.feed(lctrl, isDown: true)
        XCTAssertEqual(m.feed(lopt, isDown: true).command, .start)
    }

    func testResyncKeepsARealHold() {
        let m = ctrlOpt()
        m.feed(lctrl, isDown: true)
        m.feed(lopt, isDown: true)
        XCTAssertEqual(m.resync { [lctrl, lopt].contains($0) }, .none)
        XCTAssertTrue(m.isRecording)
    }

    func testMenuStartIsStoppedByTheHotkey() {
        let m = fnMachine()
        m.enterLocked()
        XCTAssertEqual(m.feed(fn, isDown: true).command, .stopAndSend)
    }

    func testParsesHotkeys() throws {
        for text in ["fn", "ctrl+opt", "rcmd", "F8", "ctrl + shift + space", "ropt+f12", "cmd+mouse5", "globe", "option+command"] {
            XCTAssertNoThrow(try HotkeySpec.parse(text), text)
        }
        XCTAssertEqual(try HotkeySpec.parse("ctrl+opt").allKeys, [Key.lctrl, Key.rctrl, Key.lopt, Key.ropt])
    }

    func testRejectsBadHotkeys() {
        for text in ["", "ctrl+banana", "f21", "win"] {
            XCTAssertThrowsError(try HotkeySpec.parse(text), text)
        }
    }
}

final class ShortcutTests: XCTestCase {
    func capture(_ events: [(Int, Bool)]) -> (CaptureOutcome, String?) {
        let c = KeyCapture()
        var outcome = CaptureOutcome.waiting
        for (code, down) in events {
            outcome = c.feed(code, isDown: down)
            if outcome != .waiting { break }
        }
        return (outcome, c.result)
    }

    func testCapturesAMouseButtonOnPress() {
        let (outcome, result) = capture([(Key.mouse4, true)])
        XCTAssertEqual(outcome, .done)
        XCTAssertEqual(result, "mouse4")
    }

    func testCapturesACombinationWhenEverythingIsLetGo() {
        XCTAssertEqual(capture([(Key.lopt, true), (Key.lctrl, true), (Key.lctrl, false), (Key.lopt, false)]).1, "ctrl+opt")
        XCTAssertEqual(capture([(Key.lshift, true), (Key.rcmd, true), (Key.space, true), (Key.space, false),
                                (Key.rcmd, false), (Key.lshift, false)]).1, "shift+cmd+space")
    }

    func testALoneModifierKeepsItsSide() {
        XCTAssertEqual(capture([(Key.rcmd, true), (Key.rcmd, true), (Key.rcmd, false)]).1, "rcmd")
        XCTAssertEqual(capture([(Key.fn, true), (Key.fn, false)]).1, "fn")
    }

    func testModifiersWithAMouseButton() {
        XCTAssertEqual(capture([(Key.lctrl, true), (Key.mouse5, true)]).1, "ctrl+mouse5")
    }

    func testEscapeAloneCancels() {
        XCTAssertEqual(capture([(Key.escape, true)]).0, .cancelled)
    }

    func testKeysWithoutANameAreRefused() {
        let semicolon = 41
        XCTAssertEqual(capture([(Key.lctrl, true), (semicolon, true), (semicolon, false), (Key.lctrl, false)]).0, .unusable)
        XCTAssertEqual(capture([(Key.mouse(button: 7), true)]).0, .unusable)
    }

    func testEveryCapturedNameParsesBack() throws {
        for name in ["mouse4", "mouse5", "mouse3", "ctrl+opt", "rcmd", "fn", "shift+cmd+space", "f9", "ctrl+mouse5", "lopt"] {
            XCTAssertEqual(try HotkeySpec.parse(name).text, name)
        }
        for code in KeyNames.names.keys {
            let name = try XCTUnwrap(KeyNames.name(code))
            let spec = try HotkeySpec.parse(name)
            XCTAssertTrue(spec.contains(code), name)
        }
        XCTAssertTrue(try HotkeySpec.parse("mouse4").usesMouse)
        XCTAssertFalse(try HotkeySpec.parse("ctrl+opt").usesMouse)
        XCTAssertTrue(try HotkeySpec.parse("fn+space").usesFn)
    }

    func testHoldingAMouseButtonRecordsAndSwallowsIt() {
        let m = HotkeyMachine(spec: try! HotkeySpec.parse("mouse4"), lockKey: Key.space)
        let down = m.feed(Key.mouse4, isDown: true)
        XCTAssertEqual(down.command, .start)
        XCTAssertTrue(down.swallow) // the browser must not go back a page
        let up = m.feed(Key.mouse4, isDown: false)
        XCTAssertEqual(up.command, .stopAndSend)
        XCTAssertTrue(up.swallow)
    }

    func testToggleFiresOncePerPressAndSwallowsItsButton() {
        let t = ToggleKey(spec: try! HotkeySpec.parse("mouse5"))
        let first = t.feed(Key.mouse5, isDown: true)
        XCTAssertEqual(first.command, .toggle)
        XCTAssertTrue(first.swallow)
        XCTAssertTrue(t.feed(Key.mouse5, isDown: false).swallow)
        XCTAssertEqual(t.feed(Key.lctrl, isDown: true).command, .none)
        XCTAssertFalse(t.feed(Key.lctrl, isDown: true).swallow)
        t.feed(Key.lctrl, isDown: false)
        XCTAssertEqual(t.feed(Key.mouse5, isDown: true).command, .toggle)
    }

    func testToggleOnACombinationFiresWhenItCompletes() {
        let f9 = 101
        let t = ToggleKey(spec: try! HotkeySpec.parse("ctrl+f9"))
        XCTAssertEqual(t.feed(Key.lctrl, isDown: true).command, .none)
        let r = t.feed(f9, isDown: true)
        XCTAssertEqual(r.command, .toggle)
        XCTAssertTrue(r.swallow)
        XCTAssertEqual(t.feed(f9, isDown: true).command, .none) // auto-repeat
    }
}
