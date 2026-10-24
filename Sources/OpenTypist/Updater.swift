import AppKit
import Combine
import CryptoKit

/// Updates from GitHub Releases. Checks the latest release of the repository
/// in Info.plist (`OTUpdateRepository`) at most once a day, or when asked.
/// Installing downloads the release's DMG, verifies it against the published
/// SHA-256, checks the app inside (bundle ID, version, code signature),
/// swaps it in place of the running app and relaunches.
///
/// The only data sent is a normal HTTPS request to api.github.com.
final class Updater: ObservableObject {
    static let shared = Updater()

    struct Release: Equatable {
        let version: String
        let notes: String
        let page: URL
        let dmg: URL
        let checksum: URL
    }

    enum State: Equatable {
        case idle
        case checking
        case upToDate
        case available(Release)
        case installing(String)
        case failed(String)
    }

    @Published private(set) var state: State = .idle
    @Published private(set) var lastCheck: Date? = UserDefaults.standard.object(forKey: "lastUpdateCheck") as? Date

    var repository: String {
        Bundle.main.object(forInfoDictionaryKey: "OTUpdateRepository") as? String ?? "mekanismvibe/opentypist"
    }

    var currentVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
    }

    var availableRelease: Release? {
        if case .available(let release) = state { return release }
        return nil
    }

    private var timer: Timer?

    /// Checks now if automatic checks are on and the last one is a day old,
    /// then keeps doing so while the app runs.
    func startAutomaticChecks() {
        checkIfDue()
        timer = Timer.scheduledTimer(withTimeInterval: 6 * 3600, repeats: true) { [weak self] _ in self?.checkIfDue() }
    }

    private func checkIfDue() {
        guard Settings.shared.checkForUpdates else { return }
        if let lastCheck, Date().timeIntervalSince(lastCheck) < 24 * 3600 { return }
        check()
    }

    // MARK: - Checking

    func check() {
        guard state != .checking, !isInstalling else { return }
        state = .checking
        Task {
            do {
                let release = try await fetchLatest()
                await MainActor.run {
                    self.lastCheck = Date()
                    UserDefaults.standard.set(self.lastCheck, forKey: "lastUpdateCheck")
                    if let release, Self.isNewer(release.version, than: self.currentVersion) {
                        self.state = .available(release)
                        AppStatus.shared.note("Update available: OpenTypist \(release.version)")
                    } else {
                        self.state = .upToDate
                    }
                }
            } catch {
                await MainActor.run { self.state = .failed("Couldn't check for updates: \(error.localizedDescription)") }
            }
        }
    }

    private struct GitHubRelease: Decodable {
        struct Asset: Decodable {
            let name: String
            let browser_download_url: URL
        }

        let tag_name: String
        let body: String?
        let html_url: URL
        let draft: Bool
        let prerelease: Bool
        let assets: [Asset]
    }

    private func fetchLatest() async throws -> Release? {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("OpenTypist/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        if (response as? HTTPURLResponse)?.statusCode == 404 { return nil } // no releases yet
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError("GitHub returned an error") }
        let release = try JSONDecoder().decode(GitHubRelease.self, from: data)
        guard !release.draft, !release.prerelease,
              let dmg = release.assets.first(where: { $0.name.hasSuffix(".dmg") }),
              let checksum = release.assets.first(where: { $0.name == dmg.name + ".sha256" }) else { return nil }
        let version = release.tag_name.hasPrefix("v") ? String(release.tag_name.dropFirst()) : release.tag_name
        return Release(version: version, notes: release.body ?? "", page: release.html_url,
                       dmg: dmg.browser_download_url, checksum: checksum.browser_download_url)
    }

    /// Numeric comparison of dotted versions ("0.10.0" > "0.9.2").
    static func isNewer(_ candidate: String, than current: String) -> Bool {
        let a = candidate.split(separator: ".").map { Int($0) ?? 0 }
        let b = current.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(a.count, b.count) {
            let x = i < a.count ? a[i] : 0, y = i < b.count ? b[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // MARK: - Installing

    private var isInstalling: Bool {
        if case .installing = state { return true }
        return false
    }

    struct UpdateError: LocalizedError {
        let errorDescription: String?
        init(_ message: String) { errorDescription = message }
    }

    func install() {
        guard let release = availableRelease else { return }
        state = .installing("Downloading \(release.version)…")
        Task {
            do {
                let newApp = try await prepare(release)
                await MainActor.run { self.state = .installing("Installing…") }
                try replaceRunningApp(with: newApp)
                await MainActor.run { self.relaunch() }
            } catch {
                await MainActor.run { self.state = .failed(error.localizedDescription) }
            }
        }
    }

    /// The latest release, if any (for `--update-test`).
    func latestRelease() async throws -> Release? {
        try await fetchLatest()
    }

    /// Downloads, verifies and unpacks the update; returns the new .app.
    func prepare(_ release: Release) async throws -> URL {
        let work = FileManager.default.temporaryDirectory.appendingPathComponent("OpenTypistUpdate-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)

        let (downloaded, _) = try await URLSession.shared.download(from: release.dmg)
        let dmg = work.appendingPathComponent("OpenTypist.dmg")
        try FileManager.default.moveItem(at: downloaded, to: dmg)
        let (checksumData, _) = try await URLSession.shared.data(from: release.checksum)
        let expected = String(decoding: checksumData, as: UTF8.self).split(separator: " ").first.map(String.init) ?? ""
        let actual = try Self.sha256(of: dmg)
        guard expected.count == 64, actual == expected.lowercased() else {
            throw UpdateError("The download didn't match its published checksum, so it wasn't installed.")
        }

        let mount = work.appendingPathComponent("mount")
        try Self.run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount.path, dmg.path])
        defer { try? Self.run("/usr/bin/hdiutil", ["detach", "-force", mount.path]) }

        let mounted = mount.appendingPathComponent("OpenTypist.app")
        let staged = work.appendingPathComponent("OpenTypist.app")
        try Self.run("/usr/bin/ditto", [mounted.path, staged.path])

        guard let info = Bundle(url: staged)?.infoDictionary,
              info["CFBundleIdentifier"] as? String == Bundle.main.bundleIdentifier,
              info["CFBundleShortVersionString"] as? String == release.version else {
            throw UpdateError("The downloaded app isn't the expected OpenTypist version.")
        }
        try Self.run("/usr/bin/codesign", ["--verify", "--deep", "--strict", staged.path])
        return staged
    }

    private func replaceRunningApp(with newApp: URL) throws {
        try replace(appAt: Bundle.main.bundleURL, with: newApp)
    }

    func replace(appAt current: URL, with newApp: URL) throws {
        guard FileManager.default.isWritableFile(atPath: current.deletingLastPathComponent().path) else {
            // No permission to replace it: hand the user the update to drag in.
            NSWorkspace.shared.activateFileViewerSelecting([newApp])
            throw UpdateError("OpenTypist can't replace itself in \(current.deletingLastPathComponent().path). The new version is shown in Finder; drag it into Applications.")
        }
        // Stage beside the old app so the final swap stays on one volume.
        let staged = current.deletingLastPathComponent().appendingPathComponent(".OpenTypist-update.app")
        try? FileManager.default.removeItem(at: staged)
        try Self.run("/usr/bin/ditto", [newApp.path, staged.path])
        _ = try FileManager.default.replaceItemAt(current, withItemAt: staged)
    }

    private func relaunch() {
        AppActions.relaunch()
    }

    private static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 4 << 20), !chunk.isEmpty { hasher.update(data: chunk) }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    @discardableResult
    private static func run(_ tool: String, _ arguments: [String]) throws -> String {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = pipe
        try process.run()
        process.waitUntilExit()
        let output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
        guard process.terminationStatus == 0 else {
            throw UpdateError("\(URL(fileURLWithPath: tool).lastPathComponent) failed: \(output.trimmingCharacters(in: .whitespacesAndNewlines))")
        }
        return output
    }
}
