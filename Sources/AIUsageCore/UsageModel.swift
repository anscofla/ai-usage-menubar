import Foundation
import Combine

@MainActor
public final class UsageModel: ObservableObject {
    @Published public private(set) var readings: [LimitReading] = []
    @Published public private(set) var lastError: String?
    public var provider: UsageProvider
    private var pollTask: Task<Void, Never>?
    private var refreshTask: Task<Void, Never>?

    public init(provider: UsageProvider = ClaudeProvider()) {
        self.provider = provider
    }

    public var barTitle: String {
        let base = LimitReading.barSummary(readings)
        return lastError == nil ? base : base + " ⚠︎"
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
        do {
            readings = try await provider.fetch()
            lastError = nil
        } catch {
            lastError = error.localizedDescription  // 마지막 readings 유지
        }
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
