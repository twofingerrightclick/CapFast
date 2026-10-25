import AVFoundation
import Foundation
import CoreAudio
import AudioToolbox

// stdout is raw mono PCM16 at 16 kHz. Shared .NET Silero handles speech detection.
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

func inputDevices(scope: AudioObjectPropertyScope = kAudioDevicePropertyScopeInput) -> [(device: AudioDeviceID, description: AudioInputDescription)] {
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
        var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var streamSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &streams, 0, nil, &streamSize) == noErr, streamSize > 0,
              let uid = stringProperty(device, kAudioDevicePropertyDeviceUID),
              let name = stringProperty(device, kAudioObjectPropertyName) else { return nil }
        return (device, AudioInputDescription(id: uid, name: name))
    }.sorted { $0.description.name.localizedCaseInsensitiveCompare($1.description.name) == .orderedAscending }
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments == ["--list-inputs"] || arguments == ["--list-outputs"] {
    // Enumeration never creates an engine, requests permission, or captures audio.
    do { try FileHandle.standardOutput.write(contentsOf: JSONEncoder().encode(inputDevices(scope: arguments == ["--list-outputs"] ? kAudioDevicePropertyScopeOutput : kAudioDevicePropertyScopeInput).map { $0.description })) }
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
// Local monitoring uses independent engines for independently clocked input/output devices.
final class MonitorQueue {
    let player: AVAudioPlayerNode
    let lock = NSLock()
    var pending = 0
    init(player: AVAudioPlayerNode) { self.player = player }
    func append(_ source: AVAudioPCMBuffer) {
        lock.lock()
        guard pending < 4 else { lock.unlock(); return }
        pending += 1
        lock.unlock()
        guard let copy = AVAudioPCMBuffer(pcmFormat: source.format, frameCapacity: source.frameLength) else {
            completed()
            return
        }
        copy.frameLength = source.frameLength
        let from = UnsafeMutableAudioBufferListPointer(source.mutableAudioBufferList)
        let to = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        for index in 0..<from.count {
            if let destination = to[index].mData, let data = from[index].mData {
                memcpy(destination, data, Int(from[index].mDataByteSize))
            }
        }
        player.scheduleBuffer(copy, completionCallbackType: .dataPlayedBack) { [weak self] _ in self?.completed() }
    }
    func completed() {
        lock.lock()
        pending -= 1
        lock.unlock()
    }
}


// Fixed slots keep tap work bounded. A busy worker drops new buffers rather than
// blocking capture or building a stale monitoring backlog.
final class MonitorTapQueue {
    let lock = NSLock()
    let available = DispatchSemaphore(value: 0)
    let buffers: [AVAudioPCMBuffer]
    var busy = [Bool](repeating: false, count: 8)
    var ready = [Int](repeating: 0, count: 8)
    var readIndex = 0
    var writeIndex = 0

    init(format: AVAudioFormat) {
        buffers = (0..<8).map { _ in
            guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: 32768) else { fail("MIC_FORMAT_ERROR") }
            return buffer
        }
    }

    func append(_ source: AVAudioPCMBuffer) {
        guard lock.try() else { return }
        guard let index = busy.firstIndex(of: false), source.frameLength <= buffers[index].frameCapacity else {
            lock.unlock(); return
        }
        let copy = buffers[index]
        copy.frameLength = source.frameLength
        let from = UnsafeMutableAudioBufferListPointer(source.mutableAudioBufferList)
        let to = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
        for channel in 0..<from.count {
            if let destination = to[channel].mData, let data = from[channel].mData {
                memcpy(destination, data, Int(from[channel].mDataByteSize))
            }
        }
        busy[index] = true
        ready[writeIndex] = index
        writeIndex = (writeIndex + 1) % ready.count
        lock.unlock()
        available.signal()
    }

    func consume(_ body: (AVAudioPCMBuffer) -> Void) {
        while true {
            available.wait()
            lock.lock()
            let index = ready[readIndex]
            readIndex = (readIndex + 1) % ready.count
            lock.unlock()
            body(buffers[index])
            lock.lock()
            busy[index] = false
            lock.unlock()
        }
    }
}


