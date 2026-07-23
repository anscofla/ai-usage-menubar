import Foundation

public enum CodexProviderError: LocalizedError, Equatable {
    case notLoggedIn        // ~/.codex/auth.json 없음 — 섹션 자체를 숨긴다 (오류 아님)
    case tokenMissing
    case badResponse(Int)
    case badSchema

    public var errorDescription: String? {
        switch self {
        case .notLoggedIn: return "코덱스 미로그인"
        case .tokenMissing: return "토큰 없음 — codex login 확인"
        case .badResponse(let c):
            return c == 401 ? "인증 만료 — codex 한번 실행해 토큰 갱신" : "API 응답 오류 (\(c))"
        case .badSchema: return "응답 스키마 불일치"
        }
    }
}

public actor CodexProvider: UsageProvider {
    public nonisolated let id = "codex"

    public typealias Transport = @Sendable (URLRequest) async throws -> (Data, Int)

    public struct Auth: Equatable, Sendable {
        public let token: String
        public let accountId: String?
        public init(token: String, accountId: String?) {
            self.token = token
            self.accountId = accountId
        }
    }

    private let authLoader: @Sendable () throws -> Auth
    private let transport: Transport

    public init(authLoader: @escaping @Sendable () throws -> Auth = CodexProvider.readAuth,
                transport: @escaping Transport = ClaudeProvider.liveTransport) {
        self.authLoader = authLoader
        self.transport = transport
    }

    // MARK: - auth.json

    public static func readAuth() throws -> Auth {
        try readAuth(path: NSHomeDirectory() + "/.codex/auth.json")
    }

    public static func readAuth(path: String) throws -> Auth {
        guard FileManager.default.fileExists(atPath: path) else {
            throw CodexProviderError.notLoggedIn
        }
        guard let data = FileManager.default.contents(atPath: path),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tokens = json["tokens"] as? [String: Any],
              let token = tokens["access_token"] as? String, !token.isEmpty
        else { throw CodexProviderError.tokenMissing }
        return Auth(token: token, accountId: tokens["account_id"] as? String)
    }

    // MARK: - 파싱 (실측 스키마 2026-07-23: rate_limit 단수 객체 — codex-rs 소스의 배열형과 다름)

    private struct Window: Decodable {
        let used_percent: Double
        let limit_window_seconds: Int?
        let reset_at: Double?
    }
    private struct RateLimit: Decodable {
        let primary_window: Window?
        let secondary_window: Window?
    }
    private struct Payload: Decodable { let rate_limit: RateLimit? }

    static func windowName(_ seconds: Int?) -> String {
        switch seconds {
        case .some(604_800): return "주간한도"
        case .some(let s) where s > 0 && s % 3600 == 0: return "\(s / 3600)시간한도"
        case .some(let s) where s > 0: return "\(s / 60)분한도"
        default: return "한도"
        }
    }

    public static func parse(_ data: Data) throws -> [LimitReading] {
        guard let p = try? JSONDecoder().decode(Payload.self, from: data),
              let rl = p.rate_limit else {
            throw CodexProviderError.badSchema
        }
        var out: [LimitReading] = []
        for w in [rl.primary_window, rl.secondary_window].compactMap({ $0 }) {
            guard w.used_percent.isFinite, w.used_percent >= 0 else {
                throw CodexProviderError.badSchema
            }
            out.append(LimitReading(
                name: windowName(w.limit_window_seconds),
                utilization: Int(min(w.used_percent, 100).rounded()),
                resetsAt: w.reset_at.map { Date(timeIntervalSince1970: $0) }))
        }
        guard !out.isEmpty else { throw CodexProviderError.badSchema }
        return out
    }

    // MARK: - fetch

    private func request(_ auth: Auth) async throws -> (Data, Int) {
        var req = URLRequest(url: URL(string: "https://chatgpt.com/backend-api/wham/usage")!)
        req.setValue("Bearer \(auth.token)", forHTTPHeaderField: "Authorization")
        req.setValue("codex-cli", forHTTPHeaderField: "User-Agent")
        if let acc = auth.accountId {
            req.setValue(acc, forHTTPHeaderField: "ChatGPT-Account-Id")
        }
        return try await transport(req)
    }

    public func fetch() async throws -> [LimitReading] {
        // 매 폴링마다 auth.json 재독(메모리 캐시 없음): 계정 전환은 옛 토큰을 무효화하지
        // 않아 401이 안 뜨므로, 새로 읽어야만 새 계정이 반영된다.
        let auth = try authLoader()
        var (data, code) = try await request(auth)
        if code == 401 {  // 토큰 회전 — auth.json 재독 1회
            let fresh = try authLoader()
            (data, code) = try await request(fresh)
        }
        guard code == 200 else { throw CodexProviderError.badResponse(code) }
        return try Self.parse(data)
    }
}
