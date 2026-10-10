import Foundation
import AppKit
import CryptoKit
import ServiceManagement
import UserNotifications

struct Card: Identifiable {
    let url: URL
    var id: String { url.path }
    let name: String
    let usedGB: Double
}

struct CardFile {
    let url: URL
    let name: String
    let size: Int64
    let mtime: Date
}

enum ImportError: LocalizedError {
    case verifyFailed
    case badChecksum
    case badArchive
    var errorDescription: String? {
        switch self {
        case .verifyFailed: return "Verification failed after copy"
        case .badChecksum: return "The download didn't match its checksum, so it was not installed."
        case .badArchive: return "The download couldn't be unpacked."
        }
    }
}

/// Copies new photos/videos from any inserted card with a DCIM folder into
/// <library>/YYYY/YYYY-MM-DD, verifying every copy by SHA-256. Optionally deletes files
/// from the card afterwards, but only files proven to exist in the library with an identical hash.
final class ImportModel: ObservableObject {
    @Published var phase = "Waiting for a card"
    @Published var detail = "Plug in your camera or drone, or choose Import now."
    @Published var percent: Double = 0
    @Published var busy = false
    @Published var finished = false
    @Published var history: [String] = []
    @Published var cards: [Card] = []
    @Published var launchAtLogin = false
    @Published var updateText = ""
    @Published var updateAvailable = false
    @Published var autoUpdate: Bool { didSet { defaults.set(autoUpdate, forKey: "autoUpdate") } }
    @Published var importRaw: Bool { didSet { defaults.set(importRaw, forKey: "importRaw") } }
    @Published var importJpeg: Bool { didSet { defaults.set(importJpeg, forKey: "importJpeg") } }
    @Published var importVideo: Bool { didSet { defaults.set(importVideo, forKey: "importVideo") } }
    @Published var phonesEnabled: Bool { didSet { defaults.set(phonesEnabled, forKey: "phonesEnabled") } }
    @Published var phoneLines: [String] = []
    @Published var deleteAfter: Bool { didSet { defaults.set(deleteAfter, forKey: "deleteAfter") } }
    @Published var libraryPath: String {
        didSet {
            defaults.set(libraryPath, forKey: "libraryPath")
            writeLibraryHint()
        }
    }

    // File types, so you can choose what to import. Anything not picked is left alone on the card.
    static let rawExts: Set<String> = ["arw", "dng", "cr2", "cr3", "nef", "raf", "orf", "rw2", "srf", "sr2", "pef"]
    static let jpegExts: Set<String> = ["jpg", "jpeg", "heic", "heif", "hif"]
    static let videoExts: Set<String> = ["mp4", "mov", "mxf", "srt", "lrf"]
    static let exts: Set<String> = rawExts.union(jpegExts).union(videoExts)

    func wanted(_ ext: String) -> Bool {
        let e = ext.lowercased()
        if Self.rawExts.contains(e) { return importRaw }
        if Self.jpegExts.contains(e) { return importJpeg }
        if Self.videoExts.contains(e) { return importVideo }
        return false
    }

    let defaults = UserDefaults.standard
    let queue = DispatchQueue(label: "cardimporter.work", qos: .utility)
    let logURL: URL
    private let historyURL: URL
    private let libraryHintURL: URL
    var done = Set<String>()        // only touched on `queue`
    var running = false             // only touched on the main thread
    private var lastReport = Date.distantPast // only touched on `queue`
    private var checkingUpdate = false          // main thread only
    private var updateNotified = false
    private var updateInfo: (tag: String, zip: String, sums: String)?
    static let repo = "ImSammyTTV/lightroom-card-importer"
    lazy var phoneImporter = PhoneImporter(model: self)

