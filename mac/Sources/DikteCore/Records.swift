import Foundation

public enum Wav {
    /// Wraps 16-bit little-endian PCM in a RIFF/WAVE header.
    public static func fromPcm16(_ pcm: Data, sampleRate: Int, channels: Int) -> Data {
        var wav = Data(capacity: 44 + pcm.count)
        func u32(_ v: Int) { withUnsafeBytes(of: UInt32(v).littleEndian) { wav.append(contentsOf: $0) } }
        func u16(_ v: Int) { withUnsafeBytes(of: UInt16(v).littleEndian) { wav.append(contentsOf: $0) } }
        wav.append("RIFF")
        u32(36 + pcm.count)
        wav.append("WAVE")
        wav.append("fmt ")
        u32(16)
        u16(1)
        u16(channels)
        u32(sampleRate)
        u32(sampleRate * channels * 2)
        u16(channels * 2)
        u16(16)
        wav.append("data")
        u32(pcm.count)
        wav.append(pcm)
        return wav
    }

    /// Peak absolute sample of a 16-bit PCM buffer, 0...1.
    public static func peak(_ pcm: Data) -> Float {
        var peak: Int32 = 0
        pcm.withUnsafeBytes { raw in
            let samples = raw.bindMemory(to: Int16.self)
            for s in samples {
                let v = abs(Int32(Int16(littleEndian: s)))
                if v > peak { peak = v }
            }
        }
        return Float(peak) / 32768
    }

    /// A short sine blip sequence (frequencies in Hz, each tone in ms) for feedback sounds.
    public static func tones(sampleRate: Int, volume: Float, _ tones: [(hz: Double, ms: Int)]) -> Data {
        var pcm = Data()
        for (hz, ms) in tones {
            let n = sampleRate * ms / 1000
            let fade = min(n / 4, sampleRate / 200)
            for i in 0..<n {
                let env = min(1, Double(min(i, n - 1 - i)) / Double(max(1, fade)))
                let v = sin(2 * Double.pi * hz * Double(i) / Double(sampleRate)) * env * Double(volume) * Double(Int16.max)
                withUnsafeBytes(of: Int16(v).littleEndian) { pcm.append(contentsOf: $0) }
            }
        }
        return fromPcm16(pcm, sampleRate: sampleRate, channels: 1)
    }
}

public struct HistoryEntry: Codable, Equatable {
    public let at: Date
    public let text: String
}

/// The last few transcripts, kept on disk so a paste that landed nowhere is never lost.
public final class History {
    let url: URL
    let size: Int
    public private(set) var items: [HistoryEntry]

    public init(url: URL, size: Int) {
        self.url = url
        self.size = max(1, size)
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        items = (try? decoder.decode([HistoryEntry].self, from: Data(contentsOf: url))) ?? []
    }

    public func add(_ text: String, at: Date = Date()) {
        items.insert(HistoryEntry(at: at, text: text), at: 0)
        if items.count > size { items = Array(items.prefix(size)) }
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        try? encoder.encode(items).write(to: url, options: .atomic)
    }

    public func clear() {
        items.removeAll()
        try? FileManager.default.removeItem(at: url)
    }
}

public struct DayStats: Codable, Equatable {
    public var words = 0
    public var seconds = 0.0
    public var cost = 0.0
    public var count = 0
}

public struct StatsSummary: Equatable {
    public let weekWords: Int
    public let weekMinutes: Double
    public let monthCost: Double
    public let wpm: Int
    public let totalWords: Int
    public let totalCount: Int

    public var json: [String: Any] {
        ["weekWords": weekWords, "weekMinutes": weekMinutes, "monthCost": monthCost, "wpm": wpm,
         "totalWords": totalWords, "totalCount": totalCount]
    }
}

/// What has been dictated, by day, kept on disk: words, speaking time and what it cost.
public final class Stats {
    let url: URL
    var days: [String: DayStats]
    let calendar = Calendar(identifier: .gregorian)

    public init(url: URL) {
        self.url = url
        days = (try? JSONDecoder().decode([String: DayStats].self, from: Data(contentsOf: url))) ?? [:]
    }

    /// US dollars per minute of audio; unknown models (other servers) count as free.
    public static func pricePerMinute(_ model: String) -> Double {
        switch model {
        case "gpt-transcribe": return 0.0045
        case "gpt-4o-transcribe", "whisper-1", "gpt-4o-transcribe-diarize": return 0.006
        case "gpt-4o-mini-transcribe": return 0.003
        default: return 0
        }
    }

    func key(_ date: Date) -> String {
        let c = calendar.dateComponents(in: TimeZone.current, from: date)
        return String(format: "%04d-%02d-%02d", c.year!, c.month!, c.day!)
    }

    func date(_ key: String) -> Date? {
        let parts = key.split(separator: "-").compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        var c = DateComponents()
        c.timeZone = TimeZone.current
        (c.year, c.month, c.day) = (parts[0], parts[1], parts[2])
        return calendar.date(from: c)
    }

    public func record(at: Date, words: Int, seconds: Double, model: String) {
        var day = days[key(at)] ?? DayStats()
        day.words += words
        day.seconds += seconds
        day.cost += seconds / 60 * Stats.pricePerMinute(model)
        day.count += 1
        days[key(at)] = day
        try? JSONEncoder().encode(days).write(to: url, options: .atomic)
    }

    public func summary(now: Date) -> StatsSummary {
        guard let today = date(key(now)), let weekStart = calendar.date(byAdding: .day, value: -7, to: today) else {
            return StatsSummary(weekWords: 0, weekMinutes: 0, monthCost: 0, wpm: 0, totalWords: 0, totalCount: 0)
        }
        let month = calendar.dateComponents(in: TimeZone.current, from: today)
        var weekWords = 0, totalWords = 0, totalCount = 0
        var weekSeconds = 0.0, monthCost = 0.0, totalSeconds = 0.0
        for (key, day) in days {
            guard let date = date(key) else { continue }
            totalWords += day.words
            totalSeconds += day.seconds
            totalCount += day.count
            if date > weekStart && date <= today {
                weekWords += day.words
                weekSeconds += day.seconds
            }
            let c = calendar.dateComponents(in: TimeZone.current, from: date)
            if c.year == month.year && c.month == month.month { monthCost += day.cost }
        }
        let wpm = totalSeconds >= 20 ? Int((Double(totalWords) / (totalSeconds / 60)).rounded()) : 0
        return StatsSummary(weekWords: weekWords, weekMinutes: weekSeconds / 60, monthCost: monthCost, wpm: wpm,
                            totalWords: totalWords, totalCount: totalCount)
    }
}
