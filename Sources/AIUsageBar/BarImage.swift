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
    private static let numberGap: CGFloat = 7    // 숫자 사이(기존 공백+kern3 상당)
    private static let pctGap: CGFloat = 1.5     // 숫자↔% 기호

    /// 독립 배치되는 텍스트 조각 — 각자 자기 캡하이트 중심을 height/2 에 고정해 그린다
    private struct Run {
        let text: NSAttributedString
        let font: NSFont
        let leadingGap: CGFloat
        var width: CGFloat { ceil(text.size().width) }
    }

    static func make(sections: [UsageModel.Section], hasError: Bool, loadingTitle: String?) -> NSImage {
        let baseFont = NSFont.monospacedDigitSystemFont(ofSize: 14, weight: .bold)
        // 자간은 살짝 좁게(-0.8), .expansion은 로그 스케일 — log(0.96) ≈ -0.041 로 가로폭 96% 압축
        func attrs(_ font: NSFont) -> [NSAttributedString.Key: Any] {
            [.font: font, .foregroundColor: NSColor.black, .kern: -0.8, .expansion: -0.041]
        }

        var parts: [(icon: NSImage, runs: [Run])] = []
        if let loading = loadingTitle {
            parts.append((ClaudeIcon.shared,
                          [Run(text: NSAttributedString(string: loading, attributes: attrs(baseFont)),
                               font: baseFont, leadingGap: 0)]))
        } else {
            for s in sections {
                let icon = s.id == "codex" ? CodexIcon.shared : ClaudeIcon.shared
                parts.append((icon, scaledRuns(s.readings, attrs: attrs)))
            }
        }
        if hasError {
            parts.append((warnIcon, []))
        }

        var width: CGFloat = 0
        for (i, p) in parts.enumerated() {
            if i > 0 { width += sectionGap }
            width += iconSide
            let tw = p.runs.reduce(CGFloat(0)) { $0 + $1.leadingGap + $1.width }
            if tw > 0 { width += iconGap + tw }
        }
        width = max(width, iconSide)

        let img = NSImage(size: NSSize(width: width, height: height), flipped: false) { _ in
            var x: CGFloat = 0
            for (i, p) in parts.enumerated() {
                if i > 0 { x += sectionGap }
                p.icon.draw(in: NSRect(x: x, y: (height - iconSide) / 2, width: iconSide, height: iconSide))
                x += iconSide
                if !p.runs.isEmpty { x += iconGap }
                for run in p.runs {
                    x += run.leadingGap
                    // 메트릭 추정은 좌표계 함정이 많다 — 스크래치 비트맵에 실제로 그려보고
                    // 잉크 중심을 스캔해, 그 중심이 정확히 height/2 에 오도록 보정해 그린다
                    run.text.draw(at: NSPoint(x: x, y: height / 2 - inkCenterY(run.text) - 0.6))
                    x += run.width
                }
            }
            return true
        }
        img.isTemplate = true
        return img
    }

    /// 숫자별로 자기 사용률에 비례한 크기: 0% = 10pt → 100% = 18pt.
    /// 낮을 땐 조용하고, 한도가 찰수록 커져서 시각적 경고가 된다.
    private static func scaledRuns(_ readings: [LimitReading],
                                   attrs: (NSFont) -> [NSAttributedString.Key: Any]) -> [Run] {
        let base = NSFont.monospacedDigitSystemFont(ofSize: 14, weight: .bold)
        guard !readings.isEmpty else {
            return [Run(text: NSAttributedString(string: "--", attributes: attrs(base)),
                        font: base, leadingGap: 0)]
        }
        var runs: [Run] = []
        for (i, r) in readings.enumerated() {
            let size = 10 + CGFloat(min(max(r.utilization, 0), 100)) / 100 * 8
            let font = NSFont.monospacedDigitSystemFont(ofSize: size, weight: .bold)
            runs.append(Run(text: NSAttributedString(string: String(r.utilization), attributes: attrs(font)),
                            font: font, leadingGap: i > 0 ? numberGap : 0))
        }
        // % 기호는 비볼드 11pt 고정
        let pctFont = NSFont.systemFont(ofSize: 11, weight: .regular)
        runs.append(Run(text: NSAttributedString(string: "%", attributes: attrs(pctFont)),
                        font: pctFont, leadingGap: pctGap))
        return runs
    }

    /// 스크래치 비트맵에 y=0 기준으로 그려보고 알파가 찍힌 행을 스캔해,
    /// draw(at: y=0) 좌표계에서의 잉크 세로 중심을 돌려준다 (캐시됨).
    private static var inkCenterCache: [String: CGFloat] = [:]
    private static func inkCenterY(_ text: NSAttributedString) -> CGFloat {
        let key = text.string + "|" + String(describing: text.attribute(.font, at: 0, effectiveRange: nil))
        if let hit = inkCenterCache[key] { return hit }
        let pad: CGFloat = 12   // 디센더 음수영역 여유
        let w = max(Int(ceil(text.size().width)) + 4, 1)
        let h = Int(ceil(text.size().height)) + Int(pad * 2)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true,
                                         isPlanar: false, colorSpaceName: .deviceRGB,
                                         bytesPerRow: 0, bitsPerPixel: 0)
        else { return text.size().height / 2 }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        text.draw(at: NSPoint(x: 2, y: pad))
        NSGraphicsContext.restoreGraphicsState()
        var minRow = h, maxRow = -1
        for row in 0..<h {
            for col in 0..<w where rep.colorAt(x: col, y: row)?.alphaComponent ?? 0 > 0.1 {
                minRow = min(minRow, row); maxRow = max(maxRow, row); break
            }
        }
        guard maxRow >= 0 else { return text.size().height / 2 }
        // rep 의 row는 위에서 아래 — draw(at:) y-up 좌표로 변환 후 pad 제거
        let topY = CGFloat(h - 1 - minRow), botY = CGFloat(h - 1 - maxRow)
        let center = (topY + botY) / 2 - pad
        inkCenterCache[key] = center
        return center
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
