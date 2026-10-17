import AVFoundation
import DikteCore
import Foundation

struct AudioFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// The microphone, open for a whole session. iOS lets an app start recording only while it is on screen,
/// so the session opens it once in the foreground and keeps it running in the background; between dictations
/// the audio is dropped, except the last half second, which is put in front of the next dictation so its
/// first syllable is not lost. Everything kept is 16 kHz mono 16-bit PCM: what the speech models take.
final class Microphone: @unchecked Sendable {
    static let sampleRate = 16000
    /// Half a second of 16-bit samples.
    private static let prerollBytes = sampleRate

    private let engine = AVAudioEngine()
    private let lock = NSLock()
    private var capturing = false
    private var pcm = Data()
    private var preroll = Data()
    private var currentLevel: Float = 0
    private var loudest: Float = 0
    private var converter: AVAudioConverter?
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(Microphone.sampleRate),
                                          channels: 1, interleaved: true)!

    var isRunning: Bool { engine.isRunning }

    /// Opens the audio session and the microphone. Music keeps playing (mixWithOthers), and AirPods stay in
    /// their good playback mode: the iPhone's own microphone does the listening.
    func open() throws {
        let session = AVAudioSession.sharedInstance()
        do {
            try session.setCategory(.playAndRecord, mode: .default,
                                    options: [.mixWithOthers, .allowBluetoothA2DP, .defaultToSpeaker])
            try session.setActive(true)
        } catch {
            throw AudioFailure(message: "The microphone could not be opened (\(error.localizedDescription)).")
        }
        try startEngine()
    }

    /// After a route change (headphones in or out) the input format can change: start again with the new one.
    func restart() throws {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        try startEngine()
    }

    private func startEngine() throws {
        let input = engine.inputNode
        let inFormat = input.outputFormat(forBus: 0)
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0 else { throw AudioFailure(message: "No microphone is available.") }
        guard let converter = AVAudioConverter(from: inFormat, to: outFormat) else {
            throw AudioFailure(message: "The microphone's format (\(Int(inFormat.sampleRate)) Hz) cannot be converted.")
        }
        converter.downmix = true
        self.converter = converter
        input.installTap(onBus: 0, bufferSize: 2048, format: inFormat) { [weak self] buffer, _ in
            self?.consume(buffer)
        }
        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw AudioFailure(message: error.localizedDescription)
        }
    }

    /// Closes the microphone and lets other apps' audio go back to how it was.
    func close() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        read { capturing = false; pcm = Data(); preroll = Data() }
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    private func consume(_ buffer: AVAudioPCMBuffer) {
        guard let converter else { return }
        let ratio = outFormat.sampleRate / buffer.format.sampleRate
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * ratio + 64)
        guard let out = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: capacity) else { return }
        let once = Once()
        var error: NSError?
        _ = converter.convert(to: out, error: &error) { _, status in
            if once.used {
                status.pointee = .noDataNow
                return nil
            }
            once.used = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, out.frameLength > 0, let samples = out.int16ChannelData?[0] else { return }
        let count = Int(out.frameLength)
        var peak: Int32 = 0
        for i in 0..<count { peak = max(peak, abs(Int32(samples[i]))) }
        let level = Float(peak) / 32768
        let chunk = Data(bytes: samples, count: count * 2)
        lock.lock()
        currentLevel = level
        if capturing {
            pcm.append(chunk)
            loudest = max(loudest, level)
        } else {
            preroll.append(chunk)
            if preroll.count > Microphone.prerollBytes { preroll = Data(preroll.suffix(Microphone.prerollBytes)) }
        }
        lock.unlock()
    }

    /// Hands the converter its one input buffer (a class, so the input block can be @Sendable).
    private final class Once: @unchecked Sendable {
        var used = false
    }

    private func read<T>(_ body: () -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body()
    }

    /// Starts keeping what is said, beginning with the half second before.
    func beginCapture() {
        read {
            pcm = preroll
            preroll = Data()
            loudest = 0
            capturing = true
        }
    }

    /// Stops keeping audio and returns what was said as WAV, with its length and loudest peak.
    func endCapture() -> (wav: Data, seconds: Double, peak: Float) {
        read {
            capturing = false
            let seconds = Double(pcm.count) / 2 / Double(Microphone.sampleRate)
            let result = (wav: Wav.fromPcm16(pcm, sampleRate: Microphone.sampleRate, channels: 1), seconds: seconds, peak: loudest)
            pcm = Data()
            return result
        }
    }

    /// Peak of the latest buffer, 0...1.
    var level: Float { read { currentLevel } }
    /// Seconds kept so far.
    var captured: Double { read { capturing ? Double(pcm.count) / 2 / Double(Microphone.sampleRate) : 0 } }
}
