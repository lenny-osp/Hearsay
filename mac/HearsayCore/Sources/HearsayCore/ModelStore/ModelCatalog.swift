import Foundation

/// One downloadable Whisper model (PLAN.md section 5). Every entry is an
/// `mlx-community` repo that holds `config.json` plus one safetensors file.
public struct ModelCatalogEntry: Codable, Identifiable, Sendable, Hashable {
    /// Hugging Face repo, for example `mlx-community/whisper-large-v3-turbo`.
    public let repo: String
    public let displayName: String
    /// Download size in bytes: `config.json` plus the weights file, as
    /// reported by the Hugging Face API.
    public let sizeBytes: Int64
    /// Name of the safetensors file in the repo.
    public let weightsFile: String
    /// "fp16", "8bit", or "4bit".
    public let quantization: String
    /// Model family used for grouping: "tiny", "base", "small", "medium",
    /// "large-v3", "large-v3-turbo".
    public let family: String
    public let recommended: Bool

    public var id: String { repo }

    /// Files fetched from the model repo, in download order.
    public var files: [String] { ["config.json", weightsFile] }

    public init(
        repo: String,
        displayName: String,
        sizeBytes: Int64,
        weightsFile: String,
        quantization: String,
        family: String,
        recommended: Bool
    ) {
        self.repo = repo
        self.displayName = displayName
        self.sizeBytes = sizeBytes
        self.weightsFile = weightsFile
        self.quantization = quantization
        self.family = family
        self.recommended = recommended
    }
}

/// The shared tokenizer files, fetched once from an `openai/whisper-*` repo.
public struct TokenizerSource: Codable, Sendable, Hashable {
    public let repo: String
    public let files: [String]

    public init(repo: String, files: [String]) {
        self.repo = repo
        self.files = files
    }
}

/// Built-in list of downloadable models, decoded from `ModelCatalog.json`.
public struct ModelCatalog: Codable, Sendable {
    public let entries: [ModelCatalogEntry]
    public let tokenizer: TokenizerSource

    public init(entries: [ModelCatalogEntry], tokenizer: TokenizerSource) {
        self.entries = entries
        self.tokenizer = tokenizer
    }

    public enum LoadError: Error, Equatable {
        case missingResource
    }

    /// Family order for display, smallest first.
    public static let familyOrder = ["tiny", "base", "small", "medium", "large-v3-turbo", "large-v3"]

    /// The catalog shipped inside the HearsayCore resource bundle.
    public static func bundled() throws -> ModelCatalog {
        guard let url = Bundle.module.url(forResource: "ModelCatalog", withExtension: "json") else {
            throw LoadError.missingResource
        }
        return try JSONDecoder().decode(ModelCatalog.self, from: Data(contentsOf: url))
    }

    public var recommended: ModelCatalogEntry? {
        entries.first(where: \.recommended)
    }

    public func entry(forRepo repo: String) -> ModelCatalogEntry? {
        entries.first { $0.repo == repo }
    }
}
