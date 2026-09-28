// Ported from mlx_whisper 0.4.3 (ml-explore/mlx-examples, MIT, Copyright © 2023 Apple Inc.); see LICENSES/mlx-whisper.txt.

import Foundation
import MLX
import MLXFFT

/// Port of `log_mel_spectrogram` in mlx_whisper `audio.py`, computed over the
/// whole recording at once as `transcribe()` does
/// (`log_mel_spectrogram(audio, padding=N_SAMPLES)`): the audio is padded
/// with `padding` zero samples, reflect-padded by `N_FFT / 2`, framed, and
/// the `max - 8` floor is taken over the whole spectrogram, not per window.
/// The STFT runs in blocks of frames so a long recording does not
/// materialize every windowed frame at once; the result is identical.
///
/// Returns `[nFrames, nMels]` float32, like the Python function.
func logMelSpectrogram(samples: [Float], nMels: Int, padding: Int) -> MLXArray {
    let nFft = WhisperAudioConfig.nFft
    let hop = WhisperAudioConfig.hopLength
    let half = nFft / 2

    var audio = samples
    if padding > 0 {
        audio.append(contentsOf: repeatElement(Float(0), count: padding))
    }
    let n = audio.count
    guard n > half + 1 else {
        return MLXArray.zeros([0, nMels], type: Float.self)
    }

    // stft(..., pad_mode="reflect"): x[1:half+1][::-1] + x + x[-(half+1):-1][::-1]
    var padded: [Float] = []
    padded.reserveCapacity(n + 2 * half)
    padded.append(contentsOf: audio[1...half].reversed())
    padded.append(contentsOf: audio)
    padded.append(contentsOf: audio[(n - half - 1)..<(n - 1)].reversed())

    // t = (x.size - nperseg + noverlap) // noverlap, then freqs[:-1] drops one.
    let frameCount = (padded.count - nFft + hop) / hop - 1
    guard frameCount > 0 else {
        return MLXArray.zeros([0, nMels], type: Float.self)
    }

    // hanning(N_FFT) = np.hanning(N_FFT + 1)[:-1], computed in float64.
    let window = MLXArray((0..<nFft).map { i in
        Float(0.5 - 0.5 * cos(2.0 * Double.pi * Double(i) / Double(nFft)))
    })
    let filters = WhisperAudio.melFilters(nMels: nMels)  // [n_fft/2 + 1, nMels]
    let signal = MLXArray(padded)

    let blockFrames = 6_000
    var blocks: [MLXArray] = []
    var start = 0
    while start < frameCount {
        let count = min(blockFrames, frameCount - start)
        let frames = asStrided(signal, [count, nFft], strides: [hop, 1], offset: start * hop)
        let spectrum = MLXFFT.rfft(frames * window, axis: -1)
        let magnitudes = MLX.abs(spectrum).square()
        let mel = MLX.matmul(magnitudes, filters)
        let logBlock = MLX.log10(MLX.maximum(mel, MLXArray(Float(1e-10))))
        eval(logBlock)
        blocks.append(logBlock)
        start += count
    }

    var logSpec = blocks.count == 1 ? blocks[0] : MLX.concatenated(blocks, axis: 0)
    logSpec = MLX.maximum(logSpec, logSpec.max() - MLXArray(Float(8.0)))
    logSpec = (logSpec + MLXArray(Float(4.0))) / MLXArray(Float(4.0))
    eval(logSpec)
    return logSpec
}

/// Port of `pad_or_trim(array, N_FRAMES, axis=-2)` for a `[frames, nMels]`
/// slice: trims, or pads with zeros (in the log-mel domain, as Python does).
func padOrTrimFrames(_ mel: MLXArray, length: Int) -> MLXArray {
    let frames = mel.dim(0)
    if frames > length {
        return mel[0..<length]
    }
    if frames < length {
        return MLX.padded(mel, widths: [.init((0, length - frames)), .init((0, 0))])
    }
    return mel
}
