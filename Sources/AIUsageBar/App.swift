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
            HStack(spacing: 3) {
                Image(nsImage: ClaudeIcon.shared)
                Text(model.barTitle)
            }
        }
        .menuBarExtraStyle(.window)  // 메뉴 스타일은 비클릭 Text를 회색 처리 — 패널로 전환
    }

    private func detailLine(_ r: LimitReading) -> String {
        var line = "\(r.name): \(r.utilization)%"
        if let reset = r.resetsAt {
            let mins = max(0, Int(reset.timeIntervalSinceNow / 60))
            line += "  (리셋까지 \(mins / 60)h \(mins % 60)m)"
        }
        return line
    }
}
