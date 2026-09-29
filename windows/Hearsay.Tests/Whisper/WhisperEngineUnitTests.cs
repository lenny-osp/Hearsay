using System.Reflection;
using System.Runtime.InteropServices;
using Hearsay.Core.Transcription;
using Hearsay.Whisper;
using Hearsay.Whisper.Native;

namespace Hearsay.Tests.Whisper;

/// <summary>
/// Model-free tests of the engine's option mapping, segment conversion,
/// Chinese conversion step and native layouts. The option tests mirror
/// <c>defaultOptionsMatchTheWhisperToolsCall</c> in
/// mac/HearsayWhisper/Tests/HearsayWhisperTests/TextAndFormatTests.swift and
/// <c>TranscriptionOptions.app(language:)</c> in
/// mac/Hearsay/Features/Transcription/TranscriptionOptions+App.swift; the cue
/// tests mirror <c>Transcription.cues(offset:script:)</c> in WhisperEngine.swift.
/// </summary>
public sealed class WhisperEngineUnitTests
{
    // Options

    [Fact]
    public void DefaultOptionsMatchTheWhisperToolsCall()
    {
        var options = new TranscriptionOptions();
        Assert.Null(options.Language);
        Assert.Null(options.InitialPrompt);
        Assert.False(options.ConditionOnPreviousText);
        Assert.Equal([0f], options.Temperatures);
        Assert.Equal(2.4f, options.CompressionRatioThreshold);
        Assert.Equal(-1.0f, options.LogprobThreshold);
        Assert.Equal(0.6f, options.NoSpeechThreshold);
        Assert.Equal(5, options.BestOf);
        Assert.Null(options.Threads);
    }

    [Theory]
    [InlineData(TranscriptLanguage.English, "en")]
    [InlineData(TranscriptLanguage.ChineseTaiwan, "zh")]
    [InlineData(TranscriptLanguage.ChineseMainland, "zh")]
    [InlineData(TranscriptLanguage.German, "de")]
    [InlineData(TranscriptLanguage.Spanish, "es")]
    public void AppOptionsUseTheSessionLanguageAndNoPrompt(TranscriptLanguage language, string code)
    {
        var options = TranscriptionOptions.App(language);
        Assert.Equal(code, options.Language);
        Assert.Null(options.InitialPrompt);
        Assert.False(options.ConditionOnPreviousText);
        Assert.Equal([0f], options.Temperatures);
        Assert.Equal(2.4f, options.CompressionRatioThreshold);
    }

    [Fact]
    public void ApplyMapsTheMacOptionsOntoWhisperFullParams()
    {
        var p = new WhisperFullParams
        {
            Strategy = WhisperSamplingStrategy.BeamSearch,
            PrintProgress = 1,
            PrintRealtime = 1,
            PrintTimestamps = 1,
            NoTimestamps = 1,
            SingleSegment = 1,
            DetectLanguage = 1,
            TemperatureIncrement = 0.2f,
            Greedy = new WhisperGreedyParams { BestOf = 2 },
            Vad = 1,
        };
        var language = (IntPtr)0x1234;
        TranscriptionOptions.App(TranscriptLanguage.German).Apply(ref p, 7, language, IntPtr.Zero);

        Assert.Equal(WhisperSamplingStrategy.Greedy, p.Strategy);
        Assert.Equal(7, p.Threads);
        Assert.Equal(language, p.Language);
        Assert.Equal(0, p.DetectLanguage);
        Assert.Equal(IntPtr.Zero, p.InitialPrompt);
        Assert.Equal(IntPtr.Zero, p.PromptTokens);
        Assert.Equal(0, p.CarryInitialPrompt);
        Assert.Equal(1, p.NoContext);               // condition_on_previous_text False
        Assert.Equal(0, p.NoTimestamps);            // timestamps on
        Assert.Equal(0, p.SingleSegment);
        Assert.Equal(0, p.Translate);
        Assert.Equal(0f, p.Temperature);            // temperatures [0]: no fallback
        Assert.Equal(0f, p.TemperatureIncrement);
        Assert.Equal(2.4f, p.EntropyThreshold);
        Assert.Equal(-1.0f, p.LogprobThreshold);
        Assert.Equal(0.6f, p.NoSpeechThreshold);
        Assert.Equal(5, p.Greedy.BestOf);
        Assert.Equal(1, p.SuppressBlank);
        Assert.Equal(1, p.SuppressNonSpeechTokens);  // Python suppress_tokens="-1"
        Assert.Equal(0, p.PrintProgress);
        Assert.Equal(0, p.PrintRealtime);
        Assert.Equal(0, p.PrintTimestamps);
        Assert.Equal(0, p.PrintSpecial);
        Assert.Equal(0, p.TokenTimestamps);
        Assert.Equal(0, p.OffsetMs);
        Assert.Equal(0, p.DurationMs);
        Assert.Equal(0, p.Vad);
    }

