// Draws the Hearsay app icon and writes every PNG the macOS AppIcon set needs.
// Usage, from the repo root: swift Scripts/make-icon.swift
//
// The icon is a macOS-style rounded square (Big Sur grid: 824 pt body on a
// 1024 pt canvas) filled with a vertical deep-teal-to-blue gradient, with the
// SF Symbol "waveform" drawn large and white in the center. Each size is
// rendered directly at its pixel size rather than downscaled from 1024.

import AppKit
import Foundation

let pixelSizes = [16, 32, 64, 128, 256, 512, 1024]

/// (point size, scale) pairs of the macOS app icon set, mapped to pixel sizes.
let slots: [(points: Int, scale: Int)] = [
    (16, 1), (16, 2), (32, 1), (32, 2), (128, 1), (128, 2), (256, 1), (256, 2), (512, 1), (512, 2),
]

func fileName(forPixels pixels: Int) -> String { "icon_\(pixels).png" }

enum IconError: Error, CustomStringConvertible {
    case bitmap(Int)
    case symbol
    case encode(Int)

    var description: String {
        switch self {
        case .bitmap(let size): return "could not create a \(size) px bitmap"
        case .symbol: return "SF Symbol 'waveform' is not available"
        case .encode(let size): return "could not encode the \(size) px PNG"
        }
    }
}

func renderIcon(pixels: Int) throws -> Data {
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw IconError.bitmap(pixels)
    }
    bitmap.size = NSSize(width: pixels, height: pixels)

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    defer { NSGraphicsContext.restoreGraphicsState() }

    let unit = CGFloat(pixels) / 1024
    let body = NSRect(x: 100 * unit, y: 100 * unit, width: 824 * unit, height: 824 * unit)
    let radius = 185 * unit
    let shape = NSBezierPath(roundedRect: body, xRadius: radius, yRadius: radius)

    // Soft drop shadow under the body, like system icons.
    if pixels >= 64 {
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.3)
        shadow.shadowOffset = NSSize(width: 0, height: -10 * unit)
        shadow.shadowBlurRadius = 20 * unit
        shadow.set()
        NSColor.black.setFill()
        shape.fill()
        NSGraphicsContext.restoreGraphicsState()
    }

    // Vertical gradient: blue at the bottom, deep teal at the top.
    let top = NSColor(srgbRed: 0.05, green: 0.46, blue: 0.50, alpha: 1)
    let bottom = NSColor(srgbRed: 0.10, green: 0.28, blue: 0.72, alpha: 1)
    if let gradient = NSGradient(starting: bottom, ending: top) {
        gradient.draw(in: shape, angle: 90)
    }

    // White waveform glyph, centered, about 60 percent of the body width.
    let config = NSImage.SymbolConfiguration(pointSize: 480 * unit, weight: .semibold)
        .applying(NSImage.SymbolConfiguration(paletteColors: [.white]))
    guard let symbol = NSImage(systemSymbolName: "waveform", accessibilityDescription: nil)?
        .withSymbolConfiguration(config) else {
        throw IconError.symbol
    }
    let symbolSize = symbol.size
    let targetWidth = 500 * unit
    let scale = targetWidth / symbolSize.width
    let drawSize = NSSize(width: symbolSize.width * scale, height: symbolSize.height * scale)
    let origin = NSPoint(x: body.midX - drawSize.width / 2, y: body.midY - drawSize.height / 2)
    symbol.draw(in: NSRect(origin: origin, size: drawSize), from: .zero, operation: .sourceOver, fraction: 1)

    context.flushGraphics()
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw IconError.encode(pixels)
    }
    return png
}

let scriptURL = URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL
let repoRoot = scriptURL.deletingLastPathComponent().deletingLastPathComponent()
let iconSet = repoRoot.appendingPathComponent("Hearsay/Resources/Assets.xcassets/AppIcon.appiconset")

do {
    try FileManager.default.createDirectory(at: iconSet, withIntermediateDirectories: true)
    for pixels in pixelSizes {
        let data = try renderIcon(pixels: pixels)
        let url = iconSet.appendingPathComponent(fileName(forPixels: pixels))
        try data.write(to: url, options: .atomic)
        print("wrote \(url.lastPathComponent) (\(pixels)x\(pixels), \(data.count) bytes)")
    }

    let images: [[String: String]] = slots.map { slot in
        [
            "filename": fileName(forPixels: slot.points * slot.scale),
            "idiom": "mac",
            "scale": "\(slot.scale)x",
            "size": "\(slot.points)x\(slot.points)",
        ]
    }
    let contents: [String: Any] = [
        "images": images,
        "info": ["author": "xcode", "version": 1],
    ]
    let json = try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
    try json.write(to: iconSet.appendingPathComponent("Contents.json"), options: .atomic)
    print("updated Contents.json")
} catch {
    FileHandle.standardError.write(Data("make-icon: \(error)\n".utf8))
    exit(1)
}
