import Foundation

/// One timed transcript segment, in seconds from the start of the audio.
public struct TranscriptSegment: Sendable, Equatable, Codable {
    public var start: TimeInterval
    public var end: TimeInterval
    public var text: String

    public init(start: TimeInterval, end: TimeInterval, text: String) {
        self.start = start
        self.end = end
        self.text = text
    }
}
