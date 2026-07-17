// Canonical Akapen icon sprite support (V1.2 共通アイコン化).
//
// Both shells draw the same platform-neutral line icons from
// assets/icons/akapen-ui-icons.svg. This is a direct port of the Windows
// shell's strict parser (Ui/SvgPathParser.cs): absolute-command subset
// (M / L / H / V / C / A / Z) only. SF Symbols are deliberately NOT used —
// their license limits them to Apple platforms, so a shared look must come
// from our own sprite.

import Foundation

public enum SvgSegmentKind {
    case move, line, cubic, arc, close
}

public struct SvgSegment {
    public let kind: SvgSegmentKind
    public let values: [Float]

    public init(kind: SvgSegmentKind, values: [Float]) {
        self.kind = kind
        self.values = values
    }
}

public enum SvgPathParser {
    public static func parse(_ pathData: String) throws -> [SvgSegment] {
        enum ParseError: Error { case relativeCommand, unsupported(Character), incomplete }
        let pattern = "[AaCcHhLlMmVvZz]|[-+]?(?:\\d*\\.\\d+|\\d+\\.?)(?:[eE][-+]?\\d+)?"
        let regex = try NSRegularExpression(pattern: pattern)
        let ns = pathData as NSString
        let tokens = regex.matches(in: pathData, range: NSRange(location: 0, length: ns.length))
            .map { ns.substring(with: $0.range) }
        var result: [SvgSegment] = []
        var index = 0
        var command: Character = "\0"

        func read(_ count: Int) throws -> [Float] {
            guard index + count <= tokens.count else { throw ParseError.incomplete }
            var values: [Float] = []
            for _ in 0..<count {
                guard let v = Float(tokens[index]) else { throw ParseError.incomplete }
                values.append(v)
                index += 1
            }
            return values
        }

        while index < tokens.count {
            if let first = tokens[index].first, first.isLetter {
                command = first
                index += 1
            }
            guard command != "\0", !command.isLowercase else { throw ParseError.relativeCommand }
            switch command {
            case "M":
                result.append(SvgSegment(kind: .move, values: try read(2)))
                command = "L"
            case "L":
                result.append(SvgSegment(kind: .line, values: try read(2)))
            case "H":
                result.append(SvgSegment(kind: .line, values: [try read(1)[0], .nan]))
            case "V":
                result.append(SvgSegment(kind: .line, values: [.nan, try read(1)[0]]))
            case "C":
                result.append(SvgSegment(kind: .cubic, values: try read(6)))
            case "A":
                result.append(SvgSegment(kind: .arc, values: try read(7)))
            case "Z":
                result.append(SvgSegment(kind: .close, values: []))
                command = "\0"
            default:
                throw ParseError.unsupported(command)
            }
        }
        return result
    }
}

/// Loads the canonical sprite (symbol id → path data), mirroring the Windows
/// SvgIconCatalog. The caller supplies the SVG text (from a bundle resource).
public enum SvgIconSprite {
    public static let viewBoxSize: Float = 24
    public static let strokeWidth: Float = 1.75

    public static func parseSprite(_ svgText: String) -> [String: [SvgSegment]] {
        // Minimal symbol/path extraction (the sprite is our own, fixed format:
        // one <symbol id="..."> containing one <path d="..."/> per icon).
        var icons: [String: [SvgSegment]] = [:]
        let symbolPattern = try! NSRegularExpression(
            pattern: "<symbol id=\"([^\"]+)\"[^>]*>.*?<path d=\"([^\"]+)\"",
            options: [.dotMatchesLineSeparators])
        let ns = svgText as NSString
        for match in symbolPattern.matches(in: svgText, range: NSRange(location: 0, length: ns.length)) {
            let id = ns.substring(with: match.range(at: 1))
            let d = ns.substring(with: match.range(at: 2))
            if let segments = try? SvgPathParser.parse(d) {
                icons[id] = segments
            }
        }
        return icons
    }
}
