using System.Runtime.InteropServices;

namespace Hearsay.Whisper.Native;

// Layouts of the whisper.cpp 1.8.5 structs Hearsay passes by value, as
// bundled in Whisper.net.Runtime 1.9.1 (submodule commit f24588a2). They
// match Whisper.net's own internal declarations field for field
// (Whisper.net.Native.WhisperFullParams, 304 bytes, and
// WhisperContextParams, 48 bytes); WhisperNativeLayoutTests compares the
// offsets through reflection, so a package upgrade that moves a field fails
// a test instead of corrupting memory. C bools are one byte.
// No Swift counterpart: the Mac decoder is MLX (mac/HearsayWhisper).

/// <summary><c>enum whisper_sampling_strategy</c>.</summary>
internal enum WhisperSamplingStrategy
{
    Greedy = 0,
    BeamSearch = 1,
}

/// <summary><c>struct whisper_aheads</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperAheads
{
    public nuint HeadCount;
    public IntPtr Heads;
}

/// <summary><c>struct whisper_context_params</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperContextParams
{
    public byte UseGpu;
    public byte FlashAttention;
    public int GpuDevice;
    public byte DtwTokenTimestamps;
    public int DtwAheadsPreset;
    public int DtwNTop;
    public WhisperAheads DtwAheads;
    public nuint DtwMemorySize;
}

/// <summary><c>whisper_full_params.greedy</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperGreedyParams
{
    public int BestOf;
}

/// <summary><c>whisper_full_params.beam_search</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperBeamSearchParams
{
    public int BeamSize;
    public float Patience;
}

/// <summary><c>struct whisper_vad_params</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperVadParams
{
    public float Threshold;
    public int MinSpeechDurationMs;
    public int MinSilenceDurationMs;
    public float MaxSpeechDurationS;
    public int SpeechPadMs;
    public float SamplesOverlap;
}

/// <summary><c>struct whisper_full_params</c> (whisper.cpp 1.8.5).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WhisperFullParams
{
    public WhisperSamplingStrategy Strategy;
    public int Threads;
    public int MaxTextContext;
    public int OffsetMs;
    public int DurationMs;
    public byte Translate;
    public byte NoContext;
    public byte NoTimestamps;
    public byte SingleSegment;
    public byte PrintSpecial;
    public byte PrintProgress;
    public byte PrintRealtime;
    public byte PrintTimestamps;
    public byte TokenTimestamps;
    public float TokenTimestampThreshold;
    public float TokenTimestampSumThreshold;
    public int MaxSegmentLength;
    public byte SplitOnWord;
    public int MaxTokens;
    public byte DebugMode;
    public int AudioContext;
    public byte TinyDiarize;
    public IntPtr SuppressRegex;
    public IntPtr InitialPrompt;
    public byte CarryInitialPrompt;
    public IntPtr PromptTokens;
    public int PromptTokenCount;
    public IntPtr Language;
    public byte DetectLanguage;
    public byte SuppressBlank;
    public byte SuppressNonSpeechTokens;
    public float Temperature;
    public float MaxInitialTimestamp;
    public float LengthPenalty;
    public float TemperatureIncrement;
    public float EntropyThreshold;
    public float LogprobThreshold;
    public float NoSpeechThreshold;
    public WhisperGreedyParams Greedy;
    public WhisperBeamSearchParams BeamSearch;
    public IntPtr NewSegmentCallback;
    public IntPtr NewSegmentCallbackUserData;
    public IntPtr ProgressCallback;
    public IntPtr ProgressCallbackUserData;
    public IntPtr EncoderBeginCallback;
    public IntPtr EncoderBeginCallbackUserData;
    public IntPtr AbortCallback;
    public IntPtr AbortCallbackUserData;
    public IntPtr LogitsFilterCallback;
    public IntPtr LogitsFilterCallbackUserData;
    public IntPtr GrammarRules;
    public nuint GrammarRuleCount;
    public nuint GrammarStartRule;
    public float GrammarPenalty;
    public byte Vad;
    public IntPtr VadModelPath;
    public WhisperVadParams VadParams;
}