    init() {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("CardImporter", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        logURL = dir.appendingPathComponent("copied.log")
        historyURL = dir.appendingPathComponent("history.log")
        libraryHintURL = dir.appendingPathComponent("library.txt")

        deleteAfter = defaults.object(forKey: "deleteAfter") as? Bool ?? true
        autoUpdate = defaults.object(forKey: "autoUpdate") as? Bool ?? true
        importRaw = defaults.object(forKey: "importRaw") as? Bool ?? true
        importJpeg = defaults.object(forKey: "importJpeg") as? Bool ?? true
        importVideo = defaults.object(forKey: "importVideo") as? Bool ?? true
        phonesEnabled = defaults.object(forKey: "phonesEnabled") as? Bool ?? false   // off until you turn it on
        libraryPath = defaults.string(forKey: "libraryPath")
            ?? (NSHomeDirectory() as NSString).appendingPathComponent("Pictures/Photography/Photos")

        if let s = try? String(contentsOf: logURL, encoding: .utf8) {
            done = Set(s.split(separator: "\n").map(String.init))
        }
        if let s = try? String(contentsOf: historyURL, encoding: .utf8) {
            history = Array(s.split(separator: "\n").map(String.init).reversed().prefix(4))
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
        writeLibraryHint() // lets the Lightroom plugin follow the same folder
        updateText = "Version \(Self.currentVersion)  ·  not checked yet"
        if phonesEnabled { phoneImporter.start() }

        // check about 30 seconds after launch, then re-check hourly but only act once a day
        let dailyCheck: () -> Void = { [weak self] in
            guard let self = self, self.autoUpdate else { return }
            if Date().timeIntervalSince1970 - self.defaults.double(forKey: "lastUpdateCheck") > 86400 {
                self.checkForUpdate(manual: false)
            }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 30, execute: dailyCheck)
        Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in dailyCheck() }

        if Bundle.main.bundleIdentifier != nil {
            UNUserNotificationCenter.current().requestAuthorization(options: [.alert]) { _, _ in }
        }

        refreshCards()
        // A card that is already in at launch is only imported via "Import now",
        // so starting the app never begins a copy (or a delete) by surprise.
        Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in self?.refreshCards() }
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didMountNotification, object: nil, queue: .main
        ) { [weak self] note in
            guard let self = self,
                  let url = note.userInfo?[NSWorkspace.volumeURLUserInfoKey] as? URL else { return }
            self.refreshCards()
            if self.hasDCIM(url) { self.startImport([url]) }
        }
    }

    /// ~/Library/Application Support/CardImporter/library.txt holds the photo folder for the Lightroom plugin.
    private func writeLibraryHint() {
        try? libraryPath.write(to: libraryHintURL, atomically: true, encoding: .utf8)
    }

    // MARK: - updates (from GitHub releases)

    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0"
    }

    static func isNewer(_ tag: String, than current: String) -> Bool {
        let a = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0
            let y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private struct Release: Decodable {
        struct Asset: Decodable { let name: String; let browser_download_url: String }
        let tag_name: String
        let assets: [Asset]
    }

    private func setUpdate(_ text: String, _ available: Bool) {
        DispatchQueue.main.async {
            self.updateText = text
            self.updateAvailable = available
        }
    }

    func checkForUpdate(manual: Bool) {
        guard !checkingUpdate else { return }
        checkingUpdate = true
        setUpdate("Checking for updates…", false)
        Task {
            do {
                var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(Self.repo)/releases/latest")!)
                req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
                req.setValue("CardImporter/\(Self.currentVersion)", forHTTPHeaderField: "User-Agent")
                req.timeoutInterval = 20
                let (data, _) = try await URLSession.shared.data(for: req)
                let rel = try JSONDecoder().decode(Release.self, from: data)
                defaults.set(Date().timeIntervalSince1970, forKey: "lastUpdateCheck")
                if Self.isNewer(rel.tag_name, than: Self.currentVersion),
                   let zip = rel.assets.first(where: { $0.name == "CardImporter-mac.zip" }),
                   let sums = rel.assets.first(where: { $0.name == "SHA256SUMS.txt" }) {
                    updateInfo = (rel.tag_name, zip.browser_download_url, sums.browser_download_url)
                    setUpdate("Update available: \(rel.tag_name)", true)
                    if !updateNotified {
                        updateNotified = true
                        notify("Card Importer update available", "\(rel.tag_name) is ready. Open the menu bar window and choose Install update.")
                    }
                } else {
                    setUpdate("Version \(Self.currentVersion)  ·  up to date", false)
                }
            } catch {
                setUpdate(manual ? "Couldn't check for updates" : "Version \(Self.currentVersion)", false)
            }
            await MainActor.run { self.checkingUpdate = false }
        }
    }

    /// Downloads the new app, checks it against the release's SHA256SUMS.txt, then swaps it in after this
    /// app quits and reopens it. The old app is put back if the swap fails.
    func installUpdate() {
        guard let info = updateInfo, !checkingUpdate else { return }
        if running { notify("Import in progress", "Install the update once the import has finished."); return }
        let current = Bundle.main.bundleURL
        guard FileManager.default.isWritableFile(atPath: current.deletingLastPathComponent().path) else {
            setUpdate("Can't update here: move the app to a folder you own", true)
            return
        }
        checkingUpdate = true
        setUpdate("Downloading \(info.tag)…", false)
        Task {
            do {
                let fm = FileManager.default
                let tmp = fm.temporaryDirectory.appendingPathComponent("CardImporterUpdate-\(UUID().uuidString)")
                try fm.createDirectory(at: tmp, withIntermediateDirectories: true)
                let (downloaded, _) = try await URLSession.shared.download(from: URL(string: info.zip)!)
                let zipURL = tmp.appendingPathComponent("CardImporter-mac.zip")
                try fm.moveItem(at: downloaded, to: zipURL)

                let (sumsData, _) = try await URLSession.shared.data(from: URL(string: info.sums)!)
                var want: String?
                for line in String(decoding: sumsData, as: UTF8.self).split(separator: "\n") {
                    let p = line.split(whereSeparator: { $0 == " " || $0 == "*" })
                    if p.count == 2, p[1] == "CardImporter-mac.zip" { want = p[0].lowercased() }
                }
                let got = SHA256.hash(data: try Data(contentsOf: zipURL)).map { String(format: "%02x", $0) }.joined()
                guard let w = want, w == got else { throw ImportError.badChecksum }

                let unzip = Process()
                unzip.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
                unzip.arguments = ["-x", "-k", zipURL.path, tmp.path]
                try unzip.run()
                unzip.waitUntilExit()
                let newApp = tmp.appendingPathComponent("CardImporter.app")
                guard unzip.terminationStatus == 0, fm.fileExists(atPath: newApp.path) else { throw ImportError.badArchive }

                let cur = current.path, new = newApp.path
                let script = "sleep 1; mv \"\(cur)\" \"\(cur).old\" && mv \"\(new)\" \"\(cur)\" && rm -rf \"\(cur).old\"; " +
                             "[ -e \"\(cur)\" ] || mv \"\(cur).old\" \"\(cur)\"; open \"\(cur)\""
                let sh = Process()
                sh.executableURL = URL(fileURLWithPath: "/bin/sh")
                sh.arguments = ["-c", script]
                try sh.run()
                await MainActor.run { NSApplication.shared.terminate(nil) }
            } catch {
                setUpdate("Update failed: \(error.localizedDescription)", true)
                await MainActor.run { self.checkingUpdate = false }
            }
        }
    }

    // MARK: - UI actions

    func importNow() {
        refreshCards()
        if cards.isEmpty && phonesEnabled { phoneImporter.importNow() } else { startImport(cards.map { $0.url }) }
    }

    func chooseLibrary() {
        let p = NSOpenPanel()
        p.canChooseDirectories = true
        p.canChooseFiles = false
        p.canCreateDirectories = true
        p.prompt = "Choose"
        p.message = "Choose the folder photos are imported into (and that Lightroom reads from)."
        if p.runModal() == .OK, let url = p.url { libraryPath = url.path }
    }

    func openLibrary() {
        let url = URL(fileURLWithPath: libraryPath)
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        NSWorkspace.shared.open(url)
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {}
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }

    // MARK: - cards

    private func hasDCIM(_ volume: URL) -> Bool {
        FileManager.default.fileExists(atPath: volume.appendingPathComponent("DCIM").path)
    }

    func refreshCards() {
        let keys: [URLResourceKey] = [.volumeNameKey, .volumeTotalCapacityKey, .volumeAvailableCapacityKey]
        let vols = FileManager.default.mountedVolumeURLs(includingResourceValuesForKeys: keys, options: [.skipHiddenVolumes]) ?? []
        var out: [Card] = []
        for v in vols where v.path.hasPrefix("/Volumes/") && hasDCIM(v) {
            let r = try? v.resourceValues(forKeys: Set(keys))
            let used = Double((r?.volumeTotalCapacity ?? 0) - (r?.volumeAvailableCapacity ?? 0)) / 1_073_741_824
            out.append(Card(url: v, name: r?.volumeName ?? v.lastPathComponent, usedGB: used))
        }
        cards = out
    }

    // MARK: - import

    private func startImport(_ volumes: [URL]) {
        guard !running, !volumes.isEmpty else { return }
        running = true
        busy = true
        finished = false
        let lib = URL(fileURLWithPath: libraryPath)
        let del = deleteAfter
        queue.async {
            for v in volumes { self.importVolume(v, lib: lib, deleteAfter: del) }
            self.ui {
                self.running = false
                self.busy = false
                self.refreshCards()
            }
        }
    }

    private func ui(_ f: @escaping () -> Void) { DispatchQueue.main.async(execute: f) }

    func report(_ phase: String, _ detail: String, _ pct: Double, force: Bool = false) {
        let now = Date()
        if !force && now.timeIntervalSince(lastReport) < 0.15 { return }
        lastReport = now
        ui {
            self.phase = phase
            self.detail = detail
            self.percent = max(0, min(100, pct))
            self.finished = (phase == "Done")
        }
    }

    private func importVolume(_ v: URL, lib: URL, deleteAfter: Bool) {
        let name = v.lastPathComponent
        report("Scanning card", name, 0, force: true)
        Thread.sleep(forTimeInterval: 2) // let the card settle
        let files = listFiles(v.appendingPathComponent("DCIM"), onlyWanted: true)

        report("Checking library", "\(files.count) files on card", 0, force: true)
        let index = libraryIndex(lib)
        var todo: [CardFile] = []
        for f in files {
            if index[nameSize(f)] != nil || index[sizeTime(f)] != nil { continue } // already in the library
            if done.contains(key(f)) { continue } // copied before but since removed: leave it alone
            todo.append(f)
        }

        var copied = 0
        var clean = true
        let totalBytes = todo.reduce(Int64(0)) { $0 + $1.size }
        let total = Double(max(1, totalBytes * 2)) // each file is read once to copy and once to verify
        var soFar: Int64 = 0
        for (i, f) in todo.enumerated() {
            let base = soFar
            do {
                try copyVerified(f, lib: lib) { b in
                    self.report("Copying \(i + 1) of \(todo.count)", f.name, 100.0 * Double(base + b) / total)
                }
                copied += 1
            } catch {
                clean = false
                notify("Copy failed: \(f.name)", error.localizedDescription)
            }
            soFar += f.size * 2
        }

        var summary = "\(copied) new files copied"
        if deleteAfter && clean && isRemovable(v) {
            let (removed, kept) = deleteVerified(files, lib: lib)
            summary += "; \(removed) deleted from card" + (kept > 0 ? ", \(kept) kept (not verified)" : "")
        } else if deleteAfter && !clean {
            summary += "; card left untouched because of errors"
        }
        report("Done", summary, 100, force: true)
        addHistory("\(name)  \(summary)")
        notify(copied == 0 && todo.isEmpty ? "Nothing new on \(name)" : "Import complete", summary)
    }

    /// Copies into <lib>/YYYY/YYYY-MM-DD by capture date, hashing while reading, then re-reads the
    /// copy and refuses to keep it unless the hashes match.
    private func copyVerified(_ f: CardFile, lib: URL, progress: @escaping (Int64) -> Void) throws {
        let fm = FileManager.default
        let c = Calendar.current.dateComponents([.year, .month, .day], from: f.mtime)
        let folder = lib
            .appendingPathComponent(String(format: "%04d", c.year ?? 0))
            .appendingPathComponent(String(format: "%04d-%02d-%02d", c.year ?? 0, c.month ?? 0, c.day ?? 0))
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)

        let base = (f.name as NSString).deletingPathExtension
        let ext = (f.name as NSString).pathExtension
        var dest = folder.appendingPathComponent(f.name)
        var n = 1
        while fm.fileExists(atPath: dest.path) || fm.fileExists(atPath: dest.path + ".part") {
            dest = folder.appendingPathComponent("\(base)_\(n).\(ext)")
            n += 1
        }
        let part = URL(fileURLWithPath: dest.path + ".part") // .part so a half-copied file is never mistaken for a photo

        fm.createFile(atPath: part.path, contents: nil)
        let inp = try FileHandle(forReadingFrom: f.url)
        let out = try FileHandle(forWritingTo: part)
        var hasher = SHA256()
        var copiedBytes: Int64 = 0
        do {
            while true {
                let chunk: Data? = try autoreleasepool { try inp.read(upToCount: 4 * 1024 * 1024) }
                guard let data = chunk, !data.isEmpty else { break }
                try out.write(contentsOf: data)
                hasher.update(data: data)
                copiedBytes += Int64(data.count)
                progress(copiedBytes)
            }
            try inp.close()
            try out.close()
        } catch {
            try? inp.close()
            try? out.close()
            try? fm.removeItem(at: part)
            throw error
        }

        let srcHash = Data(hasher.finalize())
        let dstHash = try sha256(part, progress: { b in progress(f.size + b) })
        guard srcHash == dstHash else {
            try? fm.removeItem(at: part)
            throw ImportError.verifyFailed
        }
        try fm.moveItem(at: part, to: dest)
        try? fm.setAttributes([.modificationDate: f.mtime], ofItemAtPath: dest.path)

        let k = key(f)
        done.insert(k)
        appendLine(k, to: logURL)
    }

    /// Deletes a card file only if a file in the library has the same SHA-256.
    private func deleteVerified(_ files: [CardFile], lib: URL) -> (Int, Int) {
        report("Verifying card", "Indexing library", 0, force: true)
        let index = libraryIndex(lib)
        let totalBytes = max(1, files.reduce(Int64(0)) { $0 + $1.size })
        var soFar: Int64 = 0
        var removed = 0
        var kept = 0
        for (i, f) in files.enumerated() {
            let base = soFar
            let label = "Verifying & deleting \(i + 1) of \(files.count)"
            var cands = Set<String>()
            for k in [nameSize(f), sizeTime(f)] { for p in index[k] ?? [] { cands.insert(p) } }
            let matches = cands.filter { fileSize($0) == f.size }

            var safe = false
            if !matches.isEmpty,
               let h = try? sha256(f.url, progress: { b in
                   self.report(label, f.name, 100.0 * Double(base + b) / Double(totalBytes))
               }) {
                safe = matches.contains { (try? sha256(URL(fileURLWithPath: $0))) == h }
            }
            if safe {
                do { try FileManager.default.removeItem(at: f.url); removed += 1 } catch { safe = false }
            }
            if !safe { kept += 1 }
            soFar += f.size
            report(label, f.name, 100.0 * Double(soFar) / Double(totalBytes), force: true)
        }
        return (removed, kept)
    }

    // MARK: - helpers

    private func isRemovable(_ v: URL) -> Bool {
        let r = try? v.resourceValues(forKeys: [.volumeIsInternalKey, .volumeIsEjectableKey, .volumeIsRemovableKey])
        return r?.volumeIsInternal != true && (r?.volumeIsEjectable == true || r?.volumeIsRemovable == true)
    }

    func listFiles(_ dir: URL, onlyWanted: Bool = false) -> [CardFile] {
        let keys: [URLResourceKey] = [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]
        guard let en = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles]) else { return [] }
        var out: [CardFile] = []
        for case let u as URL in en {
            guard Self.exts.contains(u.pathExtension.lowercased()) else { continue }
            if onlyWanted && !wanted(u.pathExtension) { continue }
            guard let v = try? u.resourceValues(forKeys: Set(keys)),
                  v.isRegularFile == true, let size = v.fileSize, size > 0,
                  let m = v.contentModificationDate else { continue }
            out.append(CardFile(url: u, name: u.lastPathComponent, size: Int64(size), mtime: m))
        }
        return out
    }

    private func fileSize(_ path: String) -> Int64 {
        let v = try? URL(fileURLWithPath: path).resourceValues(forKeys: [.fileSizeKey])
        return Int64(v?.fileSize ?? -1)
    }

    // Size + modified time (2s tolerance, as card filesystems round) survives Lightroom renaming;
    // name + size catches files whose timestamps got changed.
    func nameSize(_ f: CardFile) -> String { "N|\(f.name.lowercased())|\(f.size)" }
    func sizeTime(_ f: CardFile) -> String { "T|\(f.size)|\(Int64(f.mtime.timeIntervalSince1970) / 2)" }
    func key(_ f: CardFile) -> String { "\(f.name)|\(f.size)|\(Int64(f.mtime.timeIntervalSince1970 * 1000))" }

    func libraryIndex(_ lib: URL) -> [String: [String]] {
        var idx: [String: [String]] = [:]
        for f in listFiles(lib) {
            idx[nameSize(f), default: []].append(f.url.path)
            let t = Int64(f.mtime.timeIntervalSince1970) / 2
            for d in [Int64(-1), 0, 1] { idx["T|\(f.size)|\(t + d)", default: []].append(f.url.path) }
        }
        return idx
    }

    private func sha256(_ url: URL, progress: ((Int64) -> Void)? = nil) throws -> Data {
        let h = try FileHandle(forReadingFrom: url)
        defer { try? h.close() }
        var hasher = SHA256()
        var total: Int64 = 0
        while true {
            let chunk: Data? = try autoreleasepool { try h.read(upToCount: 4 * 1024 * 1024) }
            guard let data = chunk, !data.isEmpty else { break }
            hasher.update(data: data)
            total += Int64(data.count)
            progress?(total)
        }
        return Data(hasher.finalize())
    }

    func appendLine(_ s: String, to url: URL) {
        guard let d = (s + "\n").data(using: .utf8) else { return }
        if let h = try? FileHandle(forWritingTo: url) {
            _ = try? h.seekToEnd()
            try? h.write(contentsOf: d)
            try? h.close()
        } else {
            try? d.write(to: url)
        }
    }

    func addHistory(_ text: String) {
        let f = DateFormatter()
        f.dateFormat = "d MMM h:mm a"
        let line = "\(f.string(from: Date()))  ·  \(text)"
        appendLine(line, to: historyURL)
        ui { self.history.insert(line, at: 0) }
    }

    func notify(_ title: String, _ body: String) {
        guard Bundle.main.bundleIdentifier != nil else { return }
        let c = UNMutableNotificationContent()
        c.title = title
        c.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }
}
