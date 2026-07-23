# AI_Usage_Menubar v1 Implementation Plan (v2 — T0 실측 + Codex/감시자 리뷰 반영)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 맥 상단바에 클로드 사용률 3종(세션/주간/모델-scoped)을 `(로고) 64 79 80%`로 상시 표시하는 네이티브 메뉴바 앱.

**Architecture:** SwiftPM — `AIUsageCore` 라이브러리(모델·프로바이더·뷰모델, 테스트 대상) + `AIUsageBar` executable(@main·아이콘만). 키체인의 Claude Code OAuth 토큰을 메모리 캐시, 60초 async 폴링.

**Tech Stack:** Swift 5.9+, SwiftUI MenuBarExtra (macOS 13+), XCTest, `security` CLI, ad-hoc codesign.

## Global Constraints

- 위치: `AI_Usage_Menubar/`; Xcode GUI 불사용
- 값 = 사용률 % 그대로 (T0 실측: 0~100 스케일 확인)
- 실패 시 크래시 금지 — 마지막 값 유지 + `⚠︎`
- 토큰·credential JSON·Authorization 헤더·응답 원문을 로그/에러 메시지/커밋에 절대 포함 금지

## T0 확정 스키마 (2026-07-23 실측)

- 유효 데이터 = 응답의 `limits` 배열. top-level `seven_day_opus` 등은 null — 사용 금지
- `{kind, percent(Double 0~100), resets_at(ISO8601 소수점초+00:00), scope{model{display_name}}}`
- 3종: `session` / `weekly_all` / `weekly_scoped`(display_name="Fable")
- 401 가능성: 토큰 만료 — Claude Code 실행으로 갱신됨

---

### Task 0: 완료 ✅ (probe/probe.sh, 커밋 32f0fc9) — 게이트 통과

후속 반영: probe.sh 토큰 argv 노출 제거(-H @stdin), DESIGN.md "Fable(Opus)" 표현 → "scoped 모델 한도" 정정.

---

### Task 1: 스캐폴드(2타깃) + 모델·프로토콜 + .gitignore

**Files:**
- Create: `Package.swift`, `.gitignore`(append 방식), `Sources/AIUsageCore/Models.swift`
- Create: `Sources/AIUsageBar/App.swift` (임시 스텁 — T3에서 교체)
- Test: `Tests/AIUsageCoreTests/ModelsTests.swift`

**Interfaces (Produces):**
- `public struct LimitReading: Equatable { public let name: String; public let utilization: Int; public let resetsAt: Date? }`
- `public protocol UsageProvider { var id: String { get }; func fetch() async throws -> [LimitReading] }`
- `LimitReading.barSummary([LimitReading]) -> String` — "64 79 80%", 빈 배열 "--"

- [ ] **Step 1: Package.swift**

```swift
// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AIUsageBar",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "AIUsageCore", path: "Sources/AIUsageCore"),
        .executableTarget(name: "AIUsageBar", dependencies: ["AIUsageCore"], path: "Sources/AIUsageBar"),
        .testTarget(name: "AIUsageCoreTests", dependencies: ["AIUsageCore"], path: "Tests/AIUsageCoreTests"),
    ]
)
```

- [ ] **Step 2: .gitignore append + App.swift 스텁**

```bash
cd AI_Usage_Menubar && touch .gitignore
grep -qx '.build/' .gitignore || echo '.build/' >> .gitignore
grep -qx 'dist/' .gitignore || echo 'dist/' >> .gitignore
```

```swift
// Sources/AIUsageBar/App.swift (T1 스텁)
import SwiftUI
@main struct AIUsageBarApp: App {
    var body: some Scene { MenuBarExtra("--") { Text("준비 중") } }
}
```

- [ ] **Step 3: 실패 테스트** (Task 1 v1의 ModelsTests와 동일 — `@testable import AIUsageCore`, 값은 64/79/80)

- [ ] **Step 4: 실패 확인** — `swift test` → 컴파일 FAIL

