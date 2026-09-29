using Hearsay.Core;
using Hearsay.Core.ModelStore;

namespace Hearsay.Whisper;

/// <summary>
/// Errors raised before the engine runs. Port of <c>WhisperEngineError</c> in
/// mac/Hearsay/Features/Transcription/WhisperEngine.swift. The text is the
/// Mac's English string (a Windows key for <see cref="WhisperEngineError.LoadFailed"/>);
/// <see cref="LocalizedMessage"/> carries the key for the app.
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
        : base(Describe(error, modelName, null))
    {
        Error = error;
        ModelName = modelName;
    }

    private WhisperEngineException(Exception cause)
        : base(Describe(WhisperEngineError.LoadFailed, null, cause.Message), cause)
    {
        Error = WhisperEngineError.LoadFailed;
        Detail = cause.Message;
    }

    public WhisperEngineError? Error { get; }

    public string? ModelName { get; }

    /// <summary>The technical reason of <see cref="WhisperEngineError.LoadFailed"/> (the cause's message, English).</summary>
    public string? Detail { get; }

    /// <summary>
    /// <see cref="WhisperEngineError.LoadFailed"/> for <paramref name="cause"/>:
    /// whisper.cpp or its runtime could not load the model. The Mac passes
    /// such errors through unchanged; Windows wraps the technical message in
    /// a sentence the app can translate and keeps the cause as the inner exception.
    /// </summary>
    public static WhisperEngineException LoadFailed(Exception cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return new WhisperEngineException(cause);
    }

    /// <summary>
    /// The message as the Mac's catalog key (app catalog, WhisperEngine.swift)
    /// and the model name; null when made from a bare message.
    /// </summary>
    public ILocalizedMessage? LocalizedMessage => Error switch
    {
        WhisperEngineError.NoActiveModel => new LocalizedMessage("No model installed. Choose a model in Models."),
        WhisperEngineError.ModelNotReady => new LocalizedMessage(
            "The model %@ is not fully downloaded. Finish the download in Models.", ModelName ?? string.Empty),
        WhisperEngineError.LoadFailed => new LocalizedMessage("Could not load the speech model: %@", Detail ?? string.Empty),
        _ => null,
    };

    private static string Describe(WhisperEngineError error, string? modelName, string? detail) => error switch
    {
        WhisperEngineError.NoActiveModel => "No model installed. Choose a model in Models.",
        WhisperEngineError.ModelNotReady =>
            $"The model {modelName} is not fully downloaded. Finish the download in Models.",
        WhisperEngineError.LoadFailed => $"Could not load the speech model: {detail}",
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

    /// <summary>
    /// Windows only: whisper.cpp or its runtime could not load the model
    /// file (missing, unreadable, no runtime, out of memory);
    /// <see cref="WhisperEngineException.Detail"/> says why.
    /// </summary>
    LoadFailed,
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
