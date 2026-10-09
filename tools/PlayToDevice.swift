import AVFoundation
import Foundation
import CoreAudio
import AudioToolbox

// One-shot local test player. --dry-run and --list never create an audio engine.
func stop(_ message: String, _ code: Int32 = 2) -> Never {
    FileHandle.standardError.write(Data((message + "\n").utf8))
    exit(code)
}

func stringProperty(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr, let value else { return nil }
    return value.takeRetainedValue() as String
}

struct OutputDevice: Codable {
    let id: String
    let name: String
    let audioID: AudioDeviceID
}

func outputDevices() -> [OutputDevice] {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr else { stop("OUTPUT_ENUMERATION_FAILED") }
    if size == 0 { return [] }
    var devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    let status = devices.withUnsafeMutableBytes {
        AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, $0.baseAddress!)
    }
    guard status == noErr else { stop("OUTPUT_ENUMERATION_FAILED") }
    return devices.compactMap { device in
        var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeOutput, mElement: kAudioObjectPropertyElementMain)
        var streamSize: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &streams, 0, nil, &streamSize) == noErr, streamSize > 0,
              let uid = stringProperty(device, kAudioDevicePropertyDeviceUID),
              let name = stringProperty(device, kAudioObjectPropertyName) else { return nil }
        return OutputDevice(id: uid, name: name, audioID: device)
    }
}

let arguments = Array(CommandLine.arguments.dropFirst())
if arguments == ["--list"] {
    do { try FileHandle.standardOutput.write(contentsOf: JSONEncoder().encode(outputDevices())) }
    catch { stop("OUTPUT_ENUMERATION_FAILED") }
    exit(0)
}
guard arguments.count == 3, arguments[0] == "--dry-run" || arguments[0] == "--play" else {
    stop("Usage: PlayToDevice --list | --dry-run WAV DEVICE_UID | --play WAV DEVICE_UID")
}
guard let device = outputDevices().first(where: { $0.id == arguments[2] }) else { stop("OUTPUT_DEVICE_UNAVAILABLE") }
let file: AVAudioFile
do { file = try AVAudioFile(forReading: URL(fileURLWithPath: arguments[1])) }
catch { stop("AUDIO_FILE_UNREADABLE") }
let seconds = Double(file.length) / file.processingFormat.sampleRate
guard seconds.isFinite, seconds > 0, seconds <= 180 else { stop("AUDIO_DURATION_OUT_OF_BOUNDS") }
if arguments[0] == "--dry-run" {
    print("READY durationSeconds=\(seconds) timeoutSeconds=\(seconds + 8)")
    exit(0)
}

let engine = AVAudioEngine()
let player = AVAudioPlayerNode()
var deviceID = device.audioID
guard let unit = engine.outputNode.audioUnit,
      AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                           &deviceID, UInt32(MemoryLayout<AudioDeviceID>.size)) == noErr
else { stop("OUTPUT_DEVICE_SELECTION_FAILED") }
engine.attach(player)
engine.connect(player, to: engine.mainMixerNode, format: file.processingFormat)
player.scheduleFile(file, at: nil, completionCallbackType: .dataPlayedBack) { _ in
    DispatchQueue.main.async {
        player.stop()
        engine.stop()
        print("PLAYBACK_COMPLETE")
        exit(0)
    }
}
do { try engine.start() } catch { stop("OUTPUT_START_FAILED") }
player.play()
DispatchQueue.main.asyncAfter(deadline: .now() + seconds + 8) {
    player.stop()
    engine.stop()
    stop("PLAYBACK_TIMED_OUT")
}
RunLoop.main.run()
