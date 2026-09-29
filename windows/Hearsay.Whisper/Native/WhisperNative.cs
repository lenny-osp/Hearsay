using System.Runtime.InteropServices;

namespace Hearsay.Whisper.Native;

/// <summary>
/// The whisper.cpp C API functions Hearsay calls, resolved from the
/// <c>whisper.dll</c> that Whisper.net's loader picked
/// (<see cref="WhisperRuntime"/>). Twenty-one exports, bound as unmanaged
/// function pointers so nothing is marshalled behind Hearsay's back.
/// No Swift counterpart: the Mac decoder is MLX (mac/HearsayWhisper); this is
/// the Windows half of PLAN.md 18.3 (Whisper row) and 18.8 (Language detection).
/// </summary>
internal sealed unsafe class WhisperNative
{
    /// <summary>Every export resolved, in the order of the fields below.</summary>
    public static readonly IReadOnlyList<string> ExportNames =
    [
        "whisper_context_default_params_by_ref",
        "whisper_free_context_params",
        "whisper_init_from_file_with_params_no_state",
        "whisper_init_state",
        "whisper_free_state",
        "whisper_free",
        "whisper_is_multilingual",
        "whisper_full_default_params_by_ref",
        "whisper_free_params",
        "whisper_full_with_state",
        "whisper_full_n_segments_from_state",
        "whisper_full_get_segment_t0_from_state",
        "whisper_full_get_segment_t1_from_state",
        "whisper_full_get_segment_text_from_state",
        "whisper_pcm_to_mel_with_state",
        "whisper_lang_auto_detect_with_state",
        "whisper_get_logits_from_state",
        "whisper_token_nosp",
        "whisper_n_vocab",
        "whisper_lang_max_id",
        "whisper_lang_str",
    ];

    public readonly delegate* unmanaged[Cdecl]<WhisperContextParams*> ContextDefaultParamsByRef;
    public readonly delegate* unmanaged[Cdecl]<WhisperContextParams*, void> FreeContextParams;
    public readonly delegate* unmanaged[Cdecl]<byte*, WhisperContextParams, IntPtr> InitFromFileWithParamsNoState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> InitState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, void> FreeState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, void> Free;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int> IsMultilingual;
    public readonly delegate* unmanaged[Cdecl]<WhisperSamplingStrategy, WhisperFullParams*> FullDefaultParamsByRef;
    public readonly delegate* unmanaged[Cdecl]<WhisperFullParams*, void> FreeParams;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, WhisperFullParams, float*, int, int> FullWithState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int> FullNSegmentsFromState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int, long> FullGetSegmentT0FromState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int, long> FullGetSegmentT1FromState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int, byte*> FullGetSegmentTextFromState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float*, int, int, int> PcmToMelWithState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, float*, int> LangAutoDetectWithState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, float*> GetLogitsFromState;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int> TokenNoSpeech;
    public readonly delegate* unmanaged[Cdecl]<IntPtr, int> NVocab;
    public readonly delegate* unmanaged[Cdecl]<int> LangMaxId;
    public readonly delegate* unmanaged[Cdecl]<int, byte*> LangStr;

    /// <exception cref="PlatformNotSupportedException">An export is missing (a whisper.dll from another whisper.cpp version).</exception>
    public WhisperNative(IntPtr library, string libraryPath)
    {
        IntPtr Export(string name) =>
            NativeLibrary.TryGetExport(library, name, out var address)
                ? address
                : throw new PlatformNotSupportedException(
                    $"{libraryPath} has no export {name}; Hearsay needs the whisper.cpp 1.8.5 build from Whisper.net.Runtime 1.9.1.");

        ContextDefaultParamsByRef = (delegate* unmanaged[Cdecl]<WhisperContextParams*>)Export(ExportNames[0]);
        FreeContextParams = (delegate* unmanaged[Cdecl]<WhisperContextParams*, void>)Export(ExportNames[1]);
        InitFromFileWithParamsNoState = (delegate* unmanaged[Cdecl]<byte*, WhisperContextParams, IntPtr>)Export(ExportNames[2]);
        InitState = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export(ExportNames[3]);
        FreeState = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export(ExportNames[4]);
        Free = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export(ExportNames[5]);
        IsMultilingual = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export(ExportNames[6]);
        FullDefaultParamsByRef = (delegate* unmanaged[Cdecl]<WhisperSamplingStrategy, WhisperFullParams*>)Export(ExportNames[7]);
        FreeParams = (delegate* unmanaged[Cdecl]<WhisperFullParams*, void>)Export(ExportNames[8]);
        FullWithState = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, WhisperFullParams, float*, int, int>)Export(ExportNames[9]);
        FullNSegmentsFromState = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export(ExportNames[10]);
        FullGetSegmentT0FromState = (delegate* unmanaged[Cdecl]<IntPtr, int, long>)Export(ExportNames[11]);
        FullGetSegmentT1FromState = (delegate* unmanaged[Cdecl]<IntPtr, int, long>)Export(ExportNames[12]);
        FullGetSegmentTextFromState = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*>)Export(ExportNames[13]);
        PcmToMelWithState = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float*, int, int, int>)Export(ExportNames[14]);
        LangAutoDetectWithState = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, float*, int>)Export(ExportNames[15]);
        GetLogitsFromState = (delegate* unmanaged[Cdecl]<IntPtr, float*>)Export(ExportNames[16]);
        TokenNoSpeech = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export(ExportNames[17]);
        NVocab = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export(ExportNames[18]);
        LangMaxId = (delegate* unmanaged[Cdecl]<int>)Export(ExportNames[19]);
        LangStr = (delegate* unmanaged[Cdecl]<int, byte*>)Export(ExportNames[20]);
    }

    /// <summary>A NUL-terminated UTF-8 string from whisper.cpp (invalid bytes become U+FFFD), or null.</summary>
    public static string? Utf8(byte* text) =>
        text is null ? null : Marshal.PtrToStringUTF8((IntPtr)text);
}
