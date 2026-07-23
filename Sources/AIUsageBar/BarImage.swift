import AppKit
import AIUsageCore

/// 상태바 라벨 전체(섹션별 로고 아이콘 + 수치)를 단일 NSImage로 합성한다.
/// MenuBarExtra 라벨은 상태바 버튼으로 평탄화되며 이미지 1개만 확실히 살아남으므로,
/// 여러 아이콘·텍스트를 뷰로 나열하는 대신 통짜 이미지 하나를 넘긴다.
/// isTemplate = true 로 그려 메뉴바 라이트/다크에 자동 적응.
///
/// 수치는 iOS 배터리 잔량 스타일: 수치 하나 = 작은 배터리 픽토그램,
/// 채움 폭 = 사용률, 숫자는 채움 위에서는 뚫리고(knockout) 빈 곳에서는 그대로 보인다.
enum BarImage {
    private static let height: CGFloat = 22  // 메뉴바 시스템 두께(22pt)까지 사용
    private static let iconSide: CGFloat = 18
    private static let iconGap: CGFloat = 4       // 아이콘↔첫 배터리
    private static let sectionGap: CGFloat = 9    // 섹션 사이
    private static let cellGap: CGFloat = 4       // 배터리 사이
    private static let bodyW: CGFloat = 36        // 배터리 본체 폭
    private static let bodyH: CGFloat = 20        // 배터리 본체 높이(메뉴바 한계상 최대)
    private static let capW: CGFloat = 3.5        // 오른쪽 단자 돌기
    private static let stroke: CGFloat = 1.5

    static func make(sections: [UsageModel.Section], hasError: Bool, loadingTitle: String?) -> NSImage {
        let loadingAttrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.monospacedDigitSystemFont(ofSize: 14, weight: .bold),
            .foregroundColor: NSColor.black]

        var parts: [(icon: NSImage?, readings: [LimitReading], loading: NSAttributedString?)] = []
        if let loading = loadingTitle {
            parts.append((ClaudeIcon.shared, [], NSAttributedString(string: loading, attributes: loadingAttrs)))
        } else {
            for s in sections {
                parts.append((s.id == "codex" ? CodexIcon.shared : ClaudeIcon.shared, s.readings, nil))
            }
        }
        if hasError { parts.append((warnIcon, [], nil)) }

        let cellW = bodyW + capW
        var width: CGFloat = 0
        for (i, p) in parts.enumerated() {
            if i > 0 { width += sectionGap }
            width += iconSide
            if let t = p.loading, t.size().width > 0 { width += iconGap + ceil(t.size().width) }
            if !p.readings.isEmpty {
                width += iconGap + CGFloat(p.readings.count) * cellW
                    + CGFloat(p.readings.count - 1) * cellGap
            }
        }
        width = max(width, iconSide)

        let img = NSImage(size: NSSize(width: width, height: height), flipped: false) { _ in
            var x: CGFloat = 0
            for (i, p) in parts.enumerated() {
                if i > 0 { x += sectionGap }
                p.icon?.draw(in: NSRect(x: x, y: (height - iconSide) / 2, width: iconSide, height: iconSide))
                x += iconSide
                if let t = p.loading, t.size().width > 0 {
                    x += iconGap
                    t.draw(at: NSPoint(x: x, y: (height - t.size().height) / 2))
                    x += ceil(t.size().width)
                }
                if !p.readings.isEmpty {
                    x += iconGap
                    for (j, r) in p.readings.enumerated() {
                        if j > 0 { x += cellGap }
                        drawBattery(r.utilization, at: x)
                        x += cellW
                    }
                }
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    /// 배터리 픽토그램 하나: 외곽선 + 사용률만큼 채움 + 가운데 숫자.
    /// 숫자는 XOR 블렌드 — 채움과 겹치는 부분은 뚫리고, 빈 부분엔 그대로 찍힌다.
    private static func drawBattery(_ utilization: Int, at x: CGFloat) {
        guard let cg = NSGraphicsContext.current?.cgContext else { return }
        let y = (height - bodyH) / 2
        let body = NSRect(x: x, y: y, width: bodyW, height: bodyH)

        // 외곽선
        let outline = NSBezierPath(roundedRect: body.insetBy(dx: stroke / 2, dy: stroke / 2),
                                   xRadius: 6, yRadius: 6)
        outline.lineWidth = stroke
        NSColor.black.setStroke()
        outline.stroke()

        // 오른쪽 단자 돌기
        let cap = NSBezierPath(roundedRect: NSRect(x: x + bodyW + 0.5, y: y + bodyH / 2 - 4,
                                                   width: capW - 0.5, height: 8),
                               xRadius: 1.5, yRadius: 1.5)
        NSColor.black.setFill()
        cap.fill()

        // 채움 (사용률 비례, 내부 인셋)
        let inset = stroke + 1.5
        let innerW = bodyW - inset * 2
        let u = CGFloat(min(max(utilization, 0), 100)) / 100
        if u > 0 {
            let fill = NSBezierPath(
                roundedRect: NSRect(x: x + inset, y: y + inset,
                                    width: max(innerW * u, 2), height: bodyH - inset * 2),
                xRadius: 4, yRadius: 4)
            fill.fill()
        }

        // 숫자 — XOR로 채움에 구멍. 크기는 사용률 비례(0%=8pt → 100%=13pt)로 경고감 표현
        let fontSize = 8 + u * 5
        let text = NSAttributedString(
            string: String(min(max(utilization, 0), 100)),
            attributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: fontSize, weight: .bold),
                         .foregroundColor: NSColor.black, .kern: -0.5])
        let ts = text.size()
        cg.saveGState()
        cg.setBlendMode(.xor)
        text.draw(at: NSPoint(x: x + (bodyW - ts.width) / 2, y: y + (bodyH - ts.height) / 2))
        cg.restoreGState()
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
