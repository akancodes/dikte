import XCTest
@testable import DikteCore
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

final class ModesTests: XCTestCase {
    func decode(_ json: String) throws -> Settings { try Settings.decode(Data(json.utf8)) }

    func testAFreshInstallWritesAsHeardWithTheBuiltInModesReady() throws {
        let s = Settings()
        XCTAssertEqual(s.activeMode, Modes.plain)
        XCTAssertEqual(s.modes.map(\.id), ["clean", "prompt", "list", "commit", "email"])
        XCTAssertNoThrow(try s.validate())
    }

    func testTheOldTidyUpSwitchBecomesTheCleanUpMode() throws {
        XCTAssertEqual(try decode("{\"cleanup\": true}").activeMode, "clean")
        XCTAssertEqual(try decode("{\"cleanup\": false}").activeMode, Modes.plain)
        XCTAssertEqual(try decode("{\"cleanup\": true, \"activeMode\": \"list\"}").activeMode, "list")
    }

    func testADeletedActiveModeFallsBackToAsHeard() throws {
        XCTAssertEqual(try decode("{\"activeMode\": \"gone\"}").activeMode, Modes.plain)
    }

    func testTheSameFileAsWindowsReadsTheSame() throws {
        let s = try decode("""
        { "activeMode": "slack", "nextModeHotkey": "f8",
          "modes": [ { "id": "slack", "name": "Slack", "instruction": "Casual.", "model": "", "apps": [" Slack ", "slack"], "hotkey": "ctrl+fn" },
                     { "name": "No id", "instruction": "x" } ] }
        """)
        XCTAssertEqual(s.activeMode, "slack")
        XCTAssertEqual(s.modes[0].apps, ["Slack"])
        XCTAssertEqual(s.modes[1].id, "no-id")
        XCTAssertNoThrow(try s.validate())
        let back = try Settings.decode(s.encoded())
        XCTAssertEqual(back, s)
    }

    func testAShortcutBeatsTheAppWhichBeatsTheActiveMode() {
        var s = Settings()
        s.activeMode = "list"
        XCTAssertEqual(Modes.resolve(s, forced: "email", app: "Terminal")?.id, "email")
        XCTAssertEqual(Modes.resolve(s, forced: nil, app: "Terminal")?.id, "prompt")
        XCTAssertEqual(Modes.resolve(s, forced: nil, app: "iterm2")?.id, "prompt")
        XCTAssertEqual(Modes.resolve(s, forced: nil, app: "Code.app")?.id, "prompt")
        XCTAssertEqual(Modes.resolve(s, forced: nil, app: "Notes")?.id, "list")
        XCTAssertEqual(Modes.resolve(s, forced: nil, app: nil)?.id, "list")
        s.activeMode = Modes.plain
        XCTAssertNil(Modes.resolve(s, forced: nil, app: "Notes"))
        XCTAssertEqual(Modes.nameOf(s, Modes.plain), "As spoken")
    }

    func testNextGoesRoundThroughAsHeard() {
        var s = Settings()
        var seen: [String] = []
        for _ in 0..<7 {
            s.activeMode = Modes.next(s)
            seen.append(s.activeMode)
        }
        XCTAssertEqual(seen, ["clean", "prompt", "list", "commit", "email", Modes.plain, "clean"])
    }

    func testNewIdsAreUniqueSlugs() {
        let taken = Settings().modes.map(\.id)
        XCTAssertEqual(Modes.newId("Slack reply", taken: taken), "slack-reply")
        XCTAssertEqual(Modes.newId("!!!", taken: taken), "mode")
        XCTAssertEqual(Modes.newId("Plain", taken: taken), "mode")
        XCTAssertEqual(Modes.newId("List", taken: taken), "list-2")
    }

    func testModesNeedANameAnInstructionAndTheirOwnShortcut() {
        var s = Settings()
        s.modes[0].name = ""
        XCTAssertThrowsError(try s.validate())
        s = Settings()
        s.modes[1].instruction = ""
        XCTAssertThrowsError(try s.validate())
        s = Settings()
        s.modes[1].hotkey = s.hotkey
        XCTAssertThrowsError(try s.validate())
        s = Settings()
        s.modes[1].hotkey = "ctrl+fn"
        s.nextModeHotkey = "f8"
        XCTAssertNoThrow(try s.validate())
        s.nextModeHotkey = "ctrl + fn"
        XCTAssertThrowsError(try s.validate())
        s = Settings()
        s.modes[2].id = "clean"
        XCTAssertThrowsError(try s.validate())
    }

    func testRewriteSendsTheInstructionAndTheModesModel() async throws {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"choices\":[{\"message\":{\"content\":\"- one\\n- two\"}}]}")]
        let api = Transcriber(transport: fake)
        var s = Settings()
        s.apiBase = "http://127.0.0.1:9/v1"
        let mode = Mode(id: "list", name: "List", instruction: "As a list.", model: "gpt-4.1")
        let text = try await api.rewrite("one and two", mode: mode, settings: s, apiKey: "k")
        XCTAssertEqual(text, "- one\n- two")
        let payload = try JSONSerialization.jsonObject(with: fake.requests[0].httpBody!) as? [String: Any]
        XCTAssertEqual(payload?["model"] as? String, "gpt-4.1")
        let messages = payload?["messages"] as? [[String: String]]
        XCTAssertTrue(messages?[1]["content"]?.contains("<mode>\nAs a list.\n</mode>") == true)
    }

    func testARewriteThatAnswersKeepsTheWordsAsHeard() async throws {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"choices\":[{\"message\":{\"content\":\"\(String(repeating: "x", count: 2000))\"}}]}")]
        let text = try await Transcriber(transport: fake).rewrite("what is a monad", mode: Mode(instruction: "x"), settings: Settings(), apiKey: "k")
        XCTAssertEqual(text, "what is a monad")
    }
}

final class HoldKeysTests: XCTestCase {
    func keys() throws -> HoldKeys {
        HoldKeys([(nil, try HotkeySpec.parse("fn")), ("prompt", try HotkeySpec.parse("ctrl+fn"))], lockKey: Key.space)
    }

    func testThePlainShortcutDictatesInTheActiveMode() throws {
        let k = try keys()
        let r = k.feed(Key.fn, isDown: true)
        XCTAssertEqual(r.commands.count, 1)
        XCTAssertEqual(r.commands[0].command, .start)
        XCTAssertNil(r.commands[0].mode)
    }

    func testTheBiggerShortcutWinsAndCarriesItsMode() throws {
        let k = try keys()
        _ = k.feed(Key.lctrl, isDown: true)
        let r = k.feed(Key.fn, isDown: true)
        XCTAssertEqual(r.commands.count, 1)
        XCTAssertEqual(r.commands[0].command, .start)
        XCTAssertEqual(r.commands[0].mode, "prompt")
        let up = k.feed(Key.fn, isDown: false)
        XCTAssertEqual(up.commands.first?.command, .stopAndSend)
        XCTAssertEqual(up.commands.first?.mode, "prompt")
        XCTAssertFalse(k.isRecording)
    }
}
