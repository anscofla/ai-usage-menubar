import Foundation
import AIUsageCore

// 실측 응답 형태 (2026-07-23 probe_codex.sh — prolite: primary=주간 창 하나, secondary=null)
private let goodJSON = """
{"plan_type":"prolite",
 "rate_limit":{"allowed":true,"limit_reached":false,
   "primary_window":{"used_percent":62,"limit_window_seconds":604800,
                     "reset_after_seconds":493107,"reset_at":1785284316},
   "secondary_window":null},
 "credits":{"has_credits":false},
 "rate_limit_reset_credits":{"available_count":3}}
""".data(using: .utf8)!

private let twoWindowJSON = """
{"rate_limit":{
   "primary_window":{"used_percent":30.4,"limit_window_seconds":18000,"reset_at":1785284316},
   "secondary_window":{"used_percent":150,"limit_window_seconds":604800,"reset_at":1785284316}}}
""".data(using: .utf8)!

func runCodexProviderTests() {
    // 정상: 주간 창 1개
    if let r = try? CodexProvider.parse(goodJSON) {
        expect(r.count == 1, "codex parse: prolite = 1개 창")
        expect(r[0].utilization == 62, "codex parse: used_percent")
        expect(r[0].name == "주간한도", "codex parse: 604800s → 주간한도")
        expect(r[0].resetsAt == Date(timeIntervalSince1970: 1_785_284_316), "codex parse: reset_at unix초")
    } else { expect(false, "codex parse: 정상 응답 파싱") }

    // 2개 창(5시간+주간) + 클램프 + 소수 반올림
    if let r = try? CodexProvider.parse(twoWindowJSON) {
        expect(r.count == 2, "codex parse: 창 2개")
        expect(r[0].name == "5시간한도" && r[0].utilization == 30, "codex parse: 18000s → 5시간한도·반올림")
        expect(r[1].utilization == 100, "codex parse: >100 클램프")
    } else { expect(false, "codex parse: 2창 응답 파싱") }

    // 스키마 오류들
    expect((try? CodexProvider.parse("{}".data(using: .utf8)!)) == nil, "codex parse: rate_limit 없음 = 오류")
    expect((try? CodexProvider.parse("""
        {"rate_limit":{"primary_window":null,"secondary_window":null}}
        """.data(using: .utf8)!)) == nil, "codex parse: 창 0개 = 오류")
    expect((try? CodexProvider.parse("""
        {"rate_limit":{"primary_window":{"used_percent":-1,"limit_window_seconds":604800}}}
        """.data(using: .utf8)!)) == nil, "codex parse: 음수 percent = 오류")

    // auth.json 로더
    expect((try? CodexProvider.readAuth(path: "/nonexistent/auth.json")) == nil, "codex auth: 파일 없음 throw")
    do { _ = try CodexProvider.readAuth(path: "/nonexistent/auth.json") }
    catch { expect(error as? CodexProviderError == .notLoggedIn, "codex auth: 파일 없음 = notLoggedIn") }

    let tmp = NSTemporaryDirectory() + "codex-auth-test-\(ProcessInfo.processInfo.processIdentifier).json"
    try? """
    {"tokens":{"access_token":"synthetic-token","account_id":"acc-1"}}
    """.write(toFile: tmp, atomically: true, encoding: .utf8)
    if let a = try? CodexProvider.readAuth(path: tmp) {
        expect(a.token == "synthetic-token" && a.accountId == "acc-1", "codex auth: 토큰+계정 파싱")
    } else { expect(false, "codex auth: 정상 파일 파싱") }
    try? """
    {"tokens":{"access_token":""}}
    """.write(toFile: tmp, atomically: true, encoding: .utf8)
    do { _ = try CodexProvider.readAuth(path: tmp); expect(false, "codex auth: 빈 토큰 throw") }
    catch { expect(error as? CodexProviderError == .tokenMissing, "codex auth: 빈 토큰 = tokenMissing") }
    try? FileManager.default.removeItem(atPath: tmp)
}

func runCodexProviderFetchTests() async {
    let auth = CodexProvider.Auth(token: "synthetic-token", accountId: "acc-1")

    // 200 정상 + 헤더 확인
    let seen = Counter()
    let p1 = CodexProvider(authLoader: { auth }, transport: { req in
        seen.n += 1
        expect(req.value(forHTTPHeaderField: "Authorization") == "Bearer synthetic-token", "codex fetch: bearer 헤더")
        expect(req.value(forHTTPHeaderField: "ChatGPT-Account-Id") == "acc-1", "codex fetch: 계정 헤더")
        return (goodJSON, 200)
    })
    if let r = try? await p1.fetch() {
        expect(r.count == 1 && seen.n == 1, "codex fetch: 200 성공 1회 호출")
    } else { expect(false, "codex fetch: 200 성공") }

    // 401 → auth 재독 1회 재시도
    let calls = Counter()
    let p2 = CodexProvider(authLoader: { auth }, transport: { _ in
        calls.n += 1
        return calls.n == 1 ? (Data(), 401) : (goodJSON, 200)
    })
    if let r = try? await p2.fetch() {
        expect(r.count == 1 && calls.n == 2, "codex fetch: 401 재독+재시도 성공")
    } else { expect(false, "codex fetch: 401 재시도") }

    // 연속 401 = badResponse(401)
    let p3 = CodexProvider(authLoader: { auth }, transport: { _ in (Data(), 401) })
    do { _ = try await p3.fetch(); expect(false, "codex fetch: 연속 401 throw") }
    catch { expect(error as? CodexProviderError == .badResponse(401), "codex fetch: 연속 401 = badResponse") }
}
