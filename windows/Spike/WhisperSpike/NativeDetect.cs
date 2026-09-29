using System.Runtime.InteropServices;

namespace WhisperSpike;

/// <summary>
/// Proof that the whisper.dll shipped in Whisper.net.Runtime gives what the Mac's
/// language detection needs (LanguageDetection.swift: all language probabilities
/// plus the no-speech probability at the sot position) from ONE encoder run,
/// through whisper.cpp's C API directly: whisper_pcm_to_mel_with_state,
/// whisper_lang_auto_detect_with_state (fills lang_probs for every language),
/// then whisper_get_logits_from_state (the sot-position logits that detection
/// just computed) for the no-speech token. Whisper.net itself exposes neither.
/// Uses its own whisper_context (a second model load).
/// </summary>
internal sealed unsafe class NativeDetect : IDisposable
{
    private readonly delegate* unmanaged[Cdecl]<byte*, IntPtr> initFromFile;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr> initState;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> freeState;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, void> free;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float*, int, int, int> pcmToMel;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, float*, int> langAutoDetect;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, float*> getLogits;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> tokenNosp;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, int> nVocab;
    private readonly delegate* unmanaged[Cdecl]<int> langMaxId;
    private readonly delegate* unmanaged[Cdecl]<byte*, int> langId;
    private readonly IntPtr ctx;

    public NativeDetect(string whisperDllPath, string modelPath)
    {
        IntPtr lib = NativeLibrary.Load(whisperDllPath);
        IntPtr F(string name) => NativeLibrary.GetExport(lib, name);
        initFromFile = (delegate* unmanaged[Cdecl]<byte*, IntPtr>)F("whisper_init_from_file");
        initState = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)F("whisper_init_state");
        freeState = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("whisper_free_state");
        free = (delegate* unmanaged[Cdecl]<IntPtr, void>)F("whisper_free");
        pcmToMel = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, float*, int, int, int>)F("whisper_pcm_to_mel_with_state");
        langAutoDetect = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int, float*, int>)F("whisper_lang_auto_detect_with_state");
        getLogits = (delegate* unmanaged[Cdecl]<IntPtr, float*>)F("whisper_get_logits_from_state");
        tokenNosp = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("whisper_token_nosp");
        nVocab = (delegate* unmanaged[Cdecl]<IntPtr, int>)F("whisper_n_vocab");
        langMaxId = (delegate* unmanaged[Cdecl]<int>)F("whisper_lang_max_id");
        langId = (delegate* unmanaged[Cdecl]<byte*, int>)F("whisper_lang_id");

        byte[] path = System.Text.Encoding.UTF8.GetBytes(modelPath + "\0");
        fixed (byte* p = path)
        {
            ctx = initFromFile(p);
        }
        if (ctx == IntPtr.Zero)
        {
            throw new InvalidOperationException($"whisper_init_from_file failed for {modelPath}");
        }
    }

    public int LangId(string code)
    {
        byte[] b = System.Text.Encoding.ASCII.GetBytes(code + "\0");
        fixed (byte* p = b)
        {
            return langId(p);
        }
    }

    /// <summary>All language probabilities (indexed by whisper language id) and the no-speech probability.</summary>
    public (float[] Probs, float NoSpeech, int TopId) Detect(float[] samples, int threads)
    {
        IntPtr state = initState(ctx);
        if (state == IntPtr.Zero)
        {
            throw new InvalidOperationException("whisper_init_state failed");
        }
        try
        {
            var probs = new float[langMaxId() + 1];
            int rc;
            fixed (float* s = samples)
            {
                rc = pcmToMel(ctx, state, s, samples.Length, threads);
            }
            if (rc != 0)
            {
                throw new InvalidOperationException($"whisper_pcm_to_mel_with_state returned {rc}");
            }
            int top;
            fixed (float* p = probs)
            {
                top = langAutoDetect(ctx, state, 0, threads, p);
            }
            if (top < 0)
            {
                throw new InvalidOperationException($"whisper_lang_auto_detect_with_state returned {top}");
            }
            float* logits = getLogits(state);
            int n = nVocab(ctx);
            double max = double.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                max = Math.Max(max, logits[i]);
            }
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += Math.Exp(logits[i] - max);
            }
            float noSpeech = (float)(Math.Exp(logits[tokenNosp(ctx)] - max) / sum);
            return (probs, noSpeech, top);
        }
        finally
        {
            freeState(state);
        }
    }

    public void Dispose() => free(ctx);
}