- [ ] **Step 5: Models.swift 구현** (public 부여 외 v1과 동일 로직)

- [ ] **Step 6: `swift test` PASS + `swift build` 성공 확인 후 커밋**

```bash
git commit -m "feat(menubar): 2-target scaffold + LimitReading/UsageProvider"
```

---

### Task 2: ClaudeProvider — limits 배열 파싱 + 토큰 캐시

**Files:**
- Create: `Sources/AIUsageCore/ClaudeProvider.swift`
- Test: `Tests/AIUsageTests/ClaudeProviderTests.swift` (top-level 코드 금지 — `func runClaudeProviderTests()`로 정의, main.swift에서 호출; 검증 명령은 전부 `swift run AIUsageTests && swift build`)

**Interfaces:**
- Produces: `public final class ClaudeProvider: UsageProvider` — `id=="claude"`; `static func parse(_ data: Data) throws -> [LimitReading]`; `static func readAccessToken() throws -> String`(stderr 보존해 keychainFailed 사유에 포함); `init(tokenLoader:)` 주입 가능(기본=readAccessToken); fetch는 토큰 메모리 캐시(+401 시 재독 1회 — 호출자는 UsageModel in-flight 가드로 직렬화되므로 actor 불필요), `ClaudeProviderError: Equatable`로 테스트에서 오류 타입 검증

**파싱 규칙 (Codex 리뷰 확정):**
- `limits`에서 kind로 탐색, 반환 순서 고정 session→weekly_all→weekly_scoped
- 각 필수 kind는 정확히 1개 — 누락/중복 시 `badSchema`; 미지 kind 무시
- percent: finite && >=0 검증(음수·비유한 throw), 100 초과는 100 클램프, 표시 시 반올림
- 이름: session→"시간한도", weekly_all→"주별한도", weekly_scoped→`(scope.model.display_name ?? "모델")+"한도"`
- resets_at: `.withFractionalSeconds` 포맷터 → 실패 시 일반 ISO8601 폴백

- [ ] **Step 1: 실패 테스트** — 케이스: 정상 3종(실측 형태 픽스처, 순서 뒤섞음)·미지 kind 무시·필수 kind 누락·weekly_scoped 중복·percent 범위밖·소수점초/일반 날짜 양쪽·display_name null 폴백("모델한도")·빈 limits

- [ ] **Step 2: FAIL 확인 → Step 3: 구현**

