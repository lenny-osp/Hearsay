// Draws the Hearsay app icon and writes every PNG the macOS AppIcon set needs.
// Usage, from the repo root: swift Scripts/make-icon.swift
//
// The icon is a macOS-style rounded square (Big Sur grid: 824 pt body on a
// 1024 pt canvas) filled with a vertical deep-teal-to-blue gradient, with a
// white robot head in the center whose "ears" are two glowing blue lamps on
// stalks: the app that listens. Each size is rendered directly at its pixel
// size rather than downscaled from 1024; small sizes drop the fine details.

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
    case encode(Int)

    var description: String {
        switch self {
        case .bitmap(let size): return "could not create a \(size) px bitmap"
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

    drawRobot(unit: unit, detailed: pixels >= 64)

    context.flushGraphics()
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw IconError.encode(pixels)
    }
    return png
}


// MARK: - Robot

/// Colors of the robot. Shell is warm white, trim is graphite.
private let shellTop = NSColor(srgbRed: 1.00, green: 1.00, blue: 1.00, alpha: 1)
private let shellBottom = NSColor(srgbRed: 0.80, green: 0.83, blue: 0.87, alpha: 1)
private let trim = NSColor(srgbRed: 0.33, green: 0.35, blue: 0.39, alpha: 1)
private let trimDark = NSColor(srgbRed: 0.20, green: 0.21, blue: 0.24, alpha: 1)
private let glowCore = NSColor(srgbRed: 0.93, green: 0.98, blue: 1.00, alpha: 1)
private let glowEdge = NSColor(srgbRed: 0.35, green: 0.72, blue: 1.00, alpha: 1)

private func rect(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ u: CGFloat) -> NSRect {
    NSRect(x: x * u, y: y * u, width: w * u, height: h * u)
}

private func withShadow(_ color: NSColor, blur: CGFloat, dy: CGFloat, _ draw: () -> Void) {
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = color
    shadow.shadowBlurRadius = blur
    shadow.shadowOffset = NSSize(width: 0, height: dy)
    shadow.set()
    draw()
    NSGraphicsContext.restoreGraphicsState()
}

/// One ear: a graphite stalk from the side of the head up and outward, ending
/// in a glowing oval lamp tilted away from the head. `side` is -1 or 1.
private func drawEar(side: CGFloat, unit u: CGFloat, detailed: Bool) {
    let cx: CGFloat = 512
    let socket = NSPoint(x: (cx + side * 226) * u, y: 600 * u)
    let tip = NSPoint(x: (cx + side * 272) * u, y: 682 * u)

    // Socket where the stalk meets the head.
    trimDark.setFill()
    NSBezierPath(ovalIn: NSRect(x: socket.x - 34 * u, y: socket.y - 34 * u, width: 68 * u, height: 68 * u)).fill()

    let stalk = NSBezierPath()
    stalk.move(to: socket)
    stalk.curve(to: tip,
                controlPoint1: NSPoint(x: (cx + side * 262) * u, y: 612 * u),
                controlPoint2: NSPoint(x: (cx + side * 270) * u, y: 650 * u))
    stalk.lineWidth = 26 * u
    stalk.lineCapStyle = .round
    trim.setStroke()
    stalk.stroke()

    // Lamp: an ellipse rotated about its center, lit from inside.
    let lampCenter = NSPoint(x: (cx + side * 282) * u, y: 722 * u)
    let lamp = NSBezierPath(ovalIn: NSRect(x: -70 * u, y: -42 * u, width: 140 * u, height: 84 * u))
    var transform = AffineTransform(translationByX: lampCenter.x, byY: lampCenter.y)
    transform.rotate(byDegrees: side * -32)
    lamp.transform(using: transform)

    // Housing ring behind the glass.
    let housing = lamp.copy() as! NSBezierPath
    housing.lineWidth = 16 * u
    trim.setStroke()
    housing.stroke()

    withShadow(glowEdge.withAlphaComponent(0.95), blur: (detailed ? 50 : 16) * u, dy: 0) {
        glowEdge.setFill()
        lamp.fill()
    }
    if let gradient = NSGradient(colors: [glowCore, glowCore, glowEdge], atLocations: [0, 0.35, 1], colorSpace: .sRGB) {
        gradient.draw(in: lamp, relativeCenterPosition: NSPoint(x: 0, y: 0.1))
    }
}

