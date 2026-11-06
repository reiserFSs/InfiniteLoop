// swift probe.swift MOVIE.mp4 [GRAB.png SECONDS] — prints duration/size/fps/codec; optionally saves a decoded frame.
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

let args = CommandLine.arguments
let asset = AVURLAsset(url: URL(fileURLWithPath: args[1]))
let track = asset.tracks(withMediaType: .video)[0]
let fmt = track.formatDescriptions[0] as! CMFormatDescription
let fourcc = CMFormatDescriptionGetMediaSubType(fmt)
let code = String(bytes: [24, 16, 8, 0].map { UInt8((fourcc >> $0) & 0xff) }, encoding: .ascii)!
let size = track.naturalSize.applying(track.preferredTransform)
print("duration=\(asset.duration.seconds) width=\(Int(abs(size.width))) height=\(Int(abs(size.height))) fps=\(track.nominalFrameRate) codec=\(code) frames=\(Int((asset.duration.seconds * Double(track.nominalFrameRate)).rounded())) transform_identity=\(track.preferredTransform.isIdentity)")
if args.count > 3 {
    let gen = AVAssetImageGenerator(asset: asset)
    gen.requestedTimeToleranceBefore = .zero
    gen.requestedTimeToleranceAfter = .zero
    let image = try gen.copyCGImage(at: CMTime(seconds: Double(args[3])!, preferredTimescale: 600), actualTime: nil)
    let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: args[2]) as CFURL, UTType.png.identifier as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    CGImageDestinationFinalize(dest)
    print("grabbed \(args[2])")
}
