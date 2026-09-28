import Foundation

/// Errors from reading or repairing a WAV header.
public enum WavError: Error, Equatable, Sendable, CustomStringConvertible {
    case notAWavFile(String)
    case closed

    public var description: String {
        switch self {
        case .notAWavFile(let path):
            String(localized: "Not a readable WAV file: \(path)", bundle: .module,
                   comment: "Audio error. %@ is a file path.")
        case .closed:
            String(localized: "The WAV file is already closed.", bundle: .module, comment: "Audio error")
        }
    }
}

/// Writes the spooled recording in the same format the Python tool keeps
/// (`build_streaming_ffmpeg_argv`: `-ar 16000 -ac 1 -c:a pcm_s16le -f wav`):
/// a canonical 44-byte RIFF/WAVE header followed by 16 kHz mono signed
/// 16-bit little-endian PCM.
///
/// The header is written with zero sizes and patched on `close()`. A crash
/// leaves zeros there, which `patchHeader(at:)` repairs from the file size.
/// Not thread-safe: append and close from one context.
public final class WavWriter {
    public static let sampleRate = 16_000
    public static let channels = 1
    public static let bitsPerSample = 16
    public static let headerSize = 44
    /// Bytes of header scanned when inspecting a WAV (`MAX_WAV_HEADER_BYTES`).
    static let maxHeaderBytes = 4096

    public let url: URL
    /// Samples written so far.
    public private(set) var sampleCount = 0
    private var handle: FileHandle?

    /// Creates (or truncates) the file at `url`, creating its folder if
    /// needed, and writes a header with zero sizes.
    public init(url: URL) throws {
        self.url = url
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard fileManager.createFile(atPath: url.path, contents: Self.header(dataBytes: 0)) else {
            throw CocoaError(.fileWriteUnknown, userInfo: [NSFilePathErrorKey: url.path])
        }
        handle = try FileHandle(forWritingTo: url)
        _ = try handle?.seekToEnd()
    }

    deinit {
        try? handle?.close()
    }

    /// Seconds of audio written so far.
    public var duration: TimeInterval {
        Double(sampleCount) / Double(Self.sampleRate)
    }

    /// Appends samples, clamping each to [-1, 1] and converting to Int16.
    public func append(_ samples: [Float]) throws {
        guard let handle else { throw WavError.closed }
        guard !samples.isEmpty else { return }
        try handle.write(contentsOf: Self.pcmData(samples))
        sampleCount += samples.count
    }

    /// Patches the RIFF and data sizes and closes the file. Calling it again
    /// does nothing.
    public func close() throws {
        guard let handle else { return }
        self.handle = nil
        let dataBytes = Self.clampedUInt32(sampleCount * Self.bitsPerSample / 8 * Self.channels)
        try handle.seek(toOffset: 4)
        try handle.write(contentsOf: Self.littleEndian(Self.clampedUInt32(Int(dataBytes) + Self.headerSize - 8)))
        try handle.seek(toOffset: 40)
        try handle.write(contentsOf: Self.littleEndian(dataBytes))
        try handle.synchronize()
        try handle.close()
    }

    // MARK: - Crash recovery and inspection

    /// Whether the header of the file at `url` still carries the zero sizes
    /// of an unfinished recording while the file holds audio.
    public static func hasUnfinishedHeader(at url: URL) throws -> Bool {
        let info = try inspect(url: url)
        return info.needsPatch
    }

    /// Rewrites the RIFF and data sizes from the file size when the header
    /// still says 0 (or claims more data than the file holds). Returns
    /// whether the file was changed.
    @discardableResult
    public static func patchHeader(at url: URL) throws -> Bool {
        let info = try inspect(url: url)
        guard info.needsPatch else { return false }
        let handle = try FileHandle(forUpdating: url)
        defer { try? handle.close() }
        try handle.seek(toOffset: 4)
        try handle.write(contentsOf: littleEndian(clampedUInt32(Int(info.fileSize) - 8)))
        try handle.seek(toOffset: UInt64(info.dataSizeOffset))
        try handle.write(contentsOf: littleEndian(clampedUInt32(Int(info.availableDataBytes))))
        try handle.synchronize()
        return true
    }

    /// Duration of the audio in the WAV at `url`. An unfinished header is
    /// measured from the file size.
    public static func duration(of url: URL) throws -> TimeInterval {
        let info = try inspect(url: url)
        let bytesPerSecond = Double(info.sampleRate) * Double(info.blockAlign)
        guard bytesPerSecond > 0 else { throw WavError.notAWavFile(url.path) }
        return Double(info.effectiveDataBytes) / bytesPerSecond
    }

    // MARK: - Header layout

