import Foundation

/// Text fields OpenTypist leaves alone, besides password fields.
enum FieldPolicy {
    static let singleLineRoles: Set<String> = ["AXTextField", "AXComboBox"]

    // MARK: Sign-in and verification fields

    /// A one-line field that asks who you are or for a code: email, username,
    /// phone, one-time code, card number. A completion there is a guess at
    /// your identity, so suggestions, autocorrect and learning stay out.
    /// Several-line fields are writing (an email *body*) and always count.
    static func isSignInField(role: String, labels: [String], identifiers: [String], value: String) -> Bool {
        guard singleLineRoles.contains(role) else { return false }
        return labels.contains(where: labelAsksForCredential)
            || identifiers.contains(where: identifierNamesCredential)
            || looksLikeEmailBeingTyped(value)
    }

    /// Phrases that, in a field's label or placeholder, ask for a credential.
    private static let credentialPhrases = [
        "email", "e-mail", "email address", "username", "user name", "user id", "login", "log in", "sign in",
        "phone", "phone number", "mobile number", "password", "passcode", "pin", "otp", "2fa", "two-factor",
        "verification code", "security code", "confirmation code", "authentication code", "one-time code",
        "one time code", "card number", "cvc", "cvv", "expiry", "expiration date"
    ]

    /// Words that make a label about writing, not identity: "Email subject",
    /// "Search mail", "Message".
    private static let proseWords: Set<String> = [
        "subject", "message", "body", "search", "note", "notes", "comment", "reply", "title", "signature",
        "preview", "template", "description", "query", "find"
    ]

    static func labelAsksForCredential(_ label: String) -> Bool {
        let words = label.lowercased().split { !$0.isLetter && !$0.isNumber && $0 != "-" }.map(String.init)
        guard !words.isEmpty, !words.contains(where: proseWords.contains) else { return false }
        let padded = " " + words.joined(separator: " ") + " "
        return credentialPhrases.contains { padded.contains(" \($0) ") }
    }

    /// Parts of a DOM or accessibility id that name a credential: "user_email",
    /// "loginField", "otp-input", Google's "identifierId". Short parts must
    /// match whole ("pin" in "pin-code", not in "spinner"); long ones may sit
    /// inside a run-together id ("useremail").
    private static let shortIDParts: Set<String> = ["pin", "tel", "otp", "totp", "mfa", "2fa", "pwd", "cvc", "cvv", "sms"]
    private static let longIDParts = [
        "email", "username", "userid", "login", "logon", "signin", "password", "passwd", "passcode",
        "phone", "mobile", "identifier", "onetimecode", "verificationcode", "securitycode", "cardnumber"
    ]

    static func identifierNamesCredential(_ identifier: String) -> Bool {
        guard !identifier.isEmpty, identifier.count <= 64 else { return false }
        let parts = idParts(identifier)
        guard !parts.contains(where: proseWords.contains) else { return false }
        if parts.contains(where: shortIDParts.contains) { return true }
        let joined = parts.joined()
        return longIDParts.contains { joined.contains($0) }
    }

    /// "loginEmail_2" → ["login", "email", "2"].
    private static func idParts(_ identifier: String) -> [String] {
        var parts: [String] = []
        var current = ""
        var previous: Character?
        for c in identifier {
            if !c.isLetter && !c.isNumber {
                if !current.isEmpty { parts.append(current) }
                current = ""
            } else {
                if let p = previous, (p.isLowercase && c.isUppercase) || (p.isLetter != c.isLetter && (p.isLetter || p.isNumber)) {
                    if !current.isEmpty { parts.append(current) }
                    current = ""
                }
                current.append(c)
            }
            previous = c
        }
        if !current.isEmpty { parts.append(current) }
        return parts.map { $0.lowercased() }
    }

    /// The whole value is one email address, finished or still being typed
    /// ("alice@", "alice@example.com"). A slash or colon before the "@"
    /// makes it a URL ("medium.com/@alice").
    static func looksLikeEmailBeingTyped(_ value: String) -> Bool {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.range(of: #"^[^\s@/:]+@[^\s@/:]*$"#, options: .regularExpression) != nil
    }

    // MARK: Mail

    /// In a Mail compose window Tab moves from To to Cc to Subject to the
    /// message, so Mail's one-line fields stay Mail's: only the message body
    /// gets suggestions.
    static func isMailHeaderField(bundleID: String?, role: String) -> Bool {
        bundleID == "com.apple.mail" && singleLineRoles.contains(role)
    }
}
