import Foundation
import AIUsageCore

final class StubProvider: UsageProvider, @unchecked Sendable {
    let id = "stub"
    let calls = Counter()
    var result: Result<[LimitReading], Error>
    var delayNs: UInt64 = 0

    init(_ result: Result<[LimitReading], Error>) { self.result = result }

    func fetch() async throws -> [LimitReading] {
        calls.n += 1
        if delayNs > 0 { try? await Task.sleep(nanoseconds: delayNs) }
        return try result.get()
    }
}

@MainActor
func runUsageModelTests() async {
    let one = [LimitReading(name: "시간한도", utilization: 64, resetsAt: nil)]

    // 성공: readings 반영, 오류 없음
    let m1 = UsageModel(provider: StubProvider(.success(one)))
    await m1.refresh()
    expect(m1.barTitle == "64%", "refresh 성공 → barTitle")
    expect(m1.lastError == nil, "refresh 성공 → 오류 없음")

    // 실패: 마지막 값 유지 + ⚠︎
    let s2 = StubProvider(.success(one))
    let m2 = UsageModel(provider: s2)
    await m2.refresh()
    s2.result = .failure(ClaudeProviderError.tokenMissing)
    await m2.refresh()
    expect(m2.readings == one, "실패 시 마지막 값 유지")
    expect(m2.lastError != nil && m2.barTitle.hasSuffix("⚠︎"), "실패 시 ⚠︎ 표시")

    // 동시 refresh → fetch 1회 (actor 기각 담보 테스트)
    let s3 = StubProvider(.success(one))
    s3.delayNs = 50_000_000
    let m3 = UsageModel(provider: s3)
    async let a: Void = m3.refresh()
    async let b: Void = m3.refresh()
    _ = await (a, b)
    expect(s3.calls.n == 1, "동시 refresh 단일비행")

    // fetch 진행 중 stop→restart: 새 루프가 60초 공전하지 않고 곧 fetch 재개 (Codex P2 회귀)
    let s5 = StubProvider(.success(one))
    s5.delayNs = 50_000_000
    let m5 = UsageModel(provider: s5)
    m5.startPolling(interval: 0.05)
    try? await Task.sleep(nanoseconds: 10_000_000)  // 첫 fetch 진행 중
    m5.stopPolling()
    m5.startPolling(interval: 0.05)
    try? await Task.sleep(nanoseconds: 300_000_000)
    expect(s5.calls.n >= 2, "stop→restart 중 재개 (공전 없음)")
    m5.stopPolling()

    // 폴링 수명주기: 시작(중복 포함)→정지 후 추가 fetch 없음
    let s4 = StubProvider(.success(one))
    let m4 = UsageModel(provider: s4)
    m4.startPolling(interval: 0.05)
    m4.startPolling(interval: 0.05)  // 멱등 — 기존 루프 취소
    try? await Task.sleep(nanoseconds: 120_000_000)
    expect(s4.calls.n >= 1, "폴링 시작 후 fetch 발생")
    m4.stopPolling()
    try? await Task.sleep(nanoseconds: 30_000_000)  // 진행 중 refresh 소진 대기
    let frozen = s4.calls.n
    try? await Task.sleep(nanoseconds: 150_000_000)
    expect(s4.calls.n == frozen, "stopPolling 후 fetch 없음")
}
