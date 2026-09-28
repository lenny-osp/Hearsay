import Foundation
import Security

/// Stores API tokens. Hearsay keeps one token per provider preset id.
public protocol SecretStore: Sendable {
    func read(account: String) throws -> String?
    func write(_ secret: String, account: String) throws
    func delete(account: String) throws
}

public struct SecretStoreError: Error, LocalizedError, Equatable {
    public let status: OSStatus

    public init(status: OSStatus) {
        self.status = status
    }

    public var errorDescription: String? {
        let detail = SecCopyErrorMessageString(status, nil).map { $0 as String } ?? "OSStatus \(status)"
        return String(localized: "Keychain error: \(detail)", bundle: .module,
                      comment: "Token error. %@ is the message macOS gives for the Keychain error.")
    }
}

/// Generic-password Keychain items under service `tw.og1o.hearsay`.
public struct KeychainSecretStore: SecretStore {
    public static let defaultService = "tw.og1o.hearsay"

    public let service: String

    public init(service: String = KeychainSecretStore.defaultService) {
        self.service = service
    }

    private func baseQuery(account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
    }

    public func read(account: String) throws -> String? {
        var query = baseQuery(account: account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        switch status {
        case errSecSuccess:
            guard let data = item as? Data else { return nil }
            return String(data: data, encoding: .utf8)
        case errSecItemNotFound:
            return nil
        default:
            throw SecretStoreError(status: status)
        }
    }

    public func write(_ secret: String, account: String) throws {
        let data = Data(secret.utf8)
        let update: [String: Any] = [
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        let status = SecItemUpdate(baseQuery(account: account) as CFDictionary, update as CFDictionary)
        if status == errSecSuccess { return }
        guard status == errSecItemNotFound else { throw SecretStoreError(status: status) }

        var add = baseQuery(account: account)
        add.merge(update) { _, new in new }
        let addStatus = SecItemAdd(add as CFDictionary, nil)
        guard addStatus == errSecSuccess else { throw SecretStoreError(status: addStatus) }
    }

    public func delete(account: String) throws {
        let status = SecItemDelete(baseQuery(account: account) as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw SecretStoreError(status: status)
        }
    }
}

/// Process-local secret store for tests and previews.
public final class InMemorySecretStore: SecretStore, @unchecked Sendable {
    private let lock = NSLock()
    private var secrets: [String: String]

    public init(_ secrets: [String: String] = [:]) {
        self.secrets = secrets
    }

    public func read(account: String) throws -> String? {
        lock.withLock { secrets[account] }
    }

    public func write(_ secret: String, account: String) throws {
        lock.withLock { secrets[account] = secret }
    }

    public func delete(account: String) throws {
        lock.withLock { _ = secrets.removeValue(forKey: account) }
    }
}
