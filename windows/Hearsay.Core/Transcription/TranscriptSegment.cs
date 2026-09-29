namespace Hearsay.Core.Transcription;

/// <summary>
/// One timed transcript segment, in seconds from the start of the audio.
/// Port of mac/HearsayCore/Sources/HearsayCore/Transcription/Segment.swift.
/// </summary>
public readonly record struct TranscriptSegment(double Start, double End, string Text);
