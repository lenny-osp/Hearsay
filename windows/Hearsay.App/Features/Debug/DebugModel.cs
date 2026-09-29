using Hearsay.Core.ModelStore;

namespace Hearsay.App.Features.Debug;

/// <summary>
/// The model a debug entry point uses: <c>HEARSAY_MODEL_DIR</c>, bypassing
/// <see cref="ModelStore"/> as on the Mac (mac/Hearsay/Features/FileTranscription/FileViewModel.swift,
/// <c>runDebugTranscriptionIfRequested</c>). The Mac's variable names a
/// folder with the MLX model and tokenizer; a whisper.cpp model is one file,
/// so on Windows the variable may name the <c>ggml-*.bin</c> itself, or a
/// folder holding it (<c>windows\Spike\models</c>), in which the catalog's
/// recommended model is used, else the only <c>ggml-*.bin</c>.
/// </summary>
internal static class DebugModel
{
    public const string Variable = "HEARSAY_MODEL_DIR";

    /// <summary>The model file, or an error message for the console.</summary>
    public static (string? Path, string? Error) Resolve(string value, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var full = System.IO.Path.GetFullPath(value);
        if (File.Exists(full)) return (full, null);
        if (!Directory.Exists(full)) return (null, $"{Variable}: {full} does not exist");
        if (catalog.Recommended is { } recommended && File.Exists(System.IO.Path.Combine(full, recommended.WeightsFile)))
        {
            return (System.IO.Path.Combine(full, recommended.WeightsFile), null);
        }
        var models = Directory.GetFiles(full, "ggml-*.bin");
        return models.Length == 1
            ? (models[0], null)
            : (null, $"{Variable}: {full} holds {models.Length} ggml-*.bin files and not the recommended one; name the file");
    }
}
