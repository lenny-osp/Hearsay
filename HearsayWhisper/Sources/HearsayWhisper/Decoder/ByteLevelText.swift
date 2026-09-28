import Foundation

/// GPT-2 byte-level BPE text reconstruction, so decoded text equals what
/// tiktoken returns in mlx_whisper: token strings are mapped back to raw
/// bytes and the bytes are decoded as UTF-8 with U+FFFD for invalid
/// sequences (`errors="replace"`).
///
/// This deliberately bypasses swift-transformers' `decode`, which applies
/// `clean_up_tokenization_spaces` (for example " ." to ".") and would make
/// segment text differ from the Python tool.
enum ByteLevelText {
    /// `bytes_to_unicode()` from GPT-2, inverted: unicode scalar -> byte.
    static let unicodeToByte: [UnicodeScalar: UInt8] = {
        var bytes: [Int] = Array(33...126) + Array(161...172) + Array(174...255)
        var scalars = bytes
        var extra = 0
        for byte in 0..<256 where !bytes.contains(byte) {
            bytes.append(byte)
            scalars.append(256 + extra)
            extra += 1
        }
        var table: [UnicodeScalar: UInt8] = [:]
        for (byte, scalar) in zip(bytes, scalars) {
            if let unicode = UnicodeScalar(scalar) {
                table[unicode] = UInt8(byte)
            }
        }
        return table
    }()

    /// Raw bytes of one byte-level token string. Scalars outside the table
    /// (special tokens such as `<|en|>` are plain ASCII and are in it) are
    /// passed through as their UTF-8 bytes.
    static func bytes(of tokenString: String) -> [UInt8] {
        var result: [UInt8] = []
        for scalar in tokenString.unicodeScalars {
            if let byte = unicodeToByte[scalar] {
                result.append(byte)
            } else {
                result.append(contentsOf: Array(String(scalar).utf8))
            }
        }
        return result
    }

    static func decode(tokenStrings: [String]) -> String {
        let bytes = tokenStrings.flatMap { self.bytes(of: $0) }
        return String(decoding: bytes, as: UTF8.self)
    }
}
