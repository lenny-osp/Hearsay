import Foundation

/// Output timestamps (`yyyy-MM-dd_HH-mm-ss`) and the source-file timestamp
/// rules from whisper-tools `source_file_timestamp`.
public enum Timestamps {
    public static let format = "yyyy-MM-dd_HH-mm-ss"

    // Same pattern as the Python regex in `source_file_timestamp`.
    private static let embedded = try? NSRegularExpression(
        pattern: #"(?<!\d)(\d{4})[-_]?(\d{2})[-_]?(\d{2})[T _-]?(\d{2})[-_:]?(\d{2})[-_:]?(\d{2})(?!\d)"#
    )

    /// Formats a date in the local time zone with a fixed POSIX locale.
    public static func string(from date: Date, timeZone: TimeZone = .current) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.timeZone = timeZone
        formatter.dateFormat = format
        return formatter.string(from: date)
    }

    /// The current time as an output timestamp.
    public static func now() -> String {
        string(from: Date())
    }

    /// Finds the first embedded timestamp in a filename and returns it in the
    /// output format, or nil when there is none or it is not a real date and
    /// time. Like Python, an invalid first match does not fall through to a
    /// later one.
    public static func parse(fromFilename filename: String) -> String? {
        guard let embedded else { return nil }
        let name = filename as NSString
        guard let match = embedded.firstMatch(in: filename, range: NSRange(location: 0, length: name.length))
        else { return nil }
        var parts: [Int] = []
        for group in 1...6 {
            guard let value = Int(name.substring(with: match.range(at: group))) else { return nil }
            parts.append(value)
        }
        let (year, month, day, hour, minute, second) = (parts[0], parts[1], parts[2], parts[3], parts[4], parts[5])
        guard isValid(year: year, month: month, day: day, hour: hour, minute: minute, second: second)
        else { return nil }
        return String(format: "%04d-%02d-%02d_%02d-%02d-%02d", year, month, day, hour, minute, second)
    }

    /// Python priority: timestamp in the filename, then file birth time, then
    /// modification time. Nil when the file cannot be inspected.
    public static func sourceFileTimestamp(url: URL) -> String? {
        if let parsed = parse(fromFilename: url.lastPathComponent) {
            return parsed
        }
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path) else {
            return nil
        }
        let date = (attributes[.creationDate] as? Date) ?? (attributes[.modificationDate] as? Date)
        return date.map { string(from: $0) }
    }

    /// Mirrors the range checks of Python's `datetime(...)` constructor.
    private static func isValid(year: Int, month: Int, day: Int, hour: Int, minute: Int, second: Int) -> Bool {
        guard (1...9999).contains(year), (1...12).contains(month),
              (0...23).contains(hour), (0...59).contains(minute), (0...59).contains(second)
        else { return false }
        let daysInMonth: Int
        switch month {
        case 2:
            let leap = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0
            daysInMonth = leap ? 29 : 28
        case 4, 6, 9, 11:
            daysInMonth = 30
        default:
            daysInMonth = 31
        }
        return (1...daysInMonth).contains(day)
    }
}
