import Foundation
import AIUsageCore

private func fixture(_ limits: String) -> Data {
    Data(#"{"limits":[\#(limits)],"seven_day_opus":null}"#.utf8)
}

private let session = #"{"kind":"session","group":"session","percent":64,"resets_at":"2026-07-23T04:50:00.561460+00:00","scope":null}"#
private let weeklyAll = #"{"kind":"weekly_all","group":"weekly","percent":79,"resets_at":"2026-07-27T19:00:00Z","scope":null}"#
private let weeklyScoped = #"{"kind":"weekly_scoped","group":"weekly","percent":80.4,"resets_at":"2026-07-27T18:59:59.561764+00:00","scope":{"model":{"id":null,"display_name":"Fable"}}}"#

func expectThrows(_ expected: ClaudeProviderError, _ label: String, _ body: () throws -> Void) {
    do {
        try body()
        testFailures += 1
        print("FAIL  \(label) — 오류가 나야 하는데 성공함")
    } catch let e as ClaudeProviderError where e == expected {
        print("PASS  \(label)")
    } catch {
        testFailures += 1
        print("FAIL  \(label) — 기대와 다른 오류: \(error)")
    }
}

func runClaudeProviderTests() {
    // 정상 3종, 배열 순서 뒤섞음 + 미지 kind 무시
    let unknown = #"{"kind":"mystery","group":"x","percent":5,"resets_at":null,"scope":null}"#
    let ok = try! ClaudeProvider.parse(fixture([weeklyScoped, unknown, session, weeklyAll].joined(separator: ",")))
    expect(ok.map(\.name) == ["시간한도", "주별한도", "Fable한도"], "parse 이름·순서 고정(kind 탐색)")
    expect(ok.map(\.utilization) == [64, 79, 80], "parse percent 반올림")
    expect(ok[0].resetsAt != nil, "소수점초 ISO8601 파싱")
    expect(ok[1].resetsAt != nil, "일반 ISO8601 파싱")

    // display_name null → "모델한도" 폴백
    let noName = #"{"kind":"weekly_scoped","group":"weekly","percent":10,"resets_at":null,"scope":{"model":null}}"#
    let fallback = try! ClaudeProvider.parse(fixture([session, weeklyAll, noName].joined(separator: ",")))
    expect(fallback[2].name == "모델한도", "display_name 부재 폴백")

    // 오류 케이스
    expectThrows(.badSchema, "빈 limits") { _ = try ClaudeProvider.parse(fixture("")) }
    expectThrows(.badSchema, "필수 kind 누락") {
        _ = try ClaudeProvider.parse(fixture([session, weeklyAll].joined(separator: ",")))
    }
    expectThrows(.badSchema, "weekly_scoped 중복") {
        _ = try ClaudeProvider.parse(fixture([session, weeklyAll, weeklyScoped, weeklyScoped].joined(separator: ",")))
    }
    let negPercent = #"{"kind":"session","group":"session","percent":-3,"resets_at":null,"scope":null}"#
    expectThrows(.badSchema, "percent 음수") {
        _ = try ClaudeProvider.parse(fixture([negPercent, weeklyAll, weeklyScoped].joined(separator: ",")))
    }
    let overPercent = #"{"kind":"session","group":"session","percent":140,"resets_at":null,"scope":null}"#
    let clamped = try! ClaudeProvider.parse(fixture([overPercent, weeklyAll, weeklyScoped].joined(separator: ",")))
    expect(clamped[0].utilization == 100, "percent 100 초과 클램프")
    expectThrows(.badSchema, "limits 키 자체 부재") { _ = try ClaudeProvider.parse(Data("{}".utf8)) }
}

// ── fetch: 토큰 캐시·401 재독 (transport 주입) ──

func expectThrowsAsync(_ expected: ClaudeProviderError, _ label: String,
                       _ body: () async throws -> Void) async {
    do {
        try await body()
        testFailures += 1
        print("FAIL  \(label) — 오류가 나야 하는데 성공함")
    } catch let e as ClaudeProviderError where e == expected {
        print("PASS  \(label)")
    } catch {
        testFailures += 1
        print("FAIL  \(label) — 기대와 다른 오류: \(error)")
    }
}

final class Counter: @unchecked Sendable { var n = 0 }

private func stubTransport(_ codes: [Int], okBody: Data, calls: Counter) -> ClaudeProvider.Transport {
    { _ in
        let code = codes[min(calls.n, codes.count - 1)]
        calls.n += 1
        return (code == 200 ? okBody : Data(), code)
    }
}

func runClaudeProviderFetchTests() async {
    let okBody = fixture([session, weeklyAll, weeklyScoped].joined(separator: ","))

    // 연속 200: 매 폴링마다 재독 — 계정 전환이 다음 새로고침에 반영돼야 함
    let l1 = Counter(), t1 = Counter()
    let p1 = ClaudeProvider(tokenLoader: { l1.n += 1; return "tok\(l1.n)" },
                            transport: stubTransport([200, 200], okBody: okBody, calls: t1))
    _ = try? await p1.fetch()
    _ = try? await p1.fetch()
    expect(l1.n == 2, "폴링마다 재독 — 계정 전환 반영")
    expect(t1.n == 2, "폴링마다 재독 — 요청은 2회")

    // 401→200: 재독·재요청 각 1회
    let l2 = Counter(), t2 = Counter()
    let p2 = ClaudeProvider(tokenLoader: { l2.n += 1; return "tok\(l2.n)" },
                            transport: stubTransport([401, 200], okBody: okBody, calls: t2))
    let r2 = try? await p2.fetch()
    expect(r2?.count == 3 && l2.n == 2 && t2.n == 2, "401→200 재독 1회 후 성공")

    // 401→401: badResponse(401)
    let l3 = Counter(), t3 = Counter()
    let p3 = ClaudeProvider(tokenLoader: { l3.n += 1; return "tok" },
                            transport: stubTransport([401, 401], okBody: okBody, calls: t3))
    await expectThrowsAsync(.badResponse(401), "401→401 최종 실패") { _ = try await p3.fetch() }
    expect(t3.n == 2, "401→401 요청 정확히 2회")
}
