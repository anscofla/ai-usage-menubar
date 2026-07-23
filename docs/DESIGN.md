# AI_Usage_Menubar — 설계 스펙 (v1, 2026-07-23 승인)

맥 상단바에 AI 서비스 잔여 사용량을 상시 표시하는 네이티브 메뉴바 앱. v1 = Claude 전용, 구조는 멀티 프로바이더 확장형.

## 목표 표시 형태

- 상단바: `(클로드 로고) 56 77 79%` — 순서 = 시간한도(session) / 주별한도(weekly_all) / scoped 모델 한도(weekly_scoped, 현재 "Fable")
- (T0 실측 2026-07-23: 유효 데이터는 응답 `limits` 배열 — top-level `seven_day_opus`는 null)
- 값 = 클로드 앱 설정 화면과 동일한 **사용률 %** (잔여 아님 — 사용자 확인 완료)
- 멀티 프로바이더 시(v2+): `(클로드) 56 77 79% | (코덱스) 59%`

## 데이터 소스 (사용자 승인)

- 맥 키체인 `Claude Code-credentials`의 OAuth accessToken 재사용
- Anthropic OAuth usage 엔드포인트 호출 → 5h/주간/Opus 사용률 파싱
- 토큰 만료 시 키체인 재독 (Claude Code가 갱신해둔 값 활용, 직접 refresh 안 함)
- ⚠️ 비공식 엔드포인트 — 구현 T0에서 실제 호출로 스키마 확인 필수. 3개 값이 안 나오면 대안(ccusage식 로컬 집계) 재논의

## 아키텍처 (Swift, 3 유닛)

1. `UsageProvider` 프로토콜 — `fetch() async throws -> [LimitReading]` (이름·사용률%·리셋시각)
2. `ClaudeProvider` — 키체인 읽기 → usage API → 3개 % 파싱
3. `MenuBarApp` — SwiftUI `MenuBarExtra`; 타이틀 = 템플릿 아이콘(단색 클로드 별표, 다크/라이트 자동) + 숫자 나열; 드롭다운 = 한도별 상세(이름·%·리셋까지 남은 시간) + 지금 새로고침 + 종료

## 동작 규칙

- 폴링 60초 (내부 상수)
- 실패 시: 마지막 값 유지 + `⚠︎` 표시 + 드롭다운에 사유. 크래시로 상단바에서 사라지지 않기
- 로그인 자동실행은 v1 수동 등록

## 빌드·배포 (사용자 지시 반영)

- 위치: 워크스페이스 루트 `AI_Usage_Menubar/` (배포 염두 — 기존 10_Lab에 넣지 않음)
- Xcode GUI 불사용 — SwiftPM `swift build` + .app 번들 조립 스크립트(`make_app.sh`)
- 산출물: `AI Usage.app` (자가서명/ad-hoc)
