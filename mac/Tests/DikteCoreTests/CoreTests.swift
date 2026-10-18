import XCTest
@testable import DikteCore
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("dikte-tests-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

final class WavTests: XCTestCase {
    func u32(_ d: Data, _ at: Int) -> Int { Int(d[at]) | Int(d[at + 1]) << 8 | Int(d[at + 2]) << 16 | Int(d[at + 3]) << 24 }

    func testHeaderDescribes16kHzMonoPcm() {
        let pcm = Data(count: 32000)
        let wav = Wav.fromPcm16(pcm, sampleRate: 16000, channels: 1)
        XCTAssertEqual(wav.count, 44 + pcm.count)
        XCTAssertEqual(String(decoding: wav[0..<4], as: UTF8.self), "RIFF")
        XCTAssertEqual(String(decoding: wav[8..<12], as: UTF8.self), "WAVE")
        XCTAssertEqual(u32(wav, 24), 16000)
        XCTAssertEqual(u32(wav, 28), 32000)
        XCTAssertEqual(Int(wav[22]), 1)
        XCTAssertEqual(u32(wav, 40), pcm.count)
    }

    func testPeakReadsTheLoudestSample() {
        var pcm = Data(count: 8)
        withUnsafeBytes(of: Int16(-16384).littleEndian) { pcm.replaceSubrange(2..<4, with: $0) }
        withUnsafeBytes(of: Int16(1000).littleEndian) { pcm.replaceSubrange(4..<6, with: $0) }
        XCTAssertEqual(Wav.peak(pcm), 0.5, accuracy: 0.001)
        XCTAssertEqual(Wav.peak(Data(count: 100)), 0)
    }

    func testTonesAreValidWav() {
        XCTAssertEqual(String(decoding: Wav.tones(sampleRate: 44100, volume: 0.2, [(440, 50)]).prefix(4), as: UTF8.self), "RIFF")
    }
}

final class SettingsTests: XCTestCase {
    func testWritesDefaultsThenReadsCommentsAndPartialFiles() throws {
        let url = tempDir().appendingPathComponent("settings.json")
        let fresh = try Settings.loadOrCreate(url)
        XCTAssertEqual(fresh.hotkey, "fn")
        XCTAssertTrue(try String(contentsOf: url).contains("\"hotkey\""))

        try "{\n // my keys\n \"hotkey\": \"rcmd\", /* both */\n \"languages\": [\"tr\", \"en\",],\n \"vocabulary\": \"a // not a comment, ok\",\n}"
            .write(to: url, atomically: true, encoding: .utf8)
        let mine = try Settings.loadOrCreate(url)
        XCTAssertEqual(mine.hotkey, "rcmd")
        XCTAssertEqual(mine.languages, ["tr", "en"])
        XCTAssertEqual(mine.vocabulary, "a // not a comment, ok")
        XCTAssertEqual(mine.model, "gpt-transcribe")
    }

    func testRejectsBadValues() throws {
        let url = tempDir().appendingPathComponent("settings.json")
        try "{\"insertMode\": \"shout\"}".write(to: url, atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try Settings.loadOrCreate(url))
        try "{\"maxMinutes\": \"ten\"}".write(to: url, atomically: true, encoding: .utf8)
        XCTAssertThrowsError(try Settings.loadOrCreate(url)) { error in
            XCTAssertEqual((error as? ConfigError)?.message, "maxMinutes has the wrong kind of value in settings.json.")
        }
    }

    func testMergesAPatchFromTheWindow() throws {
        let merged = try Settings().merging(["languages": ["tr"], "quietLevel": 0.3, "cleanup": true])
        XCTAssertEqual(merged.languages, ["tr"])
        XCTAssertEqual(merged.quietLevel, 0.3)
        XCTAssertTrue(merged.cleanup)
        XCTAssertEqual(merged.hotkey, "fn")
        XCTAssertThrowsError(try Settings().merging(["hotkey": "banana"]))
    }

    func testRefusesTheSameShortcutTwice() {
        var s = Settings()
        s.hotkey = "mouse4"
        s.toggleHotkey = "mouse4"
        XCTAssertThrowsError(try s.validate())
        s.toggleHotkey = "mouse5"
        XCTAssertNoThrow(try s.validate())
        s.toggleHotkey = ""
        XCTAssertNoThrow(try s.validate())
        s.toggleHotkey = "mouse9"
        XCTAssertThrowsError(try s.validate())
    }

    func testRejectsLanguageNamesInsteadOfCodes() {
        var s = Settings()
        s.languages = ["Turkish"]
        XCTAssertThrowsError(try s.validate())
    }

    func testHistoryKeepsTheNewestAndSurvivesARestart() {
        let url = tempDir().appendingPathComponent("history.json")
        let h = History(url: url, size: 2)
        h.add("one")
        h.add("two")
        h.add("three")
        XCTAssertEqual(h.items.map(\.text), ["three", "two"])
        XCTAssertEqual(History(url: url, size: 2).items.map(\.text), ["three", "two"])
    }
}

/// Answers each request with the next canned reply and keeps what it was sent.
final class FakeTransport: HTTPTransport {
    var answers: [(Int, String)] = []
    var failures: [URLError] = []
    var requests: [URLRequest] = []

    func send(_ request: URLRequest) async throws -> (Data, Int) {
        requests.append(request)
        if !failures.isEmpty { throw failures.removeFirst() }
        let (status, body) = answers.isEmpty ? (200, "{}") : answers.removeFirst()
        return (Data(body.utf8), status)
    }

    func body(_ i: Int = 0) -> String { String(decoding: requests[i].httpBody ?? Data(), as: UTF8.self) }
}

final class TranscriberTests: XCTestCase {
    func make(_ fake: FakeTransport) -> Transcriber {
        let api = Transcriber(transport: fake)
        api.retryDelayScale = 0
        return api
    }

    func settings(_ languages: [String] = []) -> Settings {
        var s = Settings()
        s.apiBase = "http://127.0.0.1:9/v1/"
        s.languages = languages
        return s
    }

    func testSendsTheAudioAsMultipartWithModelAndVocabulary() async throws {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"text\":\"  Merhaba, deploy et.  \"}")]
        let text = try await make(fake).transcribe(wav: Wav.fromPcm16(Data(count: 640), sampleRate: 16000, channels: 1),
                                                    settings: settings(["tr"]), apiKey: "sk-test")
        XCTAssertEqual(text, "Merhaba, deploy et.")
        XCTAssertEqual(fake.requests.count, 1)
        let req = fake.requests[0]
        XCTAssertEqual(req.url?.absoluteString, "http://127.0.0.1:9/v1/audio/transcriptions")
        XCTAssertEqual(req.value(forHTTPHeaderField: "Authorization"), "Bearer sk-test")
        XCTAssertTrue(req.value(forHTTPHeaderField: "Content-Type")?.hasPrefix("multipart/form-data; boundary=") == true)
        let body = fake.body()
        for part in ["name=\"model\"", "gpt-transcribe", "name=\"language\"", "name=\"prompt\"", "Claude Code",
                     "filename=\"dictation.wav\"", "audio/wav", "RIFF"] {
            XCTAssertTrue(body.contains(part), part)
        }
    }

    func testMixedLanguagesGoInThePromptNotTheLanguageField() async throws {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"text\":\"hi\"}")]
        _ = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(["tr", "en"]), apiKey: "k")
        XCTAssertFalse(fake.body().contains("name=\"language\""))
        XCTAssertTrue(fake.body().contains("mixes Turkish and English"))
    }

    func testLeavesLanguageOutWhenAuto() async throws {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"text\":\"hi\"}")]
        _ = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(), apiKey: "k")
        XCTAssertFalse(fake.body().contains("name=\"language\""))
    }

    func testRetriesOnceOnAServerError() async throws {
        let fake = FakeTransport()
        fake.answers = [(500, "{\"error\":{\"message\":\"boom\"}}"), (200, "{\"text\":\"ok\"}")]
        let text = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(), apiKey: "k")
        XCTAssertEqual(text, "ok")
        XCTAssertEqual(fake.requests.count, 2)
    }

    func testRetriesOnceOnADroppedConnection() async throws {
        let fake = FakeTransport()
        fake.failures = [URLError(.networkConnectionLost)]
        fake.answers = [(200, "{\"text\":\"ok\"}")]
        let text = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(), apiKey: "k")
        XCTAssertEqual(text, "ok")
        XCTAssertEqual(fake.requests.count, 2)
    }

    func testReportsARefusedKeyWithoutRetrying() async {
        let fake = FakeTransport()
        fake.answers = [(401, "{\"error\":{\"message\":\"Incorrect API key\"}}")]
        do {
            _ = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(), apiKey: "k")
            XCTFail("no error")
        } catch {
            XCTAssertTrue(error.localizedDescription.contains("refused"))
        }
        XCTAssertEqual(fake.requests.count, 1)
    }

    func testReportsOtherErrorsWithTheApiMessage() async {
        let fake = FakeTransport()
        fake.answers = [(400, "{\"error\":{\"message\":\"Audio file is too short\"}}")]
        do {
            _ = try await make(fake).transcribe(wav: Data(count: 44), settings: settings(), apiKey: "k")
            XCTFail("no error")
        } catch {
            XCTAssertEqual(error as? ApiError, ApiError("API error 400: Audio file is too short"))
        }
    }

    func testKeyCheckReportsTheProblem() async {
        let fake = FakeTransport()
        fake.answers = [(200, "{\"data\":[]}"), (401, "{}")]
        let api = make(fake)
        let good = await api.checkKey(settings: settings(), apiKey: "good")
        let bad = await api.checkKey(settings: settings(), apiKey: "bad")
        XCTAssertNil(good)
        XCTAssertNotNil(bad)
        XCTAssertEqual(fake.requests[0].httpMethod, "GET")
    }
}

