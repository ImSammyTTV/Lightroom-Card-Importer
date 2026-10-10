import Foundation
import AppKit
import ImageCaptureCore

// Phone support (iPhone, iPad and Android), through macOS's own Image Capture framework (the same thing the
// Photos app and Image Capture use). Off by default. When it's switched on, every device has to be approved by
// you before anything is read from it, only the camera photos and videos are looked at, and nothing is ever
// deleted from or changed on the device.
// Android phones work when their USB mode is "Photo transfer (PTP)": macOS can read that mode natively, but
// not the usual "File transfer (MTP)" mode.

enum PhoneAnswer { case always, once, no }

extension ImportModel {
    /// Approved devices: id -> name. Kept in the app's preferences.
    var approvedPhones: [String: String] {
        get { defaults.dictionary(forKey: "approvedPhones") as? [String: String] ?? [:] }
        set { defaults.set(newValue, forKey: "approvedPhones") }
    }

    func setPhonesEnabled(_ on: Bool) {
        if on {
            NSApplication.shared.activate(ignoringOtherApps: true)
            let a = NSAlert()
            a.messageText = "Turn on phone import?"
            a.informativeText = "Card Importer will look for iPhones, iPads and Android phones connected by USB. The first time each one connects you'll be asked to allow it.\n\nIt only reads the camera photos and videos, and never deletes or changes anything on the device.\n\nOn an Android phone, set the USB mode to Photo transfer (PTP) so your Mac can read it."
            a.addButton(withTitle: "Turn on")
            a.addButton(withTitle: "Cancel")
            guard a.runModal() == .alertFirstButtonReturn else { return }
            phonesEnabled = true
            phoneImporter.start()
        } else {
            phonesEnabled = false
            phoneImporter.stop()
        }
    }

    func forgetPhones() {
        approvedPhones = [:]
        notify("Phones forgotten", "Each device will ask for permission again the next time it connects.")
    }

    func askPhonePermission(name: String) -> PhoneAnswer {
        NSApplication.shared.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = "Allow access to \(name)?"
        a.informativeText = "Card Importer would like to copy photos and videos from this device into your photo library.\n\nIt only reads the camera photos. It never deletes or changes anything on the device.\n\nYou may also need to unlock the device and tap Trust (iPhone) or choose Photo transfer / PTP (Android)."
        a.addButton(withTitle: "Always allow")
        a.addButton(withTitle: "Just this time")
        a.addButton(withTitle: "Don't allow")
        switch a.runModal() {
        case .alertFirstButtonReturn: return .always
        case .alertSecondButtonReturn: return .once
        default: return .no
        }
    }

    /// Moves a downloaded photo into <library>/YYYY/YYYY-MM-DD, keeping the device's modified time.
    func storePhoneFile(_ tmp: URL, name: String, size: Int64, mtime: Date, lib: URL) throws {
        let fm = FileManager.default
        let c = Calendar.current.dateComponents([.year, .month, .day], from: mtime)
        let folder = lib
            .appendingPathComponent(String(format: "%04d", c.year ?? 0))
            .appendingPathComponent(String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0))
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)

        let base = (name as NSString).deletingPathExtension
        let ext = (name as NSString).pathExtension
        var dest = folder.appendingPathComponent(name)
        var n = 1
        while fm.fileExists(atPath: dest.path) {
            dest = folder.appendingPathComponent("\(base)_\(n).\(ext)")
            n += 1
        }
        try fm.moveItem(at: tmp, to: dest)
        try? fm.setAttributes([.modificationDate: mtime], ofItemAtPath: dest.path)

        let k = key(CardFile(url: dest, name: name, size: size, mtime: mtime))
        done.insert(k)
        appendLine(k, to: logURL)
    }
}

final class PhoneImporter: NSObject, ICDeviceBrowserDelegate, ICCameraDeviceDelegate {
    private let browser = ICDeviceBrowser()
    private unowned let model: ImportModel
    private var cameras: [String: ICCameraDevice] = [:]   // connected devices by id (main thread only)
    private var allowedThisTime = Set<String>()
    private var wantImport = Set<String>()                 // waiting for the device's file list before importing

