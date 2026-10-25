import AppKit
import AVFoundation
import CoreAudio
import AudioToolbox

struct Device {
    let id: AudioDeviceID
    let uid: String
    let name: String
}

func deviceString(_ id: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> String? {
    var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var value: Unmanaged<CFString>?
    var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
    guard AudioObjectGetPropertyData(id, &address, 0, nil, &size, &value) == noErr else { return nil }
    return value?.takeRetainedValue() as String?
}

func devices(scope: AudioObjectPropertyScope) -> [Device] {
    var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
    var size: UInt32 = 0
    guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size) == noErr, size > 0 else { return [] }
    var ids = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
    let result = ids.withUnsafeMutableBytes { AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, $0.baseAddress!) }
    guard result == noErr else { return [] }
    return ids.compactMap { id in
        var streams = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var bytes: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(id, &streams, 0, nil, &bytes) == noErr, bytes > 0,
              let uid = deviceString(id, kAudioDevicePropertyDeviceUID),
              let name = deviceString(id, kAudioObjectPropertyName) else { return nil }
        return Device(id: id, uid: uid, name: name)
    }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
}

// Separate engines let capture and playback use independent hardware clocks.
// Bound queued buffers so a stalled output cannot accumulate delayed audio.
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

final class AppDelegate: NSObject, NSApplicationDelegate {
    var window: NSWindow!
    let input = NSPopUpButton()
    let output = NSPopUpButton()
    let listen = NSButton(title: "Listen", target: nil, action: nil)
    let refresh = NSButton(title: "Refresh devices", target: nil, action: nil)
    let volume = NSSlider(value: 0.25, minValue: 0, maxValue: 1, target: nil, action: nil)
    let status = NSTextField(wrappingLabelWithString: "Ready to listen.")
    var engine: AVAudioEngine?
    var captureEngine: AVAudioEngine?
    var player: AVAudioPlayerNode?
    var captureObserver: NSObjectProtocol?
    var configurationObserver: NSObjectProtocol?
    var requestingPermission = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        let menu = NSMenu()
        let appItem = NSMenuItem()
        menu.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Quit Simple Listen", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        NSApp.mainMenu = menu

        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 440, height: 340), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "Simple Listen"
        window.isReleasedWhenClosed = false
        let title = NSTextField(labelWithString: "Listen to your input.")
        title.font = .systemFont(ofSize: 23, weight: .semibold)
        let hint = NSTextField(wrappingLabelWithString: "Use headphones to avoid microphone feedback.")
        hint.textColor = .secondaryLabelColor
        listen.target = self
        listen.action = #selector(toggle)
        listen.keyEquivalent = "\r"
        refresh.target = self
        refresh.action = #selector(refreshDevices)
        volume.target = self
        volume.action = #selector(changeVolume)
        volume.isContinuous = true
        input.setAccessibilityLabel("Audio input")
        output.setAccessibilityLabel("Audio output")
        volume.setAccessibilityLabel("Listening volume")
        let grid = NSGridView(views: [
            [NSTextField(labelWithString: "Input"), input],
            [NSTextField(labelWithString: "Output"), output],
            [NSTextField(labelWithString: "Volume"), volume]
        ])
        grid.rowSpacing = 12
        grid.columnSpacing = 16
        grid.column(at: 0).width = 52
        grid.column(at: 1).xPlacement = .fill
        let buttons = NSStackView(views: [listen, refresh])
        buttons.spacing = 12
        let stack = NSStackView(views: [title, hint, grid, buttons, status])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 18
        stack.translatesAutoresizingMaskIntoConstraints = false
        window.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: window.contentView!.topAnchor, constant: 24),
            grid.widthAnchor.constraint(equalTo: stack.widthAnchor),
            status.widthAnchor.constraint(equalTo: stack.widthAnchor)
        ])
        refreshDevices()
        window.center()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    @objc func refreshDevices() {
        for (picker, scope, key) in [(input, kAudioDevicePropertyScopeInput, "input"), (output, kAudioDevicePropertyScopeOutput, "output")] {
            let selected = picker.selectedItem?.representedObject as? String ?? UserDefaults.standard.string(forKey: key)
            picker.removeAllItems()
            picker.addItem(withTitle: "System default")
            picker.lastItem?.representedObject = ""
            for device in devices(scope: scope) {
                picker.addItem(withTitle: device.name)
                picker.lastItem?.representedObject = device.uid
            }
            if let selected, let item = picker.itemArray.first(where: { ($0.representedObject as? String) == selected }) { picker.select(item) }
        }
    }

    @objc func changeVolume() { engine?.mainMixerNode.outputVolume = volume.floatValue }

    func updateControls() {
        let active = engine != nil
        listen.title = active ? "Stop" : "Listen"
        listen.isEnabled = !requestingPermission
        input.isEnabled = !active && !requestingPermission
        output.isEnabled = !active && !requestingPermission
        refresh.isEnabled = !active && !requestingPermission
    }

    @objc func toggle() {
        if engine != nil { stop(); return }
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: start()
        case .notDetermined:
            requestingPermission = true
            status.stringValue = "Allow microphone access to listen."
            updateControls()
            AVCaptureDevice.requestAccess(for: .audio) { allowed in
                DispatchQueue.main.async {
                    self.requestingPermission = false
                    self.updateControls()
                    if allowed { self.start() } else { self.permissionDenied() }
                }
            }
        default: permissionDenied()
        }
    }

    func permissionDenied() {
        status.stringValue = "Enable Simple Listen in System Settings → Privacy & Security → Microphone, then reopen."
    }

    func selectDevice(_ picker: NSPopUpButton, node: AVAudioIONode, scope: AudioObjectPropertyScope) throws {
        let uid = picker.selectedItem?.representedObject as? String ?? ""
        guard !uid.isEmpty else { return }
        guard var id = devices(scope: scope).first(where: { $0.uid == uid })?.id, let unit = node.audioUnit else {
            throw NSError(domain: "SimpleListen", code: 1, userInfo: [NSLocalizedDescriptionKey: "Device disconnected. Refresh devices and choose another."])
        }
        let result = AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        guard result == noErr else {
            throw NSError(domain: "SimpleListen", code: Int(result), userInfo: [NSLocalizedDescriptionKey: "Could not open the selected device (\(result)). Choose another device."])
        }
    }

    func start() {
        let audio = AVAudioEngine()
        let capture = AVAudioEngine()
        do {
            try selectDevice(input, node: capture.inputNode, scope: kAudioDevicePropertyScopeInput)
            try selectDevice(output, node: audio.outputNode, scope: kAudioDevicePropertyScopeOutput)
            let format = capture.inputNode.outputFormat(forBus: 0)
            let outputFormat = audio.outputNode.inputFormat(forBus: 0)
            guard format.sampleRate > 0, format.channelCount > 0, outputFormat.sampleRate > 0, outputFormat.channelCount > 0 else {
                throw NSError(domain: "SimpleListen", code: 2, userInfo: [NSLocalizedDescriptionKey: "No usable audio input or output. Connect a device and refresh."])
            }
            let playback = AVAudioPlayerNode()
            audio.attach(playback)
            audio.connect(playback, to: audio.mainMixerNode, format: format)
            audio.mainMixerNode.outputVolume = volume.floatValue
            let queue = MonitorQueue(player: playback)
            capture.inputNode.installTap(onBus: 0, bufferSize: 512, format: format) { buffer, _ in
                queue.append(buffer)
            }
            try audio.start()
            playback.play()
            try capture.start()
            engine = audio
            captureEngine = capture
            player = playback
            captureObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: capture, queue: .main) { [weak self] _ in
                self?.stop()
                self?.status.stringValue = "Audio input changed. Refresh devices and click Listen again."
            }
            configurationObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: audio, queue: .main) { [weak self] _ in
                self?.stop()
                self?.status.stringValue = "Audio device changed. Refresh devices and click Listen again."
            }
            for (picker, key) in [(input, "input"), (output, "output")] {
                UserDefaults.standard.set(picker.selectedItem?.representedObject as? String, forKey: key)
            }
            status.stringValue = "Listening · \(input.titleOfSelectedItem ?? "Input")"
        } catch {
            capture.stop()
            audio.stop()
            status.stringValue = error.localizedDescription
        }
        updateControls()
    }

    func stop() {
        if let observer = configurationObserver { NotificationCenter.default.removeObserver(observer) }
        configurationObserver = nil
        if let observer = captureObserver { NotificationCenter.default.removeObserver(observer) }
        captureObserver = nil
        captureEngine?.stop()
        captureEngine?.inputNode.removeTap(onBus: 0)
        captureEngine = nil
        player?.stop()
        player = nil
        engine?.stop()
        engine = nil
        status.stringValue = "Stopped."
        updateControls()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { stop() }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.setActivationPolicy(.regular)
app.delegate = delegate
app.run()