if arguments.first == "--monitor" {
    guard arguments.count == 3 || arguments.count == 4 else { fail("MIC_ARGUMENT_ERROR") }
    let capture = AVAudioEngine()
    let playback = AVAudioEngine()
    let player = AVAudioPlayerNode()
    let queue = MonitorQueue(player: player)
    let listening = arguments.count == 4 && arguments[3] == "--listen"
    func select(_ uid: String, _ node: AVAudioIONode, _ scope: AudioObjectPropertyScope) {
        if uid.isEmpty { return }
        guard var id = inputDevices(scope: scope).first(where: { $0.description.id == uid })?.device,
              let unit = node.audioUnit,
              AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                   &id, UInt32(MemoryLayout<AudioDeviceID>.size)) == noErr else { fail("MONITOR_DEVICE_UNAVAILABLE") }
    }
    var monitorStarted = false
    func startMonitor() {
        select(arguments[1], capture.inputNode, kAudioDevicePropertyScopeInput)
        let format = capture.inputNode.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0 else { fail("MIC_FORMAT_ERROR") }
        if listening {
            select(arguments[2], playback.outputNode, kAudioDevicePropertyScopeOutput)
            playback.attach(player)
            playback.connect(player, to: playback.mainMixerNode, format: format)
            do { try playback.start() } catch { fail("MONITOR_OUTPUT_ERROR") }
            player.play()
        }
        let tapQueue = MonitorTapQueue(format: format)
        let workerReady = DispatchSemaphore(value: 0)
        DispatchQueue.global(qos: .userInitiated).async {
            workerReady.wait()
            // The ready marker distinguishes permission/startup from missing first audio.
            FileHandle.standardOutput.write(Data("MONITOR_READY\n".utf8))
            var frames = 0
            var peak: Float = 0
            tapQueue.consume { buffer in
                if listening { queue.append(buffer) }
                if let channels = buffer.floatChannelData {
                    for channel in 0..<Int(buffer.format.channelCount) {
                        for index in 0..<Int(buffer.frameLength) { peak = max(peak, abs(channels[channel][index])) }
                    }
                }
                frames += Int(buffer.frameLength)
                if frames >= Int(format.sampleRate / 10) {
                    let db = peak > 0 ? max(-60, 20 * log10(peak)) : -60
                    FileHandle.standardOutput.write(Data("\(db)\n".utf8))
                    frames = 0; peak = 0
                }
            }
        }
        capture.inputNode.installTap(onBus: 0, bufferSize: 1024, format: format) { buffer, _ in
            tapQueue.append(buffer)
        }
        do { try capture.start() } catch { fail("MIC_START_ERROR") }
        monitorStarted = true
        workerReady.signal()
        // Device selection can deliver a delayed configuration notification after
        // start(), even though the engine is running with the requested format.
        // Compare the live route instead of mistaking that notification for removal.
        // A real stop, format change, or reroute still ends this sound check.
        func deviceID(_ node: AVAudioIONode) -> AudioDeviceID {
            var id = AudioDeviceID(0)
            var size = UInt32(MemoryLayout<AudioDeviceID>.size)
            guard let unit = node.audioUnit,
                  AudioUnitGetProperty(unit, kAudioOutputUnitProperty_CurrentDevice,
                                       kAudioUnitScope_Global, 0, &id, &size) == noErr
            else { fail("MONITOR_DEVICE_UNAVAILABLE") }
            return id
        }
        for audio in listening ? [capture, playback] : [capture] {
            let node: AVAudioIONode = audio === capture ? audio.inputNode : audio.outputNode
            let expectedDevice = deviceID(node)
            let expectedFormat = audio === capture ? node.outputFormat(forBus: 0) : node.inputFormat(forBus: 0)
            _ = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: audio, queue: nil) { _ in
                // Engine inspection/teardown must not run on CoreAudio's callback queue.
                DispatchQueue.main.async {
                    let currentFormat = audio === capture ? node.outputFormat(forBus: 0) : node.inputFormat(forBus: 0)
                    guard audio.isRunning, deviceID(node) == expectedDevice,
                          currentFormat == expectedFormat else { fail("MONITOR_DEVICE_CHANGED") }
                }
            }
        }
    }
    DispatchQueue.global().async {
        // Parent closes stdin on stop/exit, including unexpected app termination.
        while !FileHandle.standardInput.availableData.isEmpty { }
        exit(0)
    }
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized: startMonitor()
    case .notDetermined:
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            guard granted else { fail("MIC_PERMISSION_DENIED") }
            DispatchQueue.main.async { startMonitor() }
        }
    default: fail("MIC_PERMISSION_DENIED")
    }
    DispatchQueue.main.asyncAfter(deadline: .now() + 60) {
        if !monitorStarted { fail("MIC_PERMISSION_TIMEOUT") }
    }
    RunLoop.main.run()
    exit(0)
}