    struct Info: Equatable {
        var fileSize: UInt64
        var riffSize: UInt32
        var sampleRate: UInt32
        var channels: UInt16
        var bitsPerSample: UInt16
        var blockAlign: UInt16
        var dataSizeOffset: Int
        var dataOffset: Int
        var declaredDataBytes: UInt32

        /// Data bytes the file actually holds after the data chunk header,
        /// rounded down to whole frames.
        var availableDataBytes: UInt64 {
            let raw = fileSize > UInt64(dataOffset) ? fileSize - UInt64(dataOffset) : 0
            let align = UInt64(max(blockAlign, 1))
            return raw - raw % align
        }

        var needsPatch: Bool {
            guard availableDataBytes > 0 else { return false }
            return declaredDataBytes == 0 || riffSize == 0 || UInt64(declaredDataBytes) > availableDataBytes
        }

        var effectiveDataBytes: UInt64 {
            needsPatch ? availableDataBytes : UInt64(declaredDataBytes)
        }
    }

    static func inspect(url: URL) throws -> Info {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let head = try handle.read(upToCount: maxHeaderBytes) ?? Data()
        let fileSize = try handle.seekToEnd()
        let bytes = [UInt8](head)
        func u32(_ offset: Int) -> UInt32? {
            guard offset + 4 <= bytes.count else { return nil }
            return UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8
                | UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
        }
        func u16(_ offset: Int) -> UInt16? {
            guard offset + 2 <= bytes.count else { return nil }
            return UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
        }
        func tag(_ offset: Int) -> String? {
            guard offset + 4 <= bytes.count else { return nil }
            return String(bytes: bytes[offset..<offset + 4], encoding: .ascii)
        }
        guard tag(0) == "RIFF", tag(8) == "WAVE", let riffSize = u32(4) else {
            throw WavError.notAWavFile(url.path)
        }
        var format: (rate: UInt32, channels: UInt16, bits: UInt16, align: UInt16)?
        var offset = 12
        while let id = tag(offset), let size = u32(offset + 4) {
            let body = offset + 8
            if id == "fmt " {
                guard let channels = u16(body + 2), let rate = u32(body + 4),
                      let align = u16(body + 12), let bits = u16(body + 14)
                else { break }
                format = (rate, channels, bits, align)
            } else if id == "data" {
                guard let format else { break }
                return Info(
                    fileSize: fileSize, riffSize: riffSize, sampleRate: format.rate,
                    channels: format.channels, bitsPerSample: format.bits, blockAlign: format.align,
                    dataSizeOffset: offset + 4, dataOffset: body, declaredDataBytes: size
                )
            }
            // Chunks are padded to an even size.
            offset = body + Int(size) + Int(size % 2)
        }
        throw WavError.notAWavFile(url.path)
    }

    /// Canonical 44-byte header for 16 kHz mono s16le with `dataBytes` of audio.
    static func header(dataBytes: UInt32) -> Data {
        let byteRate = UInt32(sampleRate * channels * bitsPerSample / 8)
        let blockAlign = UInt16(channels * bitsPerSample / 8)
        var data = Data()
        data.append(contentsOf: Array("RIFF".utf8))
        data.append(littleEndian(clampedUInt32(Int(dataBytes) + headerSize - 8)))
        data.append(contentsOf: Array("WAVEfmt ".utf8))
        data.append(littleEndian(UInt32(16)))
        data.append(littleEndian(UInt16(1)))  // PCM
        data.append(littleEndian(UInt16(channels)))
        data.append(littleEndian(UInt32(sampleRate)))
        data.append(littleEndian(byteRate))
        data.append(littleEndian(blockAlign))
        data.append(littleEndian(UInt16(bitsPerSample)))
        data.append(contentsOf: Array("data".utf8))
        data.append(littleEndian(dataBytes))
        return data
    }

    /// Float samples as s16le bytes: NaN becomes 0, the rest is clamped to
    /// [-1, 1] and scaled by 32767.
    static func pcmData(_ samples: [Float]) -> Data {
        var data = Data(count: samples.count * 2)
        data.withUnsafeMutableBytes { raw in
            for (index, sample) in samples.enumerated() {
                let value = int16(sample).littleEndian
                raw.storeBytes(of: value, toByteOffset: index * 2, as: Int16.self)
            }
        }
        return data
    }

    static func int16(_ sample: Float) -> Int16 {
        guard !sample.isNaN else { return 0 }
        let clamped = Swift.max(-1, Swift.min(1, sample))
        return Int16((clamped * 32767).rounded())
    }

    private static func clampedUInt32(_ value: Int) -> UInt32 {
        UInt32(clamping: value)
    }

    private static func littleEndian<T: FixedWidthInteger>(_ value: T) -> Data {
        withUnsafeBytes(of: value.littleEndian) { Data($0) }
    }
}
