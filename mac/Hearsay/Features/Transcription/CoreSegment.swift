import HearsayCore

/// HearsayCore's SRT cue. HearsayWhisper also exports a `TranscriptSegment`
/// (the decoder's full segment), and the `HearsayCore` enum shadows the
/// module name, so files that import both modules use this alias.
typealias CoreSegment = TranscriptSegment
