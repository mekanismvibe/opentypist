import CryptoKit
import Foundation
import Security

/// Saves Codable values as AES-GCM encrypted files. The key lives in the
/// login Keychain; if the Keychain is unavailable it falls back to a key file
/// readable only by the user.
final class SecureStore {
    /// With OPENTYPIST_TEST_STORE set (developer tools only), a throwaway
    /// folder and in-memory key are used instead of the user's data.
    static let shared: SecureStore = {
        if ProcessInfo.processInfo.environment["OPENTYPIST_TEST_STORE"] != nil {
            let directory = FileManager.default.temporaryDirectory.appendingPathComponent("opentypist-test-\(UUID().uuidString)")
            return SecureStore(directory: directory, key: SymmetricKey(size: .bits256))
        }
        return SecureStore(directory: SecureStore.defaultDirectory)
    }()

    static var defaultDirectory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("OpenTypist/Personal", isDirectory: true)
    }

    let directory: URL
    private var cachedKey: SymmetricKey?

    /// `key` is for tests: a fixed key that never touches the Keychain.
    init(directory: URL, key: SymmetricKey? = nil) {
        self.directory = directory
        self.cachedKey = key
    }

    func load<T: Decodable>(_ type: T.Type, from name: String) -> T? {
        let url = directory.appendingPathComponent(name)
        guard let sealed = try? Data(contentsOf: url),
              let box = try? AES.GCM.SealedBox(combined: sealed),
              let plain = try? AES.GCM.open(box, using: key()) else { return nil }
        return try? JSONDecoder().decode(T.self, from: plain)
    }

    func save<T: Encodable>(_ value: T, to name: String) {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let plain = try JSONEncoder().encode(value)
            guard let sealed = try AES.GCM.seal(plain, using: key()).combined else { return }
            let url = directory.appendingPathComponent(name)
            try sealed.write(to: url, options: [.atomic, .completeFileProtection])
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        } catch {
            AppStatus.shared.note("Couldn't save personal data: \(error.localizedDescription)")
        }
    }

    func delete(_ name: String) {
        try? FileManager.default.removeItem(at: directory.appendingPathComponent(name))
    }

    // MARK: - Key

    private static let service = "org.opentypist.OpenTypist"
    private static let account = "personal-data-key"

    private func key() -> SymmetricKey {
        if let cachedKey { return cachedKey }
        let key = Self.keychainKey() ?? fileKey()
        cachedKey = key
        return key
    }

    private static func keychainKey() -> SymmetricKey? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
        ]
        var item: CFTypeRef?
        if SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data, data.count == 32 {
            return SymmetricKey(data: data)
        }
        let key = SymmetricKey(size: .bits256)
        let data = key.withUnsafeBytes { Data($0) }
        let add: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecAttrLabel as String: "OpenTypist personal data",
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        return SecItemAdd(add as CFDictionary, nil) == errSecSuccess ? key : nil
    }

    private func fileKey() -> SymmetricKey {
        let url = directory.appendingPathComponent(".key")
        if let data = try? Data(contentsOf: url), data.count == 32 { return SymmetricKey(data: data) }
        let key = SymmetricKey(size: .bits256)
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? key.withUnsafeBytes { Data($0) }.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
        return key
    }
}
