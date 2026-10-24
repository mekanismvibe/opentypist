import CryptoKit
import Foundation
import Security

/// Saves Codable values as AES-GCM encrypted files. Developer ID builds keep
/// the key in the login Keychain; ad-hoc builds keep it in a key file readable
/// only by the user (see `key()` for why).
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
    private let usesKeychain: Bool
    private var legacyKeyTried = false
    private var legacyKey: SymmetricKey?

    /// `key` is for tests: a fixed key that never touches the Keychain.
    init(directory: URL, key: SymmetricKey? = nil) {
        self.directory = directory
        self.cachedKey = key
        self.usesKeychain = key == nil
    }

    /// The Keychain key older builds used, read at most once per launch.
    private func legacyKeychainKey() -> SymmetricKey? {
        guard usesKeychain, !legacyKeyTried else { return legacyKey }
        legacyKeyTried = true
        legacyKey = Self.keychainKey(createIfMissing: false)
        return legacyKey
    }

    func load<T: Codable>(_ type: T.Type, from name: String) -> T? {
        let url = directory.appendingPathComponent(name)
        guard let sealed = try? Data(contentsOf: url) else { return nil }
        if let box = try? AES.GCM.SealedBox(combined: sealed), let plain = try? AES.GCM.open(box, using: key()) {
            return try? JSONDecoder().decode(T.self, from: plain)
        }
        // Saved by an older build whose key was in the Keychain: read that key
        // once (macOS may ask), then save again under the current key.
        if let legacy = legacyKeychainKey(), let box = try? AES.GCM.SealedBox(combined: sealed),
           let plain = try? AES.GCM.open(box, using: legacy), let value = try? JSONDecoder().decode(T.self, from: plain) {
            save(value, to: name)
            return value
        }
        // Never overwrite data that can't be read; set it aside instead.
        let aside = directory.appendingPathComponent(name + ".unreadable")
        try? FileManager.default.removeItem(at: aside)
        try? FileManager.default.moveItem(at: url, to: aside)
        AppStatus.shared.note("Couldn't read saved \(name); kept it as \(aside.lastPathComponent)")
        return nil
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

    /// Builds signed with a Developer ID keep the key in the Keychain, which
    /// trusts the team and so keeps working across updates. Ad-hoc builds use
    /// a key file only this user can read: the Keychain trusts one exact
    /// binary for those, so every rebuild or update would ask the user again
    /// (and the app would wait on that prompt).
    private func key() -> SymmetricKey {
        if let cachedKey { return cachedKey }
        let key: SymmetricKey
        if Self.hasTeamIdentity, let stored = Self.keychainKey() {
            key = stored
        } else if let stored = readFileKey() {
            key = stored
        } else if let migrated = Self.keychainKeyWithoutPrompt() {
            // Data saved by an earlier build used the Keychain; keep it readable.
            writeFileKey(migrated)
            key = migrated
        } else {
            key = SymmetricKey(size: .bits256)
            writeFileKey(key)
        }
        cachedKey = key
        return key
    }

    private static let hasTeamIdentity: Bool = {
        var code: SecCode?
        var staticCode: SecStaticCode?
        var info: CFDictionary?
        guard SecCodeCopySelf([], &code) == errSecSuccess, let code,
              SecCodeCopyStaticCode(code, [], &staticCode) == errSecSuccess, let staticCode,
              SecCodeCopySigningInformation(staticCode, SecCSFlags(rawValue: kSecCSSigningInformation), &info) == errSecSuccess,
              let dictionary = info as? [String: Any] else { return false }
        return dictionary[kSecCodeInfoTeamIdentifier as String] != nil
    }()

    /// Reads an existing Keychain key only if that needs no prompt.
    private static func keychainKeyWithoutPrompt() -> SymmetricKey? {
        SecKeychainSetUserInteractionAllowed(false)
        defer { SecKeychainSetUserInteractionAllowed(true) }
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data, data.count == 32 else {
            return nil
        }
        return SymmetricKey(data: data)
    }

    private static func keychainKey(createIfMissing: Bool = true) -> SymmetricKey? {
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
        guard createIfMissing else { return nil }
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

    private var keyFileURL: URL { directory.appendingPathComponent(".key") }

    private func readFileKey() -> SymmetricKey? {
        guard let data = try? Data(contentsOf: keyFileURL), data.count == 32 else { return nil }
        return SymmetricKey(data: data)
    }

    private func writeFileKey(_ key: SymmetricKey) {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try? key.withUnsafeBytes { Data($0) }.write(to: keyFileURL, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: keyFileURL.path)
    }
}