```swift
import Foundation

public enum ClaudeProviderError: LocalizedError {
    case keychainFailed, tokenMissing, badResponse(Int), badSchema
    public var errorDescription: String? {
        switch self {
        case .keychainFailed: return "키체인 읽기 실패"
        case .tokenMissing: return "토큰 없음 — Claude Code 로그인 확인"
        case .badResponse(let c):
            return c == 401 ? "인증 만료 — Claude Code 한번 실행해 토큰 갱신" : "API 응답 오류 (\(c))"
        case .badSchema: return "응답 스키마 불일치"
        }
    }
}

public final class ClaudeProvider: UsageProvider {
    public let id = "claude"
    private var cachedToken: String?
    public init() {}

    private struct ModelRef: Decodable { let display_name: String? }
    private struct Scope: Decodable { let model: ModelRef? }
    private struct Limit: Decodable {
        let kind: String; let percent: Double
        let resets_at: String?; let scope: Scope?
    }
    private struct Payload: Decodable { let limits: [Limit] }

    static func parseDate(_ s: String?) -> Date? {
        guard let s else { return nil }
        let f1 = ISO8601DateFormatter()
        f1.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let d = f1.date(from: s) { return d }
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
            guard l.percent.isFinite, (0...100).contains(l.percent) else {
                throw ClaudeProviderError.badSchema
            }
            return LimitReading(name: name, utilization: Int(l.percent.rounded()),
                                resetsAt: parseDate(l.resets_at))
        }
        let scoped = try one("weekly_scoped")
        let scopedName = (scoped.scope?.model?.display_name ?? "모델") + "한도"
        return [try reading("시간한도", try one("session")),
                try reading("주별한도", try one("weekly_all")),
                try reading(scopedName, scoped)]
    }

    public static func readAccessToken() throws -> String {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/usr/bin/security")
        proc.arguments = ["find-generic-password", "-s", "Claude Code-credentials", "-w"]
        let out = Pipe(); proc.standardOutput = out; proc.standardError = Pipe()
        try proc.run(); proc.waitUntilExit()
        guard proc.terminationStatus == 0,
              let json = try? JSONSerialization.jsonObject(
                  with: out.fileHandleForReading.readDataToEndOfFile()) as? [String: Any]
        else { throw ClaudeProviderError.keychainFailed }
        guard let oauth = json["claudeAiOauth"] as? [String: Any],
              let token = oauth["accessToken"] as? String, !token.isEmpty
        else { throw ClaudeProviderError.tokenMissing }
        return token
    }

    private func request(_ token: String) async throws -> (Data, Int) {
        var req = URLRequest(url: URL(string: "https://api.anthropic.com/api/oauth/usage")!)
        req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        req.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        let (data, resp) = try await URLSession.shared.data(for: req)
        return (data, (resp as? HTTPURLResponse)?.statusCode ?? -1)
    }

    // nonisolated async — MainActor 밖 협력풀에서 실행됨(키체인 Process 포함)
    public func fetch() async throws -> [LimitReading] {
        let token: String
        if let t = cachedToken { token = t } else {
            token = try Self.readAccessToken(); cachedToken = token
        }
        var (data, code) = try await request(token)
        if code == 401 {  // 토큰 회전 — 키체인 재독 1회
            cachedToken = nil
            let fresh = try Self.readAccessToken(); cachedToken = fresh
            (data, code) = try await request(fresh)
        }
        guard code == 200 else { throw ClaudeProviderError.badResponse(code) }
        return try Self.parse(data)
    }
}
```

- [ ] **Step 4: PASS 확인 → Step 5: 커밋** `feat(menubar): ClaudeProvider — limits parse + token cache/401 retry`

---

### Task 3: UsageModel(async 폴링) + MenuBarExtra UI + 아이콘

**Files:**
- Create: `Sources/AIUsageCore/UsageModel.swift`
- Create: `Sources/AIUsageBar/ClaudeIcon.swift`
- Modify: `Sources/AIUsageBar/App.swift` (스텁 교체)
- Test: `Tests/AIUsageTests/UsageModelTests.swift` (`@MainActor func runUsageModelTests() async` — main.swift top-level await로 호출)

**Interfaces:**
- `@MainActor public final class UsageModel: ObservableObject` — `@Published public private(set) var readings: [LimitReading]`, `@Published public private(set) var lastError: String?`, `public var provider: UsageProvider`, `public var barTitle: String`, `public func refresh() async` (in-flight 1개 보장), `public func startPolling(interval: TimeInterval = 60)` (취소가능 Task 루프), `public func stopPolling()`

- [ ] **Step 1: 실패 테스트** — v1의 스텁 성공/실패-유지-⚠︎ 2케이스 + 연속 refresh 중첩 시 1회만 실행 확인

- [ ] **Step 2: FAIL → Step 3: UsageModel 구현**

```swift
import Foundation
import Combine

@MainActor
public final class UsageModel: ObservableObject {
    @Published public private(set) var readings: [LimitReading] = []
    @Published public private(set) var lastError: String?
    public var provider: UsageProvider
    private var pollTask: Task<Void, Never>?
    private var inFlight = false

    public init(provider: UsageProvider = ClaudeProvider()) { self.provider = provider }

    public var barTitle: String {
        let base = LimitReading.barSummary(readings)
        return lastError == nil ? base : base + " ⚠︎"
    }

    public func refresh() async {
        guard !inFlight else { return }
        inFlight = true; defer { inFlight = false }
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
            while let self, !Task.isCancelled {
                await self.refresh()
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
            }
        }
    }

    public func stopPolling() { pollTask?.cancel(); pollTask = nil }
}
```

