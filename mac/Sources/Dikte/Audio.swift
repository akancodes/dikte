import AudioToolbox
import AVFoundation
import CoreAudio
import DikteCore
import Foundation

struct AudioFailure: LocalizedError {
    let message: String
    var errorDescription: String? { message }
}

/// Records the microphone as 16 kHz mono 16-bit PCM: what the speech models take, and small.
final class Recorder: @unchecked Sendable {
    static let sampleRate = 16000

    private let engine = AVAudioEngine()
    private let device: String
    private let lock = NSLock()
    private var pcm = Data()
    private var currentLevel: Float = 0
    private var loudest: Float = 0
    private var converter: AVAudioConverter?
    private let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(Recorder.sampleRate),
                                          channels: 1, interleaved: true)!

    init(device: String) {
        self.device = device
    }

    func start() throws {
        let input = engine.inputNode
        if !device.isEmpty {
            if var id = AudioDevices.input(named: device), let unit = input.audioUnit {
                let status = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                                  &id, UInt32(MemoryLayout<AudioDeviceID>.size))
                if status != noErr { Log.write("Could not pick microphone \"\(device)\" (\(status)); using the default") }
            } else {
                Log.write("Microphone \"\(device)\" not found; using the default")
            }
        }
        let inFormat = input.outputFormat(forBus: 0)
        guard inFormat.sampleRate > 0, inFormat.channelCount > 0 else { throw AudioFailure(message: "No microphone is connected.") }
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
        pcm.append(chunk)
        currentLevel = level
        loudest = max(loudest, level)
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

    /// Peak of the latest buffer, 0...1.
    var level: Float { read { currentLevel } }
    var maxLevel: Float { read { loudest } }
    /// Seconds of audio recorded so far.
    var duration: Double { read { Double(pcm.count) / 2 / Double(Recorder.sampleRate) } }

    /// Stops and returns the recording as WAV.
    func stop() -> Data {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        return read { Wav.fromPcm16(pcm, sampleRate: Recorder.sampleRate, channels: 1) }
    }
}

/// The Core Audio calls Dikte needs: input devices by name, and the default output's volume.
enum AudioDevices {
    private static func address(_ selector: AudioObjectPropertySelector,
                                _ scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }

    static func all() -> [AudioDeviceID] {
        var addr = address(kAudioHardwarePropertyDevices)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    static func string(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
        var addr = address(selector)
        var value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr, let value else { return nil }
        return value.takeRetainedValue() as String
    }

    static func hasInput(_ id: AudioDeviceID) -> Bool {
        var addr = address(kAudioDevicePropertyStreamConfiguration, kAudioDevicePropertyScopeInput)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &addr, 0, nil, &size) == noErr, size > 0 else { return false }
        let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(size), alignment: MemoryLayout<AudioBufferList>.alignment)
        defer { raw.deallocate() }
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, raw) == noErr else { return false }
        let list = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
        return list.contains { $0.mNumberChannels > 0 }
    }

    static func inputNames() -> [String] {
        all().filter(hasInput).compactMap { string($0, kAudioObjectPropertyName) }
    }

    static func input(named name: String) -> AudioDeviceID? {
        all().first { hasInput($0) && string($0, kAudioObjectPropertyName) == name }
    }

    static func defaultOutput() -> AudioDeviceID? {
        var addr = address(kAudioHardwarePropertyDefaultOutputDevice)
        var id = AudioDeviceID(0)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        guard AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &addr, 0, nil, &size, &id) == noErr, id != 0 else { return nil }
        return id
    }

    static func device(uid: String) -> AudioDeviceID? {
        all().first { string($0, kAudioDevicePropertyDeviceUID) == uid }
    }

    private static var volumeAddress: AudioObjectPropertyAddress {
        address(kAudioHardwareServiceDeviceProperty_VirtualMainVolume, kAudioDevicePropertyScopeOutput)
    }

    /// The device's main output volume, 0...1, or nil when it has none (HDMI, some USB DACs).
    static func volume(_ id: AudioDeviceID) -> Float32? {
        var addr = volumeAddress
        guard AudioObjectHasProperty(id, &addr) else { return nil }
        var value: Float32 = 0
        var size = UInt32(MemoryLayout<Float32>.size)
        guard AudioObjectGetPropertyData(id, &addr, 0, nil, &size, &value) == noErr else { return nil }
        return value
    }

    @discardableResult
    static func setVolume(_ id: AudioDeviceID, _ volume: Float32) -> Bool {
        var addr = volumeAddress
        var settable: DarwinBoolean = false
        guard AudioObjectIsPropertySettable(id, &addr, &settable) == noErr, settable.boolValue else { return false }
        var value = max(0, min(1, volume))
        return AudioObjectSetPropertyData(id, &addr, 0, nil, UInt32(MemoryLayout<Float32>.size), &value) == noErr
    }
}

/// Turns the Mac's output down while Dikte listens and back up afterwards. macOS has no per-app
/// volume, so it is the output device's own volume; what it was is written to disk first, so a crash
/// mid-dictation cannot leave the Mac quiet.
final class Ducker {
    private struct Saved: Codable {
        let uid: String
        let volume: Float32
        let ducked: Float32
    }

    private var saved: Saved?

    func duck(to level: Double) {
        guard saved == nil, let id = AudioDevices.defaultOutput(), let volume = AudioDevices.volume(id),
              let uid = AudioDevices.string(id, kAudioDevicePropertyDeviceUID) else { return }
        let target = Float32(Double(volume) * level)
        guard target < volume - 0.001 else { return }
        let entry = Saved(uid: uid, volume: volume, ducked: target)
        do {
            try JSONEncoder().encode(entry).write(to: Paths.ducked, options: .atomic)
        } catch {
            return // without the note on disk, a crash could leave the Mac quiet: better not to duck
        }
        if AudioDevices.setVolume(id, target) {
            saved = entry
        } else {
            try? FileManager.default.removeItem(at: Paths.ducked)
        }
    }

    func restore() {
        guard let entry = saved else { return }
        saved = nil
        Ducker.put(back: entry)
        try? FileManager.default.removeItem(at: Paths.ducked)
    }

    /// The volume goes back only if it is still where Dikte left it: a change the user made meanwhile stays.
    private static func put(back entry: Saved) {
        guard let id = AudioDevices.device(uid: entry.uid), let now = AudioDevices.volume(id) else { return }
        if abs(now - entry.ducked) < 0.02 { AudioDevices.setVolume(id, entry.volume) }
    }

    /// After a crash: puts back a volume the last run turned down and never restored.
    static func recoverFromCrash() {
        guard let data = try? Data(contentsOf: Paths.ducked) else { return }
        if let entry = try? JSONDecoder().decode(Saved.self, from: data) {
            put(back: entry)
            Log.write("Restored the output volume left down by the last run")
        }
        try? FileManager.default.removeItem(at: Paths.ducked)
    }
}