final class LanguagesTests: XCTestCase {
    func testOneLanguageIsPinnedSeveralAreHinted() {
        XCTAssertEqual(Languages.pinned(["tr"]), "tr")
        XCTAssertNil(Languages.pinned(["tr", "en"]))
        XCTAssertNil(Languages.pinned([]))
        XCTAssertEqual(Languages.buildPrompt(["tr"], vocabulary: " Docker "), "Docker")
        XCTAssertEqual(Languages.buildPrompt([], vocabulary: ""), "")
        let mixed = Languages.buildPrompt(["tr", "en"], vocabulary: "Docker")
        XCTAssertTrue(mixed.hasPrefix("Konuşma Türkçe ve İngilizce karışık"))
        XCTAssertTrue(mixed.hasSuffix("Docker"))
        XCTAssertTrue(Languages.buildPrompt(["de", "fr", "es"], vocabulary: "").contains("mixes German, French and Spanish"))
    }
}

final class StatsTests: XCTestCase {
    func day(_ y: Int, _ m: Int, _ d: Int, _ h: Int = 12) -> Date {
        Calendar(identifier: .gregorian).date(from: DateComponents(timeZone: .current, year: y, month: m, day: d, hour: h))!
    }

    func testSumsTheWeekAndPricesTheMonth() {
        let url = tempDir().appendingPathComponent("stats.json")
        let stats = Stats(url: url)
        let now = day(2026, 10, 17)
        stats.record(at: now, words: 100, seconds: 60, model: "gpt-transcribe")
        stats.record(at: day(2026, 10, 11), words: 50, seconds: 30, model: "gpt-transcribe")
        stats.record(at: day(2026, 10, 10), words: 1000, seconds: 600, model: "gpt-transcribe") // outside the 7 days
        stats.record(at: day(2026, 9, 30), words: 10, seconds: 60, model: "whisper-1") // last month

        let s = Stats(url: url).summary(now: now)
        XCTAssertEqual(s.weekWords, 150)
        XCTAssertEqual(s.weekMinutes, 1.5, accuracy: 0.001)
        XCTAssertEqual(s.monthCost, (60 + 30 + 600) / 60.0 * 0.0045, accuracy: 0.000001)
        XCTAssertEqual(s.totalWords, 1160)
        XCTAssertEqual(s.totalCount, 4)
        XCTAssertEqual(s.wpm, Int((1160 / (750 / 60.0)).rounded()))
    }

    func testOtherServersCostNothing() {
        XCTAssertEqual(Stats.pricePerMinute("whisper-large-v3-turbo"), 0)
    }
}

final class JSONCTests: XCTestCase {
    func testStripsCommentsAndTrailingCommasButNotStrings() {
        XCTAssertEqual(JSONC.strip("{\"a\": 1, // x\n}"), "{\"a\": 1 \n}")
        XCTAssertEqual(JSONC.strip("[1, 2, /* c */ ]"), "[1, 2  ]")
        XCTAssertEqual(JSONC.strip("{\"u\": \"http://x/*y*/\", \"q\": \"a\\\",\"}"), "{\"u\": \"http://x/*y*/\", \"q\": \"a\\\",\"}")
    }
}
