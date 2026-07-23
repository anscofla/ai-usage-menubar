import AppKit
import AIUsageCore

/// 상태바 라벨 전체(섹션별 로고 아이콘 + 수치)를 단일 NSImage로 합성한다.
/// MenuBarExtra 라벨은 상태바 버튼으로 평탄화되며 이미지 1개만 확실히 살아남으므로,
/// 여러 아이콘·텍스트를 뷰로 나열하는 대신 통짜 이미지 하나를 넘긴다.
/// isTemplate = true 로 그려 메뉴바 라이트/다크에 자동 적응.
enum BarImage {
    private static let height: CGFloat = 18
    private static let iconSide: CGFloat = 18
    private static let iconGap: CGFloat = 3      // 아이콘↔숫자
    private static let sectionGap: CGFloat = 9   // 섹션 사이

    static func make(sections: [UsageModel.Section], hasError: Bool, loadingTitle: String?) -> NSImage {
        let font = NSFont.monospacedDigitSystemFont(ofSize: 14, weight: .medium)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black]

        var parts: [(icon: NSImage, text: NSAttributedString)] = []
        if let loading = loadingTitle {
            parts.append((ClaudeIcon.shared, NSAttributedString(string: loading, attributes: attrs)))
        } else {
            for s in sections {
                let icon = s.id == "codex" ? CodexIcon.shared : ClaudeIcon.shared
                var text = LimitReading.barSummary(s.readings)
                if text.isEmpty { text = "--" }
                parts.append((icon, NSAttributedString(string: text, attributes: attrs)))
            }
        }
        if hasError {
            parts.append((warnIcon, NSAttributedString(string: "", attributes: attrs)))
        }

        var width: CGFloat = 0
        for (i, p) in parts.enumerated() {
            if i > 0 { width += sectionGap }
            width += iconSide
            let tw = ceil(p.text.size().width)
            if tw > 0 { width += iconGap + tw }
        }
        width = max(width, iconSide)

        let img = NSImage(size: NSSize(width: width, height: height), flipped: false) { _ in
            var x: CGFloat = 0
            for (i, p) in parts.enumerated() {
                if i > 0 { x += sectionGap }
                let iconY = (height - iconSide) / 2
                p.icon.draw(in: NSRect(x: x, y: iconY, width: iconSide, height: iconSide))
                x += iconSide
                let ts = p.text.size()
                if ts.width > 0 {
                    x += iconGap
                    p.text.draw(at: NSPoint(x: x, y: (height - ts.height) / 2))
                    x += ceil(ts.width)
                }
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    private static let warnIcon: NSImage = {
        if let sym = NSImage(systemSymbolName: "exclamationmark.triangle.fill",
                             accessibilityDescription: "오류"),
           let img = sym.withSymbolConfiguration(.init(pointSize: 11, weight: .semibold)) {
            img.isTemplate = true
            return img
        }
        return NSImage(size: NSSize(width: iconSide, height: iconSide))
    }()
}
