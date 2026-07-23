import AppKit

enum CodexIcon {
    /// 코덱스 로고풍 — 둥근 사각형 안 터미널 프롬프트(>_), SF Symbols terminal.fill 재사용.
    /// 심볼 미존재(구 macOS) 시 ClaudeIcon과 같은 방식의 수제 드로잉 폴백.
    static let shared: NSImage = BrandLogo.openai ?? make()

    private static func make(size: CGFloat = 16) -> NSImage {
        if let sym = NSImage(systemSymbolName: "terminal.fill", accessibilityDescription: "Codex") {
            let cfg = NSImage.SymbolConfiguration(pointSize: size * 0.82, weight: .regular)
            if let img = sym.withSymbolConfiguration(cfg) {
                img.isTemplate = true
                return img
            }
        }
        let img = NSImage(size: NSSize(width: size, height: size), flipped: false) { rect in
            let box = NSBezierPath(roundedRect: rect.insetBy(dx: size * 0.06, dy: size * 0.12),
                                   xRadius: size * 0.22, yRadius: size * 0.22)
            box.lineWidth = size * 0.10
            NSColor.black.setStroke()
            box.stroke()
            let p = NSBezierPath()
            p.lineWidth = size * 0.12
            p.lineCapStyle = .round
            p.move(to: NSPoint(x: size * 0.28, y: size * 0.62))
            p.line(to: NSPoint(x: size * 0.44, y: size * 0.48))
            p.line(to: NSPoint(x: size * 0.28, y: size * 0.34))
            p.move(to: NSPoint(x: size * 0.52, y: size * 0.32))
            p.line(to: NSPoint(x: size * 0.72, y: size * 0.32))
            p.stroke()
            return true
        }
        img.isTemplate = true
        return img
    }
}
