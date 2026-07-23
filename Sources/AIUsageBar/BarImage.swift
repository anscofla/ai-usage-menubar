import AppKit
import AIUsageCore

/// 상태바 라벨 전체(섹션별 로고 아이콘 + 수치)를 단일 NSImage로 합성한다.
/// MenuBarExtra 라벨은 상태바 버튼으로 평탄화되며 이미지 1개만 확실히 살아남으므로,
/// 여러 아이콘·텍스트를 뷰로 나열하는 대신 통짜 이미지 하나를 넘긴다.
/// isTemplate = true 로 그려 메뉴바 라이트/다크에 자동 적응.
enum BarImage {
    // 100% = 18pt 폰트의 라인 높이(~21.5)가 들어가도록 21로 — 메뉴바 한계(~22) 안
    private static let height: CGFloat = 21
    private static let iconSide: CGFloat = 18
    private static let iconGap: CGFloat = 3      // 아이콘↔숫자
    private static let sectionGap: CGFloat = 9   // 섹션 사이

    static func make(sections: [UsageModel.Section], hasError: Bool, loadingTitle: String?) -> NSImage {
        let font = NSFont.monospacedDigitSystemFont(ofSize: 14, weight: .bold)
        // 자간은 살짝 좁게(-0.8), 숫자 사이 공백은 넓게(+3) — 덩어리는 촘촘, 구분은 또렷
        // .expansion은 로그 스케일 — log(0.96) ≈ -0.041 로 가로폭 96% 압축(세로는 그대로)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black,
                                                    .kern: -0.8, .expansion: -0.041]

        var parts: [(icon: NSImage, text: NSAttributedString)] = []
        if let loading = loadingTitle {
            parts.append((ClaudeIcon.shared, NSAttributedString(string: loading, attributes: attrs)))
        } else {
            for s in sections {
                let icon = s.id == "codex" ? CodexIcon.shared : ClaudeIcon.shared
                parts.append((icon, scaledNumbers(s.readings, attrs)))
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

    /// 숫자별로 자기 사용률에 비례한 크기: 0% = 10pt → 100% = 18pt, 공통 베이스라인.
    /// 낮을 땐 조용하고, 한도가 찰수록 커져서 시각적 경고가 된다.
    private static func scaledNumbers(_ readings: [LimitReading],
                                      _ attrs: [NSAttributedString.Key: Any]) -> NSAttributedString {
        guard !readings.isEmpty else { return NSAttributedString(string: "--", attributes: attrs) }
        let a = NSMutableAttributedString()
        for (i, r) in readings.enumerated() {
            if i > 0 {
                let sp = NSMutableAttributedString(string: " ", attributes: attrs)
                sp.addAttribute(.kern, value: 3.0, range: NSRange(location: 0, length: 1))
                a.append(sp)
            }
            let size = 10 + CGFloat(min(max(r.utilization, 0), 100)) / 100 * 8
            var numAttrs = attrs
            numAttrs[.font] = NSFont.monospacedDigitSystemFont(ofSize: size, weight: .bold)
            a.append(NSAttributedString(string: String(r.utilization), attributes: numAttrs))
        }
        // % 기호는 비볼드 11pt 고정, 앞 숫자와 간격 1.5
        if a.length > 0 {
            a.addAttribute(.kern, value: 1.5, range: NSRange(location: a.length - 1, length: 1))
        }
        var pctAttrs = attrs
        pctAttrs[.font] = NSFont.systemFont(ofSize: 11, weight: .regular)
        a.append(NSAttributedString(string: "%", attributes: pctAttrs))
        return a
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
