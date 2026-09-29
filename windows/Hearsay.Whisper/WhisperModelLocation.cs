using Hearsay.Core;
using Hearsay.Core.ModelStore;

namespace Hearsay.Whisper;

/// <summary>
/// Errors raised before the engine runs. Port of <c>WhisperEngineError</c> in
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift. The text is the
/// Mac's English string; the app localizes it in W7.
/// </summary>
public sealed class WhisperEngineException : Exception, ILocalizedError
{
    public WhisperEngineException()
        : this(WhisperEngineError.NoActiveModel, null)
    {
    }

    public WhisperEngineException(string message)
        : base(message)
    {
    }

    public WhisperEngineException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WhisperEngineException(WhisperEngineError error, string? modelName)
        : base(Describe(error, modelName))
    {
        Error = error;
        ModelName = modelName;
    }

    public WhisperEngineError? Error { get; }

    public string? ModelName { get; }

    /// <summary>
    /// The message as the Mac's catalog key (app catalog, WhisperEngine.swift)
    /// and the model name; null when made from a bare message.
    /// </summary>
    public ILocalizedMessage? LocalizedMessage => Error switch
    {
        WhisperEngineError.NoActiveModel => new LocalizedMessage("No model installed. Choose a model in Models."),
        WhisperEngineError.ModelNotReady => new LocalizedMessage(
            "The model %@ is not fully downloaded. Finish the download in Models.", ModelName ?? string.Empty),
        _ => null,
    };

    private static string Describe(WhisperEngineError error, string? modelName) => error switch
    {
        WhisperEngineError.NoActiveModel => "No model installed. Choose a model in Models.",
        WhisperEngineError.ModelNotReady =>
            $"The model {modelName} is not fully downloaded. Finish the download in Models.",
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, null),
    };
}

/// <summary>The cases of <see cref="WhisperEngineException"/>.</summary>
public enum WhisperEngineError
{
    /// <summary>No model is chosen in the Models tab.</summary>
    NoActiveModel,

    /// <summary>The chosen model is not fully downloaded.</summary>
    ModelNotReady,
}

/// <summary>
/// The GGUF file <see cref="WhisperEngine.Load"/> needs, read from
/// <see cref="ModelStore"/>. Port of <c>WhisperModelLocation</c> in
/// WhisperEngine.swift; on Windows one weights file replaces the Mac's model
/// and tokenizer folders.
/// </summary>
public static class WhisperModelLocation
{
    /// <summary>The active model's weights file.</summary>
    /// <exception cref="WhisperEngineException">No active model, or it is not fully downloaded.</exception>
    public static string Active(ModelStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var entry = store.ActiveEntry ?? throw new WhisperEngineException(WhisperEngineError.NoActiveModel, null);
        if (!store.IsReady(entry))
        {
            throw new WhisperEngineException(WhisperEngineError.ModelNotReady, entry.DisplayName);
        }
        return Path.Combine(store.ModelDirectory(entry.Id), entry.WeightsFile);
    }
}
