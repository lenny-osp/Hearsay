using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hearsay.Core.ModelStore;

/// <summary>
/// One downloadable Whisper model (PLAN.md section 5, schema in
/// shared/models/README.md). Port of <c>ModelCatalogEntry</c> in
/// mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelCatalog.swift.
/// <para>
/// Windows difference: an entry is one whisper.cpp GGML file from
/// <c>ggerganov/whisper.cpp</c> with the tokenizer built in, so
/// <see cref="WeightsFile"/> is the whole download (no <c>config.json</c>).
/// Every entry shares that one repo, so the stable id is
/// <see cref="Repo"/> + <c>"/"</c> + <see cref="WeightsFile"/>; the Mac uses
/// the repo alone because each MLX model has a repo of its own.
/// </para>
/// </summary>
public sealed record ModelCatalogEntry
{
    /// <summary>Hugging Face repo, for example <c>ggerganov/whisper.cpp</c>.</summary>
    [JsonPropertyName("repo")]
    public required string Repo { get; init; }

    /// <summary>Name shown in the Models tab (a product name, not localized).</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>Download size in bytes of <see cref="WeightsFile"/>, as reported by the Hugging Face API.</summary>
    [JsonPropertyName("sizeBytes")]
    public required long SizeBytes { get; init; }

    /// <summary>Name of the GGML file in the repo, for example <c>ggml-large-v3-turbo-q5_0.bin</c>.</summary>
    [JsonPropertyName("weightsFile")]
    public required string WeightsFile { get; init; }

    /// <summary>The GGML quantization name: <c>f16</c>, <c>q5_0</c>, <c>q8_0</c>, ...</summary>
    [JsonPropertyName("quantization")]
    public required string Quantization { get; init; }

    /// <summary>Grouping key, one of <see cref="ModelCatalog.FamilyOrder"/>.</summary>
    [JsonPropertyName("family")]
    public required string Family { get; init; }

    [JsonPropertyName("recommended")]
    public required bool Recommended { get; init; }

    /// <summary>Stable id: <c>repo/weightsFile</c> (the Mac's id is the repo).</summary>
    [JsonIgnore]
    public string Id => Repo + "/" + WeightsFile;

    /// <summary>Files fetched from the model repo, in download order (the Mac adds <c>config.json</c> first).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Files => [WeightsFile];
}

/// <summary>
/// The shared tokenizer files, fetched once. Port of <c>TokenizerSource</c> in
/// ModelCatalog.swift. The Windows catalog leaves <see cref="Files"/> empty
/// because whisper.cpp models carry their tokenizer.
/// </summary>
public sealed record TokenizerSource
{
    [JsonPropertyName("repo")]
    public required string Repo { get; init; }

    [JsonPropertyName("files")]
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>Value equality over the file list too (records compare lists by reference).</summary>
    public bool Equals(TokenizerSource? other) =>
        other is not null && Repo == other.Repo && Files.SequenceEqual(other.Files);

    public override int GetHashCode() => HashCode.Combine(Repo, Files.Count);
}

/// <summary>The built-in model list could not be read.</summary>
public sealed class ModelCatalogException : Exception
{
    public ModelCatalogException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Built-in list of downloadable models, decoded from the embedded
/// <c>Resources/ModelCatalog.json</c> (Windows-only list, shared schema).
/// Port of <c>ModelCatalog</c> in
/// mac/HearsayCore/Sources/HearsayCore/ModelStore/ModelCatalog.swift. Unknown
/// JSON keys are ignored, as Swift's <c>Codable</c> does, so the file can
/// carry a leading <c>"about"</c> note; a missing key is an error.
/// </summary>
public sealed class ModelCatalog
{
    internal const string ResourceName = "Hearsay.Core.Resources.ModelCatalog.json";

    private static readonly JsonSerializerOptions DecodeOptions = new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public ModelCatalog(IReadOnlyList<ModelCatalogEntry> entries, TokenizerSource tokenizer)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(tokenizer);
        Entries = entries;
        Tokenizer = tokenizer;
    }

    /// <summary>Family order for display, smallest first.</summary>
    public static IReadOnlyList<string> FamilyOrder { get; } =
        ["tiny", "base", "small", "medium", "large-v3-turbo", "large-v3"];

    public IReadOnlyList<ModelCatalogEntry> Entries { get; }

    public TokenizerSource Tokenizer { get; }

    /// <summary>The first entry marked recommended, or null.</summary>
    public ModelCatalogEntry? Recommended => Entries.FirstOrDefault(entry => entry.Recommended);

    /// <summary>The entry with this <see cref="ModelCatalogEntry.Id"/> (the Mac's <c>entry(forRepo:)</c>).</summary>
    public ModelCatalogEntry? Entry(string id) => Entries.FirstOrDefault(entry => entry.Id == id);

    /// <summary>The catalog embedded in Hearsay.Core.</summary>
    /// <exception cref="ModelCatalogException">The resource is missing or not valid catalog JSON.</exception>
    public static ModelCatalog Bundled()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new ModelCatalogException($"Embedded resource '{ResourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Decode(buffer.ToArray());
    }

    /// <summary>Decodes catalog JSON in the shared schema.</summary>
    /// <exception cref="ModelCatalogException">The JSON is not a valid catalog.</exception>
    public static ModelCatalog Decode(ReadOnlySpan<byte> json)
    {
        Document? document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(json, DecodeOptions);
        }
        catch (JsonException error)
        {
            throw new ModelCatalogException(error.Message, error);
        }
        if (document is null)
        {
            throw new ModelCatalogException("The model list is empty.");
        }
        return new ModelCatalog(document.Entries, document.Tokenizer);
    }

    private sealed class Document
    {
        [JsonPropertyName("entries")]
        public required IReadOnlyList<ModelCatalogEntry> Entries { get; init; }

        [JsonPropertyName("tokenizer")]
        public required TokenizerSource Tokenizer { get; init; }
    }
}
