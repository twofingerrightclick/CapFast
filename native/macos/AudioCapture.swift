import AVFoundation
import Foundation
import CoreAudio
import AudioToolbox
import SoundAnalysis

// Default stdout is raw PCM16. --activity uses fixed 3201-byte packets:
// one local class byte (0 unknown/noise, 1 speech, 2 music/singing), then 100 ms PCM.
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
var activityMode = false
var activityFile: String?
var argumentIndex = 0
while argumentIndex < arguments.count {
    switch arguments[argumentIndex] {
    case "--activity":
        guard !activityMode else { fail("MIC_ARGUMENT_ERROR") }
        activityMode = true
        argumentIndex += 1
    case "--device", "--activity-file":
        guard argumentIndex + 1 < arguments.count, !arguments[argumentIndex + 1].isEmpty else { fail("MIC_ARGUMENT_ERROR") }
        if arguments[argumentIndex] == "--device" {
            guard selectedDeviceUID == nil else { fail("MIC_ARGUMENT_ERROR") }
            selectedDeviceUID = arguments[argumentIndex + 1]
        } else {
            guard activityFile == nil else { fail("MIC_ARGUMENT_ERROR") }
            activityFile = arguments[argumentIndex + 1]
            activityMode = true
        }
        argumentIndex += 2
    default: fail("MIC_ARGUMENT_ERROR")
    }
}
guard activityFile == nil || selectedDeviceUID == nil else { fail("MIC_ARGUMENT_ERROR") }

let engine = AVAudioEngine()
let output = FileHandle.standardOutput
let pendingLock = NSLock()
var pending: [AVAudioPCMBuffer] = []
let available = DispatchSemaphore(value: 0)
var captureStarted = false

// Analyze away from the realtime audio tap. Hold each packet until all overlapping
// classification windows that cover it have completed; a music veto always wins.
final class ActivityClassifier: NSObject, SNResultsObserving {
    struct Frame {
        let start: Int64
        let data: Data
        var speech = false
        var music = false
        var covered = false
        let audible: Bool
    }
    let analyzer: SNAudioStreamAnalyzer
    let request: SNClassifySoundRequest
    let lock = NSLock()
    let completion = DispatchSemaphore(value: 0)
    var frames: [Frame] = []
    var bytes = Data()
    var position: Int64 = 0
    var nextFrame: Int64 = 0
    var lastResultEnd: Int64 = 0
    let overlapSamples: Int64

    init(format: AVAudioFormat) {
        do { request = try SNClassifySoundRequest(classifierIdentifier: .version1) }
        catch { fail("MIC_CLASSIFIER_ERROR") }
        request.windowDuration = CMTime(seconds: 1, preferredTimescale: 16000)
        request.overlapFactor = 0.5
        overlapSamples = Int64((CMTimeGetSeconds(request.windowDuration) * 16000 * 0.5).rounded())
        analyzer = SNAudioStreamAnalyzer(format: format)
        super.init()
        guard ["speech", "music", "singing", "choir_singing"].allSatisfy({ request.knownClassifications.contains($0) }) else { fail("MIC_CLASSIFIER_ERROR") }
        do { try analyzer.add(request, withObserver: self) }
        catch { fail("MIC_CLASSIFIER_ERROR") }
    }

    func append(_ buffer: AVAudioPCMBuffer, pcm: Data) {
        lock.lock()
        bytes.append(pcm)
        while bytes.count >= 3200 {
            let data = Data(bytes.prefix(3200))
            // Silence rejection complements the semantic classifier; energy alone
            // never qualifies a packet as speech. Exact-zero device silence is common.
            let energy = data.withUnsafeBytes { raw -> Double in
                var sum = 0.0
                for offset in stride(from: 0, to: 3200, by: 2) {
                    let value = Double(Int16(littleEndian: raw.loadUnaligned(fromByteOffset: offset, as: Int16.self))) / 32768
                    sum += value * value
                }
                return sqrt(sum / 1600)
            }
            frames.append(Frame(start: nextFrame, data: data, audible: energy >= 0.0005))
            bytes.removeFirst(3200)
            nextFrame += 1600
        }
        if frames.count > 60 { lock.unlock(); fail("MIC_CLASSIFIER_ERROR") }
        let start = position
        position += Int64(buffer.frameLength)
        lock.unlock()
        analyzer.analyze(buffer, atAudioFramePosition: start)
    }

