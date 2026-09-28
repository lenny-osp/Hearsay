// Ported from mlx_whisper 0.4.3 (ml-explore/mlx-examples, MIT, Copyright © 2023 Apple Inc.); see LICENSES/mlx-whisper.txt.

import CZlib
import Foundation

/// Port of `compression_ratio` in mlx_whisper `decoding.py`:
/// `len(utf8) / len(zlib.compress(utf8))`.
///
/// Choice: this links the system libz and calls `compress()` (zlib container,
/// `Z_DEFAULT_COMPRESSION`), which is exactly what Python's `zlib.compress`
/// does with its default level. Apple's Compression framework and
/// `NSData.compressed(using: .zlib)` emit raw DEFLATE without the 2-byte
/// header and 4-byte Adler-32 trailer and use a different level, so their
/// lengths would not match Python's.
public func compressionRatio(_ text: String) -> Double {
    let bytes = Array(text.utf8)
    let compressed = zlibCompressedLength(bytes)
    guard compressed > 0 else { return 0 }
    return Double(bytes.count) / Double(compressed)
}

/// Length of `zlib.compress(bytes)` at the default level; 0 on failure.
func zlibCompressedLength(_ bytes: [UInt8]) -> Int {
    var destinationLength = compressBound(uLong(bytes.count))
    var destination = [UInt8](repeating: 0, count: Int(destinationLength))
    let status = bytes.withUnsafeBufferPointer { source in
        destination.withUnsafeMutableBufferPointer { target in
            compress(target.baseAddress, &destinationLength, source.baseAddress, uLong(bytes.count))
        }
    }
    guard status == Z_OK else { return 0 }
    return Int(destinationLength)
}
