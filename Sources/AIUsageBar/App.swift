import SwiftUI
import AppKit
import AIUsageCore

@main
struct AIUsageBarApp: App {
    @StateObject private var model: UsageModel

    init() {
        let m = UsageModel(providers: [ClaudeProvider(), CodexProvider()])
        m.startPolling()  // label .task 미발화 이슈 회피 — init에서 시작
        _model = StateObject(wrappedValue: m)
    }

    var body: some Scene {
        MenuBarExtra {
            VStack(alignment: .leading, spacing: 8) {
                if model.sections.isEmpty {
                    Text("불러오는 중…")
                }
                ForEach(model.sections) { section in
                    if model.sections.count > 1 {
                        Text(section.title)
                            .font(.caption)
                            .foregroundStyle(.secondary)
                    }
                    ForEach(Array(section.readings.enumerated()), id: \.offset) { _, r in
                        Text(detailLine(r)).foregroundStyle(.primary)
                    }
                    if let err = section.error {
                        Text("⚠︎ \(err)")
                    }
                    if section.id != model.sections.last?.id {
                        Divider()
                    }
                }
                Divider()
                HStack {
                    Button("지금 새로고침") { Task { await model.refresh() } }
                    Spacer()
                    Button("종료") { NSApplication.shared.terminate(nil) }
                        .keyboardShortcut("q")
                }
            }
            .padding(12)
            .frame(minWidth: 240, alignment: .leading)
        } label: {
            // MenuBarExtra 라벨은 상태바 버튼으로 평탄화되며 이미지 1개만 확실히
            // 살아남는다 — 아이콘+수치 전체를 BarImage로 합성해 이미지 하나만 넘긴다.
            Image(nsImage: BarImage.make(
                sections: model.sections,
                hasError: model.lastError != nil,
                loadingTitle: model.sections.isEmpty ? model.barTitle : nil))
        }
        .menuBarExtraStyle(.window)  // 메뉴 스타일은 비클릭 Text를 회색 처리 — 패널로 전환
    }

    private func detailLine(_ r: LimitReading) -> String {
        var line = "\(r.name): \(r.utilization)%"
        if let reset = r.resetsAt {
            let df = DateFormatter()
            df.dateFormat = "M/d"
            let mins = max(0, Int(reset.timeIntervalSinceNow / 60))
            let days = mins / 1440, hours = (mins % 1440) / 60
            var remain = days > 0 ? "\(days)일 \(hours)h" : "\(hours)h \(mins % 60)m"
            remain = "\(df.string(from: reset)), " + remain
            line += "  (리셋 \(remain))"
        }
        return line
    }
}