    [Fact]
    public void ApplyPassesAPromptConditioningAndFallbackWhenAsked()
    {
        var p = new WhisperFullParams();
        var options = new TranscriptionOptions
        {
            Language = "zh",
            InitialPrompt = "以下是繁體中文的句子。",
            ConditionOnPreviousText = true,
            Temperatures = [0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f],
            CompressionRatioThreshold = null,
            LogprobThreshold = null,
            NoSpeechThreshold = null,
            BestOf = 3,
        };
        options.Apply(ref p, 4, (IntPtr)1, (IntPtr)2);
        Assert.Equal((IntPtr)2, p.InitialPrompt);
        Assert.Equal(0, p.NoContext);
        Assert.Equal(0f, p.Temperature);
        Assert.Equal(0.2f, p.TemperatureIncrement, 6);
        Assert.Equal(float.PositiveInfinity, p.EntropyThreshold);
        Assert.Equal(float.NegativeInfinity, p.LogprobThreshold);
        Assert.Equal(float.PositiveInfinity, p.NoSpeechThreshold);
        Assert.Equal(3, p.Greedy.BestOf);
    }

    [Fact]
    public void TemperatureScheduleIsAStartAndAnEvenStepToOne()
    {
        Assert.Equal((0f, 0f), TranscriptionOptions.TemperatureSchedule([0f]));
        Assert.Equal((0.4f, 0f), TranscriptionOptions.TemperatureSchedule([0.4f]));
        var (start, step) = TranscriptionOptions.TemperatureSchedule([0f, 0.2f, 0.4f, 0.6f, 0.8f, 1.0f]);
        Assert.Equal(0f, start);
        Assert.Equal(0.2f, step, 6);
        Assert.Equal((0.5f, 0.5f), TranscriptionOptions.TemperatureSchedule([0.5f, 1.0f]));
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.TemperatureSchedule([]));
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.TemperatureSchedule([0f, 0.2f]));      // stops before 1.0
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.TemperatureSchedule([0f, 0.2f, 0.6f, 1.0f]));
        Assert.Throws<ArgumentException>(() => TranscriptionOptions.TemperatureSchedule([0.4f, 0.2f]));
    }

    [Theory]
    [InlineData(true, 1, 4)]
    [InlineData(true, 4, 4)]
    [InlineData(true, 8, 8)]
    [InlineData(true, 12, 12)]
    [InlineData(true, 32, 12)]
    [InlineData(false, 1, 1)]
    [InlineData(false, 2, 2)]
    [InlineData(false, 12, 4)]
    [InlineData(false, 32, 4)]
    public void DefaultThreadsFollowPlanEighteenFour(bool cpu, int cores, int expected) =>
        Assert.Equal(expected, TranscriptionOptions.DefaultThreads(cpu, cores));

    // Segments and cues

    [Fact]
    public void WhisperCentisecondsBecomeSecondsWithTheRunOffset()
    {
        Assert.Equal(new TranscriptSegment(0, 4.48, " Welcome"), WhisperEngine.ConvertSegment(0, 448, " Welcome", 0));
        Assert.Equal(new TranscriptSegment(90.25, 93, "x"), WhisperEngine.ConvertSegment(25, 300, "x", 90));
        var converted = WhisperEngine.ConvertSegment(2998, 2998, "", 30);
        Assert.Equal(59.98, converted.Start, 9);
    }

    [Fact]
    public void CuesAreStrippedArrowSafeAndShifted()
    {
        var transcription = new WhisperTranscription(
            [new(0, 1.5, "  Hello --> there \n"), new(1.5, 3, " second ")], "en");
        var cues = transcription.Cues(10, null);
        Assert.Equal([new TranscriptSegment(10, 11.5, "Hello -> there"), new TranscriptSegment(11.5, 13, "second")], cues);
        // Script null leaves Chinese untouched.
        var zh = new WhisperTranscription([new(0, 1, " 这是一年里最冷的时候")], "zh");
        Assert.Equal("这是一年里最冷的时候", zh.Cues(0, null)[0].Text);
    }

    [Fact]
    public void ChineseCuesAreConvertedAfterTranscription()
    {
        // turbo q5_0's zh-30s output (W1 spike), cues 2 and 4.
        var transcription = new WhisperTranscription(
            [new(5.28, 10.04, "春节差不多在腊月的初旬就开始了"), new(12.6, 15.48, "这是一年里最冷的时候")], "zh");
        var traditional = transcription.Cues(0, TranscriptLanguage.ChineseTaiwan.ChineseScript());
        Assert.Equal("春節差不多在臘月的初旬就開始了", traditional[0].Text);
        Assert.Equal("這是一年里最冷的時候", traditional[1].Text);  // ICU Hans-Hant keeps 里 (PLAN.md 18.8)
        Assert.Equal(5.28, traditional[0].Start);
        var simplified = new WhisperTranscription(traditional, "zh").Cues(0, TranscriptLanguage.ChineseMainland.ChineseScript());
        Assert.Equal("春节差不多在腊月的初旬就开始了", simplified[0].Text);
        Assert.Null(TranscriptLanguage.German.ChineseScript());
    }

    [Fact]
    public void NoSpeechIsTheSoftmaxOfTheSotLogits()
    {
        float[] logits = [1f, 2f, 3f, 1000f];
        Assert.Equal(1f, WhisperEngine.NoSpeechProbability(logits, 3), 6);
        Assert.Equal(0f, WhisperEngine.NoSpeechProbability(logits, 0), 6);
        float[] even = [0.5f, 0.5f, 0.5f, 0.5f];
        Assert.Equal(0.25f, WhisperEngine.NoSpeechProbability(even, 2), 6);
        double expected = Math.Exp(2) / (Math.Exp(1) + Math.Exp(2) + Math.Exp(3));
        Assert.Equal(expected, WhisperEngine.NoSpeechProbability([1f, 2f, 3f], 1), 6);
    }

    [Fact]
    public void ErrorsCarryTheMacText()
    {
        Assert.Equal("No model installed. Choose a model in Models.",
            new WhisperEngineException(WhisperEngineError.NoActiveModel, null).Message);
        Assert.Equal("The model Large v3 Turbo is not fully downloaded. Finish the download in Models.",
            new WhisperEngineException(WhisperEngineError.ModelNotReady, "Large v3 Turbo").Message);
    }

    [Fact]
    public void AnUnloadedEngineRefusesJobs()
    {
        using var engine = new WhisperEngine();
        Assert.False(engine.IsLoaded);
        Assert.False(engine.IsBusy);
        var error = Assert.Throws<WhisperEngineException>(() =>
            engine.Transcribe(new float[16_000], new TranscriptionOptions { Language = "en" }));
        Assert.Equal(WhisperEngineError.NoActiveModel, error.Error);
        Assert.Throws<WhisperEngineException>(() => engine.DetectLanguage(new float[16_000]));
        Assert.Throws<FileNotFoundException>(() => engine.Load(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no-such-model.bin")));
        Assert.False(engine.IsBusy);
    }

    // WAV reader

    [Theory]
    [InlineData("en-30s.wav")]
    [InlineData("zh-30s.wav")]
    public void FixtureWavsReadAsSixteenKilohertzMono(string name)
    {
        var path = SharedFiles.Path("fixtures", name);
        var samples = PcmWav.ReadMono16k(path);
        Assert.Equal(Hearsay.Core.Audio.WavWriter.DurationOf(path), samples.Length / 16_000.0, 3);
        Assert.InRange(samples.Length, 16_000 * 15, 16_000 * 30);
        Assert.Contains(samples, s => Math.Abs(s) > 0.01f);
    }

    [Fact]
    public void NonPcmWavIsRejected()
    {
        byte[] header = [.. Hearsay.Core.Audio.WavWriter.Header(0)];
        header[24] = 0x44; // 44100 Hz
        header[25] = 0xAC;
        header[26] = 0;
        Assert.Throws<InvalidDataException>(() => PcmWav.ReadMono16k(header, "x.wav"));
        Assert.Throws<InvalidDataException>(() => PcmWav.ReadMono16k("RIFF"u8.ToArray(), "y.wav"));
    }

    // Native layout

    [Theory]
    [InlineData(typeof(WhisperFullParams), "Whisper.net.Native.WhisperFullParams", 304)]
    [InlineData(typeof(WhisperContextParams), "Whisper.net.Native.WhisperContextParams", 48)]
    [InlineData(typeof(WhisperVadParams), "Whisper.net.Native.WhisperVadParams", 24)]
    public void NativeStructsMatchWhisperNetsDeclarations(Type ours, string theirsName, int size)
    {
        var theirs = typeof(global::Whisper.net.WhisperFactory).Assembly.GetType(theirsName, throwOnError: true);
        Assert.NotNull(theirs);
        Assert.Equal(size, Marshal.SizeOf(ours));
        Assert.Equal(size, Marshal.SizeOf(theirs));
        static IEnumerable<(long Offset, int Size)> Layout(Type type) =>
            type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(f => ((long)Marshal.OffsetOf(type, f.Name), FieldSize(f.FieldType)))
                .OrderBy(x => x.Item1);
        Assert.Equal(Layout(theirs), Layout(ours));
    }

    private static int FieldSize(Type type) =>
        type.IsEnum ? Marshal.SizeOf(Enum.GetUnderlyingType(type))
        : type == typeof(bool) ? 1
        : Marshal.SizeOf(type);

    [Fact]
    public void BindsTwentyOneExports()
    {
        Assert.Equal(21, WhisperNative.ExportNames.Count);
        Assert.Equal(WhisperNative.ExportNames.Count, WhisperNative.ExportNames.Distinct().Count());
    }
}
