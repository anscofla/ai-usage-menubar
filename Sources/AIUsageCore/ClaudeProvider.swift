import Foundation

public enum ClaudeProviderError: LocalizedError, Equatable {
    case keychainFailed(String)
    case tokenMissing
    case badResponse(Int)
    case badSchema

    public var errorDescription: String? {
        switch self {
        case .keychainFailed(let detail):
            return detail.isEmpty ? "키체인 읽기 실패" : "키체인 읽기 실패 — \(detail)"
        case .tokenMissing: return "토큰 없음 — Claude Code 로그인 확인"
        case .badResponse(let c):
            return c == 401 ? "인증 만료 — Claude Code 한번 실행해 토큰 갱신" : "API 응답 오류 (\(c))"
        case .badSchema: return "응답 스키마 불일치"
        }
    }
}

public actor ClaudeProvider: UsageProvider {
    public nonisolated let id = "claude"

    public typealias Transport = @Sendable (URLRequest) async throws -> (Data, Int)

    private let tokenLoader: @Sendable () throws -> String
    private let transport: Transport

    public init(tokenLoader: @escaping @Sendable () throws -> String = ClaudeProvider.readAccessToken,
                transport: @escaping Transport = ClaudeProvider.liveTransport) {
        self.tokenLoader = tokenLoader
        self.transport = transport
    }

    public static let liveTransport: Transport = { req in
        let (data, resp) = try await URLSession.shared.data(for: req)
        return (data, (resp as? HTTPURLResponse)?.statusCode ?? -1)
    }

    // MARK: - 파싱

    private struct ModelRef: Decodable { let display_name: String? }
    private struct Scope: Decodable { let model: ModelRef? }
    private struct Limit: Decodable {
        let kind: String
        let percent: Double
        let resets_at: String?
        let scope: Scope?
    }
    private struct Payload: Decodable { let limits: [Limit] }

    static func parseDate(_ s: String?) -> Date? {
        guard let s else { return nil }
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f.date(from: s) { return d }
        return ISO8601DateFormatter().date(from: s)
    }

    public static func parse(_ data: Data) throws -> [LimitReading] {
        guard let p = try? JSONDecoder().decode(Payload.self, from: data) else {
            throw ClaudeProviderError.badSchema
        }
        func one(_ kind: String) throws -> Limit {
            let hits = p.limits.filter { $0.kind == kind }
            guard hits.count == 1 else { throw ClaudeProviderError.badSchema }
            return hits[0]
        }
        func reading(_ name: String, _ l: Limit) throws -> LimitReading {
            // 음수·비유한 = 스키마 오류; 100 초과는 오버런 표시 가능성 있어 100으로 클램프
            guard l.percent.isFinite, l.percent >= 0 else {
                throw ClaudeProviderError.badSchema
            }
            return LimitReading(name: name, utilization: Int(min(l.percent, 100).rounded()),
                                resetsAt: parseDate(l.resets_at))
        }
        let scoped = try one("weekly_scoped")
        let scopedName = (scoped.scope?.model?.display_name ?? "모델") + "한도"
        return [try reading("시간한도", try one("session")),
                try reading("주별한도", try one("weekly_all")),
                try reading(scopedName, scoped)]
    }

    // MARK: - 키체인

    public static func readAccessToken() throws -> String {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        proc.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
        let out = Pipe(), err = Pipe()
        proc.standardOutput = out
        proc.standardError = err
        do { try proc.run() } catch {
            throw ClaudeProviderError.keychainFailed("security 실행 불가")
        }
        proc.waitUntilExit()
        guard proc.terminationStatus == 0 else {
            let detail = String(data: err.fileHandleForReading.readDataToEndOfFile(),
                                encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
            throw ClaudeProviderError.keychainFailed(detail)
        }
        guard let json = try? JSONSerialization.jsonObject(
                  with: out.fileHandleForReading.readDataToEndOfFile()) as? [String: Any],
              let oauth = json["claudeAiOauth"] as? [String: Any],
              let token = oauth["accessToken"] as? String, !token.isEmpty
        else { throw ClaudeProviderError.tokenMissing }
        return token
    }

    // MARK: - fetch

    private func request(_ token: String) async throws -> (Data, Int) {
        var req = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        req.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        return try await transport(req)
    }

    public func fetch() async throws -> [LimitReading] {
        // 매 폴링마다 키체인 재독(메모리 캐시 없음): 계정 전환은 옛 토큰을 무효화하지
        // 않아 401이 안 뜨므로, 새로 읽어야만 새 계정이 반영된다.
        let token = try tokenLoader()
        var (data, code) = try await request(token)
        if code == 401 {  // 토큰 회전 — 키체인 재독 1회
            let fresh = try tokenLoader()
            (data, code) = try await request(fresh)
        }
        guard code == 200 else { throw ClaudeProviderError.badResponse(code) }
        return try Self.parse(data)
    }
}
