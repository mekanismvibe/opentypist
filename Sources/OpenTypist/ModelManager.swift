import AppKit
import Combine
import CryptoKit

/// Downloads, verifies and selects GGUF model files. Nothing is downloaded
/// unless the user asks for it.
final class ModelManager: NSObject, ObservableObject {
    static let shared = ModelManager()

    enum DownloadState: Equatable {
        case none
        case downloading(modelID: String, received: Int64, total: Int64)
        case verifying(modelID: String)
        case failed(modelID: String, message: String)
    }

    @Published private(set) var download: DownloadState = .none
    /// Bumped whenever the active model file changes.
    @Published private(set) var revision = 0

    private var session: URLSession?
    private var task: URLSessionDownloadTask?
    private var downloadingModel: ModelInfo?

    var modelsDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("OpenTypist/Models", isDirectory: true)
    }

    /// Where a new download of `model` goes.
    func downloadURL(for model: ModelInfo) -> URL { modelsDirectory.appendingPathComponent(model.fileName) }

    /// The file to load: the current one, or an earlier file of the same
    /// model that is already on disk (e.g. Gemma 4 E2B's former Q4_K_M).
    func fileURL(for model: ModelInfo) -> URL {
        for name in [model.fileName] + model.legacyFileNames {
            let url = modelsDirectory.appendingPathComponent(name)
            if FileManager.default.fileExists(atPath: url.path) { return url }
        }
        return downloadURL(for: model)
    }

    func isDownloaded(_ model: ModelInfo) -> Bool { FileManager.default.fileExists(atPath: fileURL(for: model).path) }

    var downloadedModels: [ModelInfo] { ModelCatalog.all.filter(isDownloaded) }

    var selectedModel: ModelInfo? { ModelCatalog.model(id: Settings.shared.selectedModelID) }

    /// The catalog model in use: the selected one, or any downloaded one while
    /// the selected one is still downloading.
    var activeCatalogModel: ModelInfo? {
        if Settings.shared.customModelPath != nil, activeCustomURL != nil { return nil }
        if let selected = selectedModel, isDownloaded(selected) { return selected }
        return downloadedModels.first
    }

    private var activeCustomURL: URL? {
        guard let custom = Settings.shared.customModelPath, FileManager.default.fileExists(atPath: custom) else { return nil }
        return URL(fileURLWithPath: custom)
    }

    /// The file the engine should load, if any model is available.
    var activeModelURL: URL? {
        if let custom = activeCustomURL { return custom }
        return activeCatalogModel.map(fileURL(for:))
    }

    var activeModelName: String? {
        if let custom = activeCustomURL { return custom.deletingPathExtension().lastPathComponent }
        return activeCatalogModel?.name
    }

    var isDownloading: Bool {
        switch download {
        case .downloading, .verifying: return true
        default: return false
        }
    }

    func isDownloading(_ model: ModelInfo) -> Bool {
        switch download {
        case .downloading(let id, _, _), .verifying(let id): return id == model.id
        default: return false
        }
    }

    // MARK: - Selection

    func select(_ model: ModelInfo) {
        Settings.shared.customModelPath = nil
        Settings.shared.selectedModelID = model.id
        revision += 1
    }

    func useCustomModel(_ url: URL?) {
        Settings.shared.customModelPath = url?.path
        revision += 1
    }

    func chooseModelFile() {
        let panel = NSOpenPanel()
        panel.title = "Choose a GGUF model"
        panel.allowsOtherFileTypes = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        NSApp.activate(ignoringOtherApps: true)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        guard url.pathExtension.lowercased() == "gguf" else {
            download = .failed(modelID: "", message: "\(url.lastPathComponent) is not a .gguf file")
            return
        }
        useCustomModel(url)
    }

    func delete(_ model: ModelInfo) {
        for name in [model.fileName] + model.legacyFileNames {
            try? FileManager.default.removeItem(at: modelsDirectory.appendingPathComponent(name))
        }
        revision += 1
    }

    // MARK: - Download

    func startDownload(_ model: ModelInfo) {
        guard !isDownloading else { return }
        do {
            try FileManager.default.createDirectory(at: modelsDirectory, withIntermediateDirectories: true)
        } catch {
            download = .failed(modelID: model.id, message: error.localizedDescription)
            return
        }
        let session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
        let task = session.downloadTask(with: model.url)
        self.session = session
        self.task = task
        downloadingModel = model
        download = .downloading(modelID: model.id, received: 0, total: model.size)
        task.resume()
    }

    func cancelDownload() {
        task?.cancel()
        session?.invalidateAndCancel()
        task = nil
        session = nil
        downloadingModel = nil
        download = .none
    }

    private func verifyAndInstall(_ partial: URL, model: ModelInfo) {
        download = .verifying(modelID: model.id)
        let destination = downloadURL(for: model)
        DispatchQueue.global(qos: .utility).async {
            let result = Result { try Self.verify(partial, model: model) }
            DispatchQueue.main.async {
                self.downloadingModel = nil
                switch result {
                case .success:
                    do {
                        try? FileManager.default.removeItem(at: destination)
                        try FileManager.default.moveItem(at: partial, to: destination)
                        self.download = .none
                        // A finished download becomes the active model.
                        self.select(model)
                    } catch {
                        self.download = .failed(modelID: model.id, message: error.localizedDescription)
                    }
                case .failure(let error):
                    try? FileManager.default.removeItem(at: partial)
                    self.download = .failed(modelID: model.id, message: error.localizedDescription)
                }
            }
        }
    }

    private struct VerificationError: LocalizedError {
        let errorDescription: String?
    }

    private static func verify(_ url: URL, model: ModelInfo) throws {
        let size = (try FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? -1
        guard size == model.size else {
            throw VerificationError(errorDescription: "Downloaded file has the wrong size (\(size) bytes)")
        }
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 8 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        let digest = hasher.finalize().map { String(format: "%02x", $0) }.joined()
        guard digest == model.sha256 else {
            throw VerificationError(errorDescription: "Downloaded file failed the checksum check")
        }
    }
}

extension ModelManager: URLSessionDownloadDelegate {
    func urlSession(
        _ session: URLSession, downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64
    ) {
        guard let model = downloadingModel else { return }
        let total = totalBytesExpectedToWrite > 0 ? totalBytesExpectedToWrite : model.size
        download = .downloading(modelID: model.id, received: totalBytesWritten, total: total)
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        guard let model = downloadingModel else { return }
        // The temporary file is deleted when this method returns, so move it now.
        let partial = modelsDirectory.appendingPathComponent(model.fileName + ".partial")
        try? FileManager.default.removeItem(at: partial)
        do {
            try FileManager.default.moveItem(at: location, to: partial)
        } catch {
            download = .failed(modelID: model.id, message: error.localizedDescription)
            return
        }
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 {
            try? FileManager.default.removeItem(at: partial)
            download = .failed(modelID: model.id, message: "Download failed (HTTP \(http.statusCode))")
            return
        }
        verifyAndInstall(partial, model: model)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        defer {
            self.session?.finishTasksAndInvalidate()
            self.session = nil
            self.task = nil
        }
        guard let error, let model = downloadingModel else { return }
        if (error as NSError).code == NSURLErrorCancelled { return }
        downloadingModel = nil
        download = .failed(modelID: model.id, message: error.localizedDescription)
    }
}