var selectedDeviceUID: String?
var pcmFile: String?
var argumentIndex = 0
while argumentIndex < arguments.count {
    switch arguments[argumentIndex] {
    case "--device", "--pcm-file":
        guard argumentIndex + 1 < arguments.count, !arguments[argumentIndex + 1].isEmpty else { fail("MIC_ARGUMENT_ERROR") }
        if arguments[argumentIndex] == "--device" {
            guard selectedDeviceUID == nil else { fail("MIC_ARGUMENT_ERROR") }
            selectedDeviceUID = arguments[argumentIndex + 1]
        } else {
            guard pcmFile == nil else { fail("MIC_ARGUMENT_ERROR") }
            pcmFile = arguments[argumentIndex + 1]
        }
        argumentIndex += 2
    default: fail("MIC_ARGUMENT_ERROR")
    }
}
guard pcmFile == nil || selectedDeviceUID == nil else { fail("MIC_ARGUMENT_ERROR") }

let engine = AVAudioEngine()
let output = FileHandle.standardOutput
let pendingLock = NSLock()
var pending: [AVAudioPCMBuffer] = []
let available = DispatchSemaphore(value: 0)
var captureStarted = false

let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!
func processBuffer(_ buffer: AVAudioPCMBuffer) {
    guard let samples = buffer.floatChannelData?[0] else { fail("MIC_CONVERSION_ERROR") }
    var pcmData = Data(capacity: Int(buffer.frameLength) * 2)
    for index in 0..<Int(buffer.frameLength) {
        let sample = samples[index]
        let value = sample.isFinite ? max(-1, min(1, sample)) : 0
        var pcm = Int16(value * 32767).littleEndian
        withUnsafeBytes(of: &pcm) { pcmData.append(contentsOf: $0) }
    }
    do { try output.write(contentsOf: pcmData) }
    catch { fail("MIC_PIPE_CLOSED") }
}

if let path = pcmFile {
    // Offline PCM conversion: no microphone permission or network.
    do {
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        if file.processingFormat == targetFormat {
            while file.framePosition < file.length {
                let buffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: 1600)!
                try file.read(into: buffer)
                if buffer.frameLength == 0 { break }
                processBuffer(buffer)
            }
            exit(0)
        }
        guard let converter = AVAudioConverter(from: file.processingFormat, to: targetFormat) else { fail("MIC_FORMAT_ERROR") }
        var eof = false
        while !eof {
            let converted = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: 1600)!
            var conversionError: NSError?
            let status = converter.convert(to: converted, error: &conversionError) { count, state in
                if eof || file.framePosition >= file.length { eof = true; state.pointee = .endOfStream; return nil }
                let source = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: count)!
                do { try file.read(into: source) } catch { fail("MIC_CONVERSION_ERROR") }
                if source.frameLength == 0 { eof = true; state.pointee = .endOfStream; return nil }
                state.pointee = .haveData
                return source
            }
            guard status != .error, conversionError == nil else { fail("MIC_CONVERSION_ERROR") }
            if converted.frameLength > 0 { processBuffer(converted) }
        }
        exit(0)
    } catch { fail("MIC_FORMAT_ERROR") }
}
DispatchQueue.global(qos: .userInitiated).async {
    while true {
        available.wait()
        pendingLock.lock()
        let buffer = pending.removeFirst()
        pendingLock.unlock()
        processBuffer(buffer)
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
        guard status != .error, error == nil else { fail("MIC_CONVERSION_ERROR") }
        if converted.frameLength == 0 { return }
        pendingLock.lock()
        // Bound all queued capture audio; do not accumulate stale meeting audio.
        if pending.count >= 100 { pendingLock.unlock(); fail("MIC_BACKPRESSURE") }
        pending.append(converted)
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