/// One eye: pale sclera with a graphite iris looking forward, and an upper
/// lid in the shell color lowered to about 40 percent, which gives the calm,
/// attentive look of the reference robot.
private func drawEye(centerX: CGFloat, unit u: CGFloat, detailed: Bool) {
    let eyeRect = rect(centerX - 72, 432, 144, 124, u)
    let eye = NSBezierPath(ovalIn: eyeRect)

    // Recessed socket.
    withShadow(NSColor.black.withAlphaComponent(0.35), blur: 10 * u, dy: -4 * u) {
        NSColor(srgbRed: 0.93, green: 0.94, blue: 0.95, alpha: 1).setFill()
        eye.fill()
    }

    NSGraphicsContext.saveGraphicsState()
    eye.addClip()
    // Iris and pupil, slightly below center so the lid does not hide them.
    let iris = NSBezierPath(ovalIn: rect(centerX - 40, 450, 80, 80, u))
    if let irisGradient = NSGradient(starting: NSColor(srgbRed: 0.55, green: 0.58, blue: 0.62, alpha: 1), ending: trimDark) {
        irisGradient.draw(in: iris, relativeCenterPosition: NSPoint(x: 0, y: 0.3))
    }
    NSColor(srgbRed: 0.10, green: 0.10, blue: 0.12, alpha: 1).setFill()
    NSBezierPath(ovalIn: rect(centerX - 18, 472, 36, 36, u)).fill()
    if detailed {
        NSColor.white.withAlphaComponent(0.9).setFill()
        NSBezierPath(ovalIn: rect(centerX + 6, 494, 14, 14, u)).fill()
    }
    // Upper lid.
    let lidTop = eyeRect.maxY
    let lidBottom = eyeRect.minY + eyeRect.height * 0.60
    let lid = NSBezierPath(rect: NSRect(x: eyeRect.minX, y: lidBottom, width: eyeRect.width, height: lidTop - lidBottom))
    if let lidGradient = NSGradient(starting: NSColor(srgbRed: 0.86, green: 0.88, blue: 0.91, alpha: 1), ending: shellTop) {
        lidGradient.draw(in: lid, angle: 90)
    }
    let lidEdge = NSBezierPath()
    lidEdge.move(to: NSPoint(x: eyeRect.minX, y: lidBottom))
    lidEdge.line(to: NSPoint(x: eyeRect.maxX, y: lidBottom))
    lidEdge.lineWidth = 7 * u
    trim.setStroke()
    lidEdge.stroke()
    NSGraphicsContext.restoreGraphicsState()

    if detailed {
        eye.lineWidth = 5 * u
        NSColor(srgbRed: 0.62, green: 0.65, blue: 0.70, alpha: 1).setStroke()
        eye.stroke()
    }
}

func drawRobot(unit u: CGFloat, detailed: Bool) {
    // Neck.
    let neck = NSBezierPath(roundedRect: rect(452, 170, 120, 110, u), xRadius: 22 * u, yRadius: 22 * u)
    if let neckGradient = NSGradient(colors: [trimDark, trim, trimDark]) {
        neckGradient.draw(in: neck, angle: 0)
    }

    // Ears go behind the head.
    drawEar(side: -1, unit: u, detailed: detailed)
    drawEar(side: 1, unit: u, detailed: detailed)

    // Head: a tall rounded dome.
    let headRect = rect(272, 250, 480, 520, u)
    let head = NSBezierPath(roundedRect: headRect, xRadius: 240 * u, yRadius: 240 * u)
    withShadow(NSColor.black.withAlphaComponent(0.35), blur: 30 * u, dy: -14 * u) {
        shellBottom.setFill()
        head.fill()
    }
    if let shell = NSGradient(colors: [shellBottom, shellTop, shellTop], atLocations: [0, 0.55, 1], colorSpace: .sRGB) {
        shell.draw(in: head, angle: 90)
    }

    NSGraphicsContext.saveGraphicsState()
    head.addClip()
    // Graphite stripe down the crown.
    NSColor(srgbRed: 0.45, green: 0.47, blue: 0.51, alpha: 1).setFill()
    NSBezierPath(roundedRect: rect(488, 640, 48, 160, u), xRadius: 24 * u, yRadius: 24 * u).fill()
    if detailed {
        // Face-plate seam and a soft highlight on the dome.
        let seam = NSBezierPath()
        seam.move(to: NSPoint(x: 300 * u, y: 600 * u))
        seam.curve(to: NSPoint(x: 724 * u, y: 600 * u),
                   controlPoint1: NSPoint(x: 400 * u, y: 632 * u),
                   controlPoint2: NSPoint(x: 624 * u, y: 632 * u))
        seam.lineWidth = 4 * u
        NSColor(srgbRed: 0.72, green: 0.75, blue: 0.79, alpha: 1).setStroke()
        seam.stroke()
        NSColor.white.withAlphaComponent(0.55).setFill()
        NSBezierPath(ovalIn: rect(350, 660, 110, 60, u)).fill()
    }
    NSGraphicsContext.restoreGraphicsState()

    drawEye(centerX: 428, unit: u, detailed: detailed)
    drawEye(centerX: 596, unit: u, detailed: detailed)

    if detailed {
        // Small nose bump and a closed mouth line.
        NSColor(srgbRed: 0.78, green: 0.80, blue: 0.84, alpha: 1).setFill()
        NSBezierPath(ovalIn: rect(496, 382, 32, 26, u)).fill()
        let mouth = NSBezierPath()
        mouth.move(to: NSPoint(x: 476 * u, y: 336 * u))
        mouth.curve(to: NSPoint(x: 548 * u, y: 336 * u),
                    controlPoint1: NSPoint(x: 498 * u, y: 326 * u),
                    controlPoint2: NSPoint(x: 526 * u, y: 326 * u))
        mouth.lineWidth = 8 * u
        mouth.lineCapStyle = .round
        trim.setStroke()
        mouth.stroke()
    }
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
