import AVFoundation
import Foundation
import CoreAudio
import AudioToolbox

// Stdout is exclusively raw little-endian 16 kHz mono PCM16; stderr has fixed codes.
func fail(_ code: String) -> Never {
    FileHandle.standardError.write(Data((code + "\n").utf8))
    exit(2)
}

struct AudioInputDescription: Codable {
    let id: String
    let name: String
}

func stringProperty(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr,
          let value else { return nil }
    // CoreAudio property headers require the caller to release these returned CFObjects.
    return value.takeRetainedValue() as String
}

func inputDevices() -> [(device: AudioDeviceID, description: AudioInputDescription)] {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { fail("MIC_ENUMERATION_ERROR") }
    if size == 0 { return [] }
    var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    let status = devices.withUnsafeMutableBytes {
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, $0.baseAddress!)
    }
    guard status == noErr else { fail("MIC_ENUMERATION_ERROR") }
    return devices.compactMap { device in
        var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeInput, mElement: kAudioObjectPropertyElementMain)
        var streamSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &streams, 0, nil, &streamSize) == noErr, streamSize > 0,
              let uid = stringProperty(device, kAudioDevicePropertyDeviceUID),
              let name = stringProperty(device, kAudioObjectPropertyName) else { return nil }
        return (device, AudioInputDescription(id: uid, name: name))
    }.sorted { $0.description.name.localizedCaseInsensitiveCompare($1.description.name) == .orderedAscending }
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments == ["--list-inputs"] {
    // Enumeration never creates an engine, requests permission, or captures audio.
    do { try FileHandle.standardOutput.write(contentsOf: JSONEncoder().encode(inputDevices().map { $0.description })) }
    catch { fail("MIC_ENUMERATION_ERROR") }
    exit(0)
}
if arguments == ["--request-permission"] {
    // Permission preflight is separate from capture and can run before cloud setup.
    func granted() -> Never {
        FileHandle.standardOutput.write(Data("MIC_PERMISSION_GRANTED\n".utf8))
        exit(0)
    }
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized: granted()
    case .notDetermined:
        AVCaptureDevice.requestAccess(for: .audio) { allowed in
            if allowed { granted() }
            else { fail("MIC_PERMISSION_DENIED") }
        }
    case .denied, .restricted: fail("MIC_PERMISSION_DENIED")
    @unknown default: fail("MIC_PERMISSION_DENIED")
    }
    DispatchQueue.main.asyncAfter(deadline: .now() + 60) { fail("MIC_PERMISSION_TIMEOUT") }
    RunLoop.main.run()
    exit(2)
}
var selectedDeviceUID: String?
if !arguments.isEmpty {
    guard arguments.count == 2, arguments[0] == "--device", !arguments[1].isEmpty else { fail("MIC_ARGUMENT_ERROR") }
    selectedDeviceUID = arguments[1]
}

let engine = AVAudioEngine()
let output = FileHandle.standardOutput
let pendingLock = NSLock()
var pending: [Data] = []
let available = DispatchSemaphore(value: 0)
var captureStarted = false
DispatchQueue.global(qos: .userInitiated).async {
    while true {
        available.wait()
        pendingLock.lock()
        let data = pending.removeFirst()
        pendingLock.unlock()
        do { try output.write(contentsOf: data) }
        catch { fail("MIC_PIPE_CLOSED") }
    }
}

func startCapture() {
    let input = engine.inputNode
    if let uid = selectedDeviceUID {
        guard var device = inputDevices().first(where: { $0.description.id == uid })?.device,
              let unit = input.audioUnit else { fail("MIC_DEVICE_UNAVAILABLE") }
        // Select only this engine's input AudioUnit. Never change the system default device.
        guard AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                   &device, UInt32(MemoryLayout<AudioDeviceID>.size)) == noErr
        else { fail("MIC_DEVICE_UNAVAILABLE") }
    }
    let sourceFormat = input.outputFormat(forBus: 0)
    guard sourceFormat.sampleRate > 0, sourceFormat.channelCount > 0,
          let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false),
          let converter = AVAudioConverter(from: sourceFormat, to: targetFormat)
    else { fail("MIC_FORMAT_ERROR") }

    input.installTap(onBus: 0, bufferSize: 2048, format: sourceFormat) { buffer, _ in
        let capacity = AVAudioFrameCount(ceil(Double(buffer.frameLength) * 16000 / sourceFormat.sampleRate)) + 32
        guard let converted = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: capacity) else { fail("MIC_CONVERSION_ERROR") }
        var provided = false
        var error: NSError?
        let status = converter.convert(to: converted, error: &error) { _, state in
            if provided { state.pointee = .noDataNow; return nil }
            provided = true
            state.pointee = .haveData
            return buffer
        }
        guard status != .error, error == nil, let samples = converted.floatChannelData?[0] else { fail("MIC_CONVERSION_ERROR") }
        var data = Data(capacity: Int(converted.frameLength) * 2)
        for index in 0..<Int(converted.frameLength) {
            let sample = samples[index]
            let value = sample.isFinite ? max(-1, min(1, sample)) : 0
            var pcm = Int16(value * 32767).littleEndian
            withUnsafeBytes(of: &pcm) { data.append(contentsOf: $0) }
        }
        if data.isEmpty { return }
        pendingLock.lock()
        // Fail explicitly rather than accumulating stale audio under network backpressure.
        if pending.count >= 20 { pendingLock.unlock(); fail("MIC_BACKPRESSURE") }
        pending.append(data)
        pendingLock.unlock()
        available.signal()
    }
    do { try engine.start() } catch { fail("MIC_START_ERROR") }
    captureStarted = true
}

switch AVCaptureDevice.authorizationStatus(for: .audio) {
case .authorized: startCapture()
case .notDetermined:
    AVCaptureDevice.requestAccess(for: .audio) { granted in
        guard granted else { fail("MIC_PERMISSION_DENIED") }
        DispatchQueue.main.async { startCapture() }
    }
case .denied, .restricted: fail("MIC_PERMISSION_DENIED")
@unknown default: fail("MIC_PERMISSION_DENIED")
}
DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
    if !captureStarted { fail("MIC_PERMISSION_TIMEOUT") }
}
RunLoop.main.run()