    func request(_ request: SNRequest, didProduce result: SNResult) {
        guard let result = result as? SNClassificationResult else { fail("MIC_CLASSIFIER_ERROR") }
        let start = Int64((CMTimeGetSeconds(result.timeRange.start) * 16000).rounded())
        let end = Int64((CMTimeGetSeconds(CMTimeRangeGetEnd(result.timeRange)) * 16000).rounded())
        let speech = result.classifications.contains { $0.identifier == "speech" && $0.confidence >= 0.5 }
        let music = result.classifications.contains {
            ($0.identifier == "music" || $0.identifier == "singing" || $0.identifier == "choir_singing") && $0.confidence >= 0.5
        }
        lock.lock()
        lastResultEnd = max(lastResultEnd, end)
        for index in frames.indices where frames[index].start < end && frames[index].start + 1600 > start {
            frames[index].covered = true
            frames[index].speech = frames[index].speech || speech
            frames[index].music = frames[index].music || music
        }
        emitThrough(end - overlapSamples)
        lock.unlock()
    }

    // Called only under lock, so stream packets cannot interleave.
    func emitThrough(_ boundary: Int64) {
        while let frame = frames.first, frame.start + 1600 <= boundary {
            frames.removeFirst()
            var packet = Data([frame.music ? 2 : (frame.covered && frame.speech && frame.audible ? 1 : 0)])
            packet.append(frame.data)
            do { try output.write(contentsOf: packet) }
            catch { fail("MIC_PIPE_CLOSED") }
        }
    }
    func finish() {
        analyzer.completeAnalysis()
        guard completion.wait(timeout: .now() + 30) == .success else { fail("MIC_CLASSIFIER_ERROR") }
        lock.lock()
        emitThrough(position)
        lock.unlock()
    }
    func request(_ request: SNRequest, didFailWithError error: Error) { fail("MIC_CLASSIFIER_ERROR") }
    func requestDidComplete(_ request: SNRequest) { completion.signal() }
}

let targetFormat = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16000, channels: 1, interleaved: false)!
let classifier = activityMode ? ActivityClassifier(format: targetFormat) : nil
func processBuffer(_ buffer: AVAudioPCMBuffer) {
    guard let samples = buffer.floatChannelData?[0] else { fail("MIC_CONVERSION_ERROR") }
    var pcmData = Data(capacity: Int(buffer.frameLength) * 2)
    for index in 0..<Int(buffer.frameLength) {
        let sample = samples[index]
        let value = sample.isFinite ? max(-1, min(1, sample)) : 0
        var pcm = Int16(value * 32767).littleEndian
        withUnsafeBytes(of: &pcm) { pcmData.append(contentsOf: $0) }
    }
    if let classifier { classifier.append(buffer, pcm: pcmData) }
    else {
        do { try output.write(contentsOf: pcmData) }
        catch { fail("MIC_PIPE_CLOSED") }
    }
}

if let path = activityFile {
    // Offline local classifier check: no engine, microphone permission, or network.
    do {
        let file = try AVAudioFile(forReading: URL(fileURLWithPath: path))
        if file.processingFormat == targetFormat {
            while file.framePosition < file.length {
                let buffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: 1600)!
                try file.read(into: buffer)
                if buffer.frameLength == 0 { break }
                processBuffer(buffer)
            }
            classifier!.finish()
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
        classifier!.finish()
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