    init(model: ImportModel) {
        self.model = model
        super.init()
        browser.delegate = self
        browser.browsedDeviceTypeMask = ICDeviceTypeMask(rawValue: ICDeviceTypeMask.camera.rawValue | ICDeviceLocationTypeMask.local.rawValue)!
    }

    func start() { browser.start() }

    func stop() {
        browser.stop()
        for (_, c) in cameras { c.requestCloseSession() }
        cameras.removeAll()
        allowedThisTime.removeAll()
        wantImport.removeAll()
        publish()
    }

    private func id(_ d: ICDevice) -> String { d.uuidString ?? d.name ?? "device" }
    private func isApproved(_ id: String) -> Bool { model.approvedPhones[id] != nil || allowedThisTime.contains(id) }

    private func publish() {
        let lines = cameras.values.map { cam -> String in
            "●  \(cam.name ?? "Device")  ·  phone  ·  " + (isApproved(id(cam)) ? "allowed" : "needs your permission")
        }.sorted()
        DispatchQueue.main.async { self.model.phoneLines = lines }
    }

    /// "Import now": ask about (if needed) and import from every connected device.
    func importNow() {
        for cam in cameras.values { arrived(cam) }
    }

    private func arrived(_ cam: ICCameraDevice) {
        guard model.phonesEnabled, !model.running else { return }
        let key = id(cam)
        if !isApproved(key) {
            switch model.askPhonePermission(name: cam.name ?? "this device") {
            case .always: model.approvedPhones[key] = cam.name ?? "Device"
            case .once: allowedThisTime.insert(key)
            case .no: return
            }
            publish()
        }
        wantImport.insert(key)
        if cam.hasOpenSession { run(cam) } else { cam.requestOpenSession() }
    }

    // MARK: - importing

    private func run(_ cam: ICCameraDevice) {
        let key = id(cam)
        guard wantImport.remove(key) != nil, !model.running else { return }
        model.running = true
        model.busy = true
        model.finished = false
        let name = cam.name ?? "Device"
        model.report("Reading \(name)", "Checking what's on the device…", 0, force: true)

        let all = (cam.mediaFiles ?? []).compactMap { $0 as? ICCameraFile }
        if all.isEmpty {
            let hint = "No photos found. Unlock the device, tap Trust (iPhone) or choose Photo transfer / PTP (Android), then try Import now."
            model.report("Done", hint, 100, force: true)
            model.addHistory("\(name)  nothing found")
            model.notify("Nothing found on \(name)", hint)
            model.running = false
            model.busy = false
            cam.requestCloseSession()
            return
        }
        let lib = URL(fileURLWithPath: model.libraryPath)
        model.queue.async {
            // work out which files are new; this reads the whole library folder, so keep it off the main thread
            let index = self.model.libraryIndex(lib)
            var todo: [(ICCameraFile, CardFile)] = []
            for f in all {
                let fname = f.name ?? "file"
                guard self.model.wanted((fname as NSString).pathExtension) else { continue }
                let cf = CardFile(url: URL(fileURLWithPath: "/"), name: fname, size: Int64(f.fileSize),
                                  mtime: f.modificationDate ?? f.creationDate ?? Date())
                if index[self.model.nameSize(cf)] != nil || index[self.model.sizeTime(cf)] != nil { continue }
                if self.model.done.contains(self.model.key(cf)) { continue }
                todo.append((f, cf))
            }
            DispatchQueue.main.async { self.download(cam, name: name, todo: todo, lib: lib) }
        }
    }

