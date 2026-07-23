# AI Usage Tray (Windows) — 설계 스펙 v1

작성 2026-07-23. 맥 메뉴바 앱(v1.0.0)의 윈도우 포팅. 개발 머신=macOS(크로스빌드), 실기동 검수=사용자 회사 윈도우 PC.

## 목표

윈도우 작업표시줄 트레이에 클로드 사용률(세션/주간/모델별)을 상시 표시. 데이터 소스·의미는 맥 버전과 동일, UI는 윈도우 트레이 관습을 따름.

## 아키텍처

기존 레포 `windows/` 폴더, .NET 8 솔루션 3프로젝트:

- `AIUsage.Core` — 클래스 라이브러리(UI 무관, 크로스플랫폼). 토큰 로더, usage API 클라이언트, limits 파서, 표시 상태머신.
- `AIUsage.Tray` — WinForms exe. NotifyIcon, 60초 폴링 타이머, GDI 아이콘 렌더링, 컨텍스트 메뉴, 자동시작 등록.
- `AIUsage.Tests` — 콘솔 assert 하니스(맥 AIUsageTests와 동형). XCTest/xUnit 미사용, 실패 시 exit 1. 맥에서 `dotnet run`으로 실행 가능.

## 데이터 흐름

1. 토큰: `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`. 파일 없음/파싱 실패/만료(expiresAt 경과)는 각각 구분된 에러 상태.
2. API: `GET https://api.anthropic.com/api/oauth/usage`, `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`.
3. 파싱: **유효 데이터는 `limits` 배열뿐**. kind=session/weekly_all/weekly_scoped, `utilization` 0~100, `resets_at` ISO8601(소수점초 허용). weekly_scoped는 정확히 1개 가정 — 0개면 해당 슬롯 표시 생략, 2개 이상이면 스키마 에러.
4. 갱신: 토큰 리프레시는 하지 않음(Claude Code가 갱신한 것을 재사용). 401/만료 시 에러 상태로 강등.

## UI

- **트레이 아이콘(16×16 GDI 동적 렌더링)**: 3개 중 최고 utilization 정수를 그려 넣음. 배경 초록(<70)/주황(70~89)/빨강(≥90). 값이 100이면 폰트를 한 단계 줄여 "100" 세 자리를 그대로 렌더(검수 시 가독성 확인 항목).
- **에러 아이콘**: 회색 배경 `!`.
- **툴팁**: `Session 64% · Weekly 79% · Model 80%` (에러 시 사유 한 줄). NotifyIcon 툴팁 63자 제한 준수.
- **컨텍스트 메뉴(우클릭)**: 한도 3행(라벨 · % · 리셋 카운트다운 "resets in 3h 12m"), 구분선, Refresh now, Start at login(체크 토글, HKCU `Software\Microsoft\Windows\CurrentVersion\Run` 키), Quit.
- 좌클릭=컨텍스트 메뉴와 동일 메뉴 표시.

## 폴링·상태머신

- 시작 즉시 1회 + 60초 간격. Refresh now는 즉시 재조회.
- 상태: `ok(limits)` / `degraded(reason)` — 맥 버전과 동일하게 어떤 실패도 크래시 없이 degraded로. 연속 실패 시 마지막 성공 데이터 유지하지 않고 에러 아이콘(오해 방지).
- 중복 실행 가드: named mutex(`Global\AIUsageTray`) — 이미 실행 중이면 조용히 종료. (맥 v1의 미비점 보완.)

## 보안(유지 규칙)

토큰·Authorization 헤더·API 응답 원문을 로그/에러 메시지/커밋/픽스처에 절대 포함하지 않음. 테스트 픽스처는 합성 JSON만 사용.

## 빌드·배포

- 맥 크로스빌드: `dotnet publish AIUsage.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableWindowsTargeting=true` → 단일 exe(~60–70MB, 런타임 포함).
- 검수: exe zip을 GitHub pre-release로 올려 회사 PC에서 다운로드→실행 검수. SmartScreen "알 수 없는 게시자" 경고는 More info→Run anyway (README 문서화).
- 통과 후: Release v1.1.0 = 맥 zip + 윈도우 zip, README에 Windows 섹션(요구사항: Windows 10+, Claude Code 로그인).

## 검수 체크리스트(회사 PC)

1. exe 실행 → 트레이 아이콘 표시, 숫자·색 정상
2. 툴팁 3개 수치, 우클릭 메뉴 카운트다운
3. Refresh now 동작
4. Start at login 토글 → 재부팅 후 자동 시작
5. `.credentials.json` 임시 리네임 → `!` 아이콘 강등, 복원 → 다음 폴링에 복귀
6. 중복 실행 시 두 번째 인스턴스 조용히 종료
