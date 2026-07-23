import AppKit

enum ClaudeIcon {
    /// 클로드풍 8방사 별표 — 단색 템플릿(다크/라이트 자동), static 캐시
    static let shared: NSImage = make()

    private static func make(size: CGFloat = 16) -> NSImage {
        let img = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
            let c = NSPoint(x: rect.midX, y: rect.midY)
            let path = NSBezierPath()
            path.lineWidth = size * 0.14
            path.lineCapStyle = .round
            let rOut = size * 0.44, rIn = size * 0.10
            for i in 0..<8 {
                let a = CGFloat(i) * .pi / 4
                path.move(to: NSPoint(x: c.x + rIn * cos(a), y: c.y + rIn * sin(a)))
                path.line(to: NSPoint(x: c.x + rOut * cos(a), y: c.y + rOut * sin(a)))
            }
            NSColor.black.setStroke()
            path.stroke()
            return true
        }
        img.isTemplate = true
        return img
    }
}
