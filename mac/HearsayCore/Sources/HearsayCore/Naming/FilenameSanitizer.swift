import Foundation

/// Port of `sanitize_ai_filename` from whisper-tools `run_whisper.py`.
///
/// Produces a lowercase ASCII slug of letters, digits, and hyphens, capped at
/// 80 characters, or nil when nothing usable remains.
public enum FilenameSanitizer {
    public static let maxLength = 80

    public static func sanitize(_ raw: String) -> String? {
        // NFKC, then trim surrounding whitespace.
        var value = raw.precomposedStringWithCompatibilityMapping
            .trimmingCharacters(in: .whitespacesAndNewlines)
        // Drop a trailing .md / .srt extension (case-insensitive).
        value = value.replacingOccurrences(
            of: #"\.(?:md|srt)\s*$"#,
            with: "",
            options: [.regularExpression, .caseInsensitive]
        )
        // NFKD, lowercase, drop combining marks (accents).
        value = value.decomposedStringWithCompatibilityMapping.lowercased()
        let scalars = value.unicodeScalars.filter {
            $0.properties.canonicalCombiningClass == .notReordered
        }

        // Replace each run of characters outside [a-z0-9-] with one hyphen.
        var slug = ""
        var inRun = false
        for scalar in scalars {
            if isAllowed(scalar) {
                slug.unicodeScalars.append(scalar)
                inRun = false
            } else if !inRun {
                slug.append("-")
                inRun = true
            }
        }

        // strip("-"), collapse "-+" to "-", cap, then rstrip("-").
        slug = trimHyphens(slug, leading: true, trailing: true)
        var collapsed = ""
        for character in slug {
            if character == "-", collapsed.last == "-" { continue }
            collapsed.append(character)
        }
        let capped = trimHyphens(String(collapsed.prefix(maxLength)), leading: false, trailing: true)
        return capped.isEmpty ? nil : capped
    }

    private static func isAllowed(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x61...0x7A, 0x30...0x39, 0x2D: return true
        default: return false
        }
    }

    private static func trimHyphens(_ value: String, leading: Bool, trailing: Bool) -> String {
        var slice = Substring(value)
        if leading { slice = slice.drop(while: { $0 == "-" }) }
        if trailing {
            while slice.last == "-" { slice = slice.dropLast() }
        }
        return String(slice)
    }
}