    // Downloads one file at a time into a temporary folder (callbacks arrive on the main thread), checks the
    // size, then moves it into the library. A device can't give us the original bytes to hash, so size is the check.
    private func download(_ cam: ICCameraDevice, name: String, todo: [(ICCameraFile, CardFile)], lib: URL) {
        let fm = FileManager.default
        let tmp = fm.temporaryDirectory.appendingPathComponent("CardImporterPhone-\(UUID().uuidString)")
        try? fm.createDirectory(at: tmp, withIntermediateDirectories: true)
        let totalBytes = Double(max(1, todo.reduce(Int64(0)) { $0 + $1.1.size }))
        var copied = 0
        var failed = 0
        var soFar: Int64 = 0

        func finish() {
            try? fm.removeItem(at: tmp)
            var summary = todo.isEmpty ? "nothing new" : "\(copied) new files copied"
            if failed > 0 { summary += "; \(failed) failed" }
            summary += "; nothing deleted from the device"
            model.report("Done", summary, 100, force: true)
            model.addHistory("\(name)  \(summary)")
            model.notify(todo.isEmpty ? "Nothing new on \(name)" : "Import complete", summary)
            model.running = false
            model.busy = false
            cam.requestCloseSession()
        }

        func next(_ i: Int) {
            guard i < todo.count else { finish(); return }
            let (file, cf) = todo[i]
            model.report("Copying \(i + 1) of \(todo.count)", cf.name, 100.0 * Double(soFar) / totalBytes, force: true)
            let options: [ICDownloadOption: Any] = [
                .downloadsDirectoryURL: tmp,
                .saveAsFilename: cf.name,
                .overwrite: true,
            ]
            _ = file.requestDownload(options: options) { savedName, error in
                if error == nil, let savedName = savedName {
                    let url = tmp.appendingPathComponent(savedName)
                    let size = ((try? fm.attributesOfItem(atPath: url.path)[.size]) as? NSNumber)?.int64Value ?? -1
                    // an iPhone can convert HEIC to JPEG while sending, so a renamed file only has to be non-empty
                    let ok = size == cf.size || (savedName != cf.name && size > 0)
                    if ok, (try? self.model.storePhoneFile(url, name: savedName, size: cf.size, mtime: cf.mtime, lib: lib)) != nil {
                        copied += 1
                    } else {
                        failed += 1
                    }
                } else {
                    failed += 1
                }
                soFar += cf.size
                next(i + 1)
            }
        }
        next(0)
    }

    // MARK: - ICDeviceBrowserDelegate

    func deviceBrowser(_ browser: ICDeviceBrowser, didAdd device: ICDevice, moreComing: Bool) {
        guard let cam = device as? ICCameraDevice else { return }
        cameras[id(cam)] = cam
        cam.delegate = self
        publish()
        DispatchQueue.main.async { self.arrived(cam) }
    }

    func deviceBrowser(_ browser: ICDeviceBrowser, didRemove device: ICDevice, moreGoing: Bool) {
        let key = id(device)
        cameras.removeValue(forKey: key)
        allowedThisTime.remove(key)   // "just this time" ends when it's unplugged
        wantImport.remove(key)
        publish()
    }

    // MARK: - ICDeviceDelegate

    func didRemove(_ device: ICDevice) {
        cameras.removeValue(forKey: id(device))
        publish()
    }

    func device(_ device: ICDevice, didOpenSessionWithError error: Error?) {}
    func device(_ device: ICDevice, didCloseSessionWithError error: Error?) {}
    func deviceDidBecomeReady(_ device: ICDevice) {}

    // MARK: - ICCameraDeviceDelegate

    func deviceDidBecomeReady(withCompleteContentCatalog device: ICCameraDevice) {
        DispatchQueue.main.async { self.run(device) }
    }

    func cameraDevice(_ camera: ICCameraDevice, didAdd items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRemove items: [ICCameraItem]) {}
    func cameraDevice(_ camera: ICCameraDevice, didRenameItems items: [ICCameraItem]) {}
    func cameraDeviceDidChangeCapability(_ camera: ICCameraDevice) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveThumbnail thumbnail: CGImage?, for item: ICCameraItem, error: Error?) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceiveMetadata metadata: [AnyHashable: Any]?, for item: ICCameraItem, error: Error?) {}
    func cameraDevice(_ camera: ICCameraDevice, didCompleteDeleteFilesWithError error: Error?) {}
    func cameraDevice(_ camera: ICCameraDevice, didReceivePTPEvent eventData: Data) {}
    func cameraDeviceDidRemoveAccessRestriction(_ device: ICDevice) {}
    func cameraDeviceDidEnableAccessRestriction(_ device: ICDevice) {}
}