- [ ] **Step 4: ClaudeIcon (static 캐시) + App.swift 교체**

```swift
// ClaudeIcon.swift — v1 계획과 동일한 8방사 별표 드로잉이되 캐시 부여
import AppKit
enum ClaudeIcon {
    static let shared: NSImage = make()
    private static func make(size: CGFloat = 16) -> NSImage { /* v1 Task3 Step4 코드 그대로 */ }
}
```

```swift
// App.swift
import SwiftUI
import AppKit
import AIUsageCore

@main
struct AIUsageBarApp: App {
    @StateObject private var model: UsageModel

    init() {
        let m = UsageModel()
        m.startPolling()               // label .task 미발화 이슈 회피 — init에서 시작
        _model = StateObject(wrappedValue: m)
    }

    var body: some Scene {
        MenuBarExtra {
            if model.readings.isEmpty && model.lastError == nil { Text("불러오는 중…") }
            ForEach(model.readings, id: \.name) { r in Text(detailLine(r)) }
            if let err = model.lastError { Divider(); Text("⚠︎ \(err)") }
            Divider()
            Button("지금 새로고침") { Task { await model.refresh() } }
            Button("종료") { NSApplication.shared.terminate(nil) }.keyboardShortcut("q")
        } label: {
            HStack(spacing: 3) {
                Image(nsImage: ClaudeIcon.shared)
                Text(model.barTitle)
            }
        }
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
```

- [ ] **Step 5: `swift test && swift build` 전체 PASS → 커밋** `feat(menubar): async-poll UsageModel + MenuBarExtra UI + cached icon`

---

### Task 4: 번들 스크립트 + 실기동 검증

**Files:** Create `make_app.sh`, `README.md`

- [ ] **Step 1: make_app.sh (스테이징→검증→교체)**

```bash
#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"
swift build -c release
BIN="$(swift build -c release --show-bin-path)/AIUsageBar"
STAGE="$(mktemp -d)/AI Usage.app"
mkdir -p "$STAGE/Contents/MacOS"
cp "$BIN" "$STAGE/Contents/MacOS/AIUsageBar"
cat > "$STAGE/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>AI Usage</string>
  <key>CFBundleIdentifier</key><string>com.chaelimi.aiusagebar</string>
  <key>CFBundleExecutable</key><string>AIUsageBar</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
</dict></plist>
EOF
plutil -lint "$STAGE/Contents/Info.plist"
codesign --force --sign - "$STAGE"
codesign --verify --strict "$STAGE"
mkdir -p dist && rm -rf "dist/AI Usage.app"
mv "$STAGE" "dist/AI Usage.app"
echo "OK: dist/AI Usage.app"
```

- [ ] **Step 2: 빌드 → Step 3: 실기동(사용자 동석)** — `open "dist/AI Usage.app"`; 확인 항목: 상단바 아이콘+숫자(설정 %와 대조)·드롭다운 상세·새로고침·종료·**재빌드 후 2회째 실행 시 키체인 프롬프트 재발 여부**(ad-hoc 서명 변동 리스크)·다크/라이트 아이콘 육안
- [ ] **Step 4: README(설치·로그인항목 수동등록 안내) 작성 → 커밋** `feat(menubar): app bundle script + docs — v1 complete`

---

## 리뷰 반영 이력

- v2 (2026-07-23): T0 실측(limits 배열·0~100 스케일·소수점초) + Codex(gpt-5.6-sol) 리뷰(kind 탐색·정확히1개 규칙·percent 검증·토큰 캐시/401 재시도·argv 노출 제거·2타깃 분리·async 폴링 루프·번들 스테이징/검증) + Fable 감시자(코어 분리 선제·gitignore 조기화·label .task 미발화·서명 변동 프롬프트 검증 항목) 통합 반영.
