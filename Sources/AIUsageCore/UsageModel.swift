import Foundation
import Combine

@MainActor
public final class UsageModel: ObservableObject {
    public struct Section: Equatable, Identifiable {
        public let id: String
        public let title: String
        public let readings: [LimitReading]
        public let error: String?
    }

    @Published public private(set) var sections: [Section] = []
    public let providers: [UsageProvider]
    private var lastGood: [String: [LimitReading]] = [:]
    private var pollTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?

    public init(provider: UsageProvider = ClaudeProvider()) {
        self.providers = [provider]
    }

    public init(providers: [UsageProvider]) {
        self.providers = providers
    }

    /// 기존 단일 프로바이더 호환 표면 (첫 섹션 기준)
    public var readings: [LimitReading] { sections.first?.readings ?? [] }

    public var lastError: String? {
        let errs = sections.compactMap { s in
            s.error.map { sections.count > 1 ? "\(s.title): \($0)" : $0 }
        }
        return errs.isEmpty ? nil : errs.joined(separator: " · ")
    }

    public var barTitle: String {
        let parts = sections.map { LimitReading.barSummary($0.readings) }
        let base = parts.isEmpty ? "--" : parts.joined(separator: " ⌁ ")
        return lastError == nil ? base : base + " ⚠︎"
    }

    static func title(for id: String) -> String {
        switch id {
        case "claude": return "Claude"
        case "codex": return "Codex"
        default: return id
        }
    }

    /// 공유 단일비행: 동시 호출은 진행 중인 같은 refresh를 함께 기다린다
    /// (폴링 stop→restart 시 새 루프가 60초 공전하는 경쟁도 이걸로 해소)
    public func refresh() async {
        if let t = refreshTask { await t.value; return }
        let t = Task { await self.performRefresh() }
        refreshTask = t
        await t.value
        refreshTask = nil
    }

    private func performRefresh() async {
        var next: [Section] = []
        for p in providers {
            do {
                let r = try await p.fetch()
                lastGood[p.id] = r
                next.append(Section(id: p.id, title: Self.title(for: p.id), readings: r, error: nil))
            } catch CodexProviderError.notLoggedIn {
                lastGood[p.id] = nil  // 미로그인 = 섹션 숨김 (오류 아님)
            } catch {
                next.append(Section(id: p.id, title: Self.title(for: p.id),
                                    readings: lastGood[p.id] ?? [],  // 마지막 값 유지
                                    error: error.localizedDescription))
            }
        }
        sections = next
    }

    public func startPolling(interval: TimeInterval = 60) {
        pollTask?.cancel()
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                await self?.refresh()  // refresh 구간에만 self 승격 — sleep 중 보유 안 함
                do {
                    try await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                } catch { break }  // 취소 즉시 종료
                if self == nil { break }
            }
        }
    }

    public func stopPolling() {
        pollTask?.cancel()
        pollTask = nil
    }
}
