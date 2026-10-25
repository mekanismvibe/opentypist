using System.Text.RegularExpressions;

namespace OpenTypist;

/// Text fields OpenTypist leaves alone, besides password fields. A port of
/// FieldPolicy.swift.
public static class FieldPolicy
{
    // Sign-in and verification fields

    /// A one-line field that asks who you are or for a code: email, username,
    /// phone, one-time code, card number. A completion there is a guess at
    /// your identity, so suggestions, autocorrect and learning stay out.
    /// Several-line fields are writing (an email *body*) and always count.
    public static bool IsSignInField(bool singleLine, IEnumerable<string> labels, IEnumerable<string> identifiers, string value)
    {
        if (!singleLine) return false;
        return labels.Any(LabelAsksForCredential) || identifiers.Any(IdentifierNamesCredential)
            || LooksLikeEmailBeingTyped(value) || LooksLikeNumberBeingTyped(value);
    }

    /// Phrases that, in a field's label or placeholder, ask for a credential.
    private static readonly string[] CredentialPhrases =
    [
        "email", "e-mail", "email address", "username", "user name", "user id", "login", "log in", "sign in",
        "phone", "phone number", "mobile number", "password", "passcode", "pin", "otp", "2fa", "two-factor",
        "verification code", "security code", "confirmation code", "authentication code", "one-time code",
        "one time code", "card number", "cvc", "cvv", "expiry", "expiration date",
        // Windows can't see autocomplete="one-time-code", so its usual labels count.
        "enter code", "enter the code", "sms code", "text code", "login code", "sign-in code", "access code",
        "one-time password", "one time password",
    ];

    /// Words that make a label about writing, not identity: "Email subject",
    /// "Search mail", "Message".
    private static readonly HashSet<string> ProseWords =
    [
        "subject", "message", "body", "search", "note", "notes", "comment", "reply", "title", "signature",
        "preview", "template", "description", "query", "find",
    ];

    public static bool LabelAsksForCredential(string label)
    {
        var words = Regex.Split(label.ToLowerInvariant(), @"[^\p{L}\p{N}-]+").Where(w => w.Length > 0).ToList();
        if (words.Count == 0 || words.Any(ProseWords.Contains)) return false;
        var padded = " " + string.Join(" ", words) + " ";
        if (CredentialPhrases.Any(p => padded.Contains($" {p} ", StringComparison.Ordinal))) return true;
        // "6-digit code", "Enter the 4 digit code".
        return Regex.IsMatch(padded, @" \d+(-| )digits? (code|pin|number) ");
    }

    /// Parts of a DOM or UI Automation id that name a credential: "user_email",
    /// "loginField", "otp-input", Google's "identifierId". Short parts must
    /// match whole ("pin" in "pin-code", not in "spinner"); long ones may sit
    /// inside a run-together id ("useremail").
    private static readonly HashSet<string> ShortIdParts = ["pin", "tel", "otp", "totp", "mfa", "2fa", "pwd", "cvc", "cvv", "sms"];
    private static readonly string[] LongIdParts =
    [
        "email", "username", "userid", "login", "logon", "signin", "password", "passwd", "passcode",
        "phone", "mobile", "identifier", "onetimecode", "verificationcode", "securitycode", "cardnumber",
    ];

    public static bool IdentifierNamesCredential(string identifier)
    {
        if (identifier.Length == 0 || identifier.Length > 64) return false;
        var parts = IdParts(identifier);
        if (parts.Any(ProseWords.Contains)) return false;
        if (parts.Any(ShortIdParts.Contains)) return true;
        var joined = string.Concat(parts);
        return LongIdParts.Any(p => joined.Contains(p, StringComparison.Ordinal));
    }

    /// "loginEmail_2" → ["login", "email", "2"].
    private static List<string> IdParts(string identifier)
    {
        var parts = new List<string>();
        var current = "";
        char? previous = null;
        foreach (var c in identifier)
        {
            if (!char.IsLetterOrDigit(c))
            {
                if (current.Length > 0) parts.Add(current);
                current = "";
            }
            else
            {
                if (previous is char p && ((char.IsLower(p) && char.IsUpper(c)) || (char.IsLetter(p) != char.IsLetter(c) && char.IsLetterOrDigit(p))))
                {
                    if (current.Length > 0) parts.Add(current);
                    current = "";
                }
                current += c;
            }
            previous = c;
        }
        if (current.Length > 0) parts.Add(current);
        return parts.Select(p => p.ToLowerInvariant()).ToList();
    }

    private static readonly Regex EmailBeingTyped = new(@"^[^\s@/:]+@[^\s@/:]*$", RegexOptions.Compiled);

    /// The whole value is one email address, finished or still being typed
    /// ("alice@", "alice@example.com"). A slash or colon before the "@"
    /// makes it a URL ("medium.com/@alice").
    public static bool LooksLikeEmailBeingTyped(string value) => EmailBeingTyped.IsMatch(value.Trim());

    private static readonly Regex NumberBeingTyped = new(@"^\+?[\d][\d -]{2,}$", RegexOptions.Compiled);

    /// The whole value is a number being typed: a code, phone or card number
    /// ("123456", "+1 555 0100", "4111 1111"). Windows apps don't say a field
    /// is type="tel" or a one-time code, and no suggestion helps there.
    public static bool LooksLikeNumberBeingTyped(string value) => NumberBeingTyped.IsMatch(value.Trim());

    // Mail

    /// Mail apps where Tab moves from To to Cc to Subject to the message:
    /// classic Outlook, the new Outlook and the old Windows Mail.
    public static readonly string[] TabMovesBetweenFieldsApps = ["outlook.exe", "olk.exe", "hxoutlook.exe"];

    /// Their one-line fields stay theirs: only the message body gets
    /// suggestions (Tab must keep moving to the next field).
    public static bool IsMailHeaderField(string? appId, bool singleLine) =>
        singleLine && appId is not null && TabMovesBetweenFieldsApps.Contains(appId, StringComparer.OrdinalIgnoreCase);
}
