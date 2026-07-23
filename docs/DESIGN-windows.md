# AI Usage Tray (Windows) — 설계 스펙 v2

작성 2026-07-23 (v2: Codex 적대검증 14건 반영). 맥 메뉴바 앱(v1.0.0)의 윈도우 포팅. 개발 머신=macOS(로직·테스트), 릴리스 빌드=GitHub Actions windows-latest, 실기동 검수=사용자 회사 윈도우 PC.

## 목표

윈도우 작업표시줄 트레이에 클로드 사용률(세션/주간/모델별)을 상시 표시. 데이터 소스·의미는 맥 버전과 동일, UI는 윈도우 트레이 관습을 따름.

**지원 범위**: 네이티브 Windows에서 구독(OAuth) 로그인한 Claude Code 전용. WSL 내부 Claude Code, API 키/Bedrock/Vertex 인증은 미지원(README 명시).

## 아키텍처

기존 레포 `windows/` 폴더, **.NET 10 LTS** 솔루션 3프로젝트:

- `AIUsage.Core` — 클래스 라이브러리(UI 무관). 토큰 로더, usage API 클라이언트, limits 파서, 표시 상태머신.
- `AIUsage.Tray` — WinForms exe(`<OutputType>WinExe</OutputType>`). NotifyIcon, 폴링 루프, GDI 아이콘 렌더링, 컨텍스트 메뉴, 자동시작 등록.
- `AIUsage.Tests` — 콘솔 assert 하니스(맥 AIUsageTests와 동형, xUnit 미사용, 실패 시 exit 1). 맥에서 `dotnet run` 가능.

## 데이터 흐름

1. 토큰: `%CLAUDE_CONFIG_DIR%\.credentials.json`(환경변수 설정 시) 또는 기본 `%USERPROFILE%\.claude\.credentials.json` → `claudeAiOauth.accessToken`. `expiresAt`=Unix epoch **밀리초**(맥 파서와 동일). 파일 없음/파싱 실패/만료는 구분된 에러 상태. 파일 교체 레이스 대비: 읽기 실패 시 500ms 후 1회 재시도.
2. API: `GET https://api.anthropic.com/api/oauth/usage`, `Authorization: Bearer <token>`, `anthropic-beta: oauth-2025-04-20`. 싱글턴 `HttpClient`, 타임아웃 15초.
3. 파싱: **유효 데이터는 `limits` 배열뿐**(와이어 필드명 `percent` 0~100 — 내부 모델 속성명은 자유). kind=session/weekly_all/weekly_scoped, `resets_at` ISO8601(소수점초 허용). **weekly_scoped는 정확히 1개** — 0개 또는 2개 이상이면 스키마 에러(맥 계약과 동일).
4. 토큰 갱신: 자체 리프레시 안 함. **401 수신 시 credentials 파일 재독 후 1회 재시도, 그래도 실패하면 degraded**(맥 ClaudeProvider와 동일). 토큰은 폴링마다 캐시 사용, 401에서만 재독.

## UI

- **트레이 아이콘(동적 GDI 렌더링)**: 3개 중 최고 percent 정수를 렌더. 배경 초록(<70)/주황(70~89)/빨강(≥90). 앱은 per-monitor DPI aware로 선언하고 `SystemInformation.SmallIconSize` 기준 크기로 렌더(16/20/24/32px). 세 자리(100)는 가독성 검수 실패 시 `99+` 폴백. 폰트는 Segoe UI 고정, 중앙 정렬.
- **GDI 소유권**: `Bitmap.GetHicon()`→`Icon.FromHandle()` 후 이전 아이콘 교체 시 `DestroyIcon(hIcon)` P/Invoke로 명시 해제 + Bitmap/Graphics/Font/Brush 전부 using. 장시간 핸들 누수 없음이 검수 항목.
- **에러 아이콘**: 회색 배경 `!`.
- **툴팁**: `Session 64% · Weekly 79% · Model 80%`(에러 시 사유 한 줄). `NotifyIcon.Text` 한도 127자 — 대입 전 127자 절단(초과 시 예외 방지).
- **컨텍스트 메뉴(좌/우클릭 동일)**: 한도 3행(라벨 · % · "resets in 3h 12m"), 구분선, Refresh now, Start at login(체크 토글), Quit.
- **degraded 시**: 맥과 동일하게 **마지막 성공 데이터를 유지하고 경고 행 추가**(메뉴 상단에 사유), 아이콘은 `!`. 시작 직후 아직 데이터 없으면 "loading" 상태 구분.

## 폴링·상태머신

- 시작 즉시 1회, 이후 **완료 시점 기준 60초 후** 다음 폴링(고정주기 중첩 금지). 단일 in-flight 보장 — Refresh now는 진행 중이면 조인, 아니면 즉시 실행. `async/await`로 UI 스레드 비차단, UI 갱신은 WinForms 스레드로 마샬링, 종료 시 취소.
- 중복 실행 가드: **`Local\AIUsageTray` named mutex**(세션 로컬 — Global 금지, 크로스유저 ACL 문제 회피). 이미 실행 중이면 조용히 종료.

## 자동시작(Start at login)

- 토글 ON 시: exe를 `%LOCALAPPDATA%\AIUsageTray\AIUsageTray.exe`로 자기복사(임시 폴더·Downloads에서 실행돼도 안정 경로 확보) 후 HKCU `Software\Microsoft\Windows\CurrentVersion\Run`에 **따옴표 포함 전체 경로** 등록.
- 토글 OFF 시: Run 값이 우리 exe 경로인지 확인 후 삭제. 레지스트리 접근 거부 시 메뉴에 실패 사유 표시(크래시 금지).

## 보안(유지 규칙)

토큰·Authorization 헤더·API 응답 원문을 로그/에러 메시지/커밋/픽스처에 절대 포함하지 않음. 테스트 픽스처는 합성 JSON만.

## 빌드·배포

- **맥(개발 루프)**: `dotnet build -p:EnableWindowsTargeting=true` 컴파일 확인 + `AIUsage.Tests` 실행. 맥산 exe는 배포 금지(MS 공식: 비-Windows 산출물은 콘솔 서브시스템/아이콘 누락 가능).
- **릴리스(GitHub Actions windows-latest)**: `dotnet publish AIUsage.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` → 단일 exe(크기는 산출물에서 실측·README 기재). CI에서 테스트+publish, 산출물 zip을 workflow artifact로.
- 검수: pre-release로 zip+**SHA-256 체크섬** 게시 → 회사 PC 다운로드·검수. 서명 없는 exe의 SmartScreen 경고는 README에 사실대로 안내하되, 소스 빌드 대안(`dotnet publish` 직접 실행)을 1순위로 권장.
- 통과 후: Release v1.1.0 = 맥 zip + 윈도우 zip + 체크섬, README에 Windows 섹션(요구사항: Windows 10+, 네이티브 Claude Code OAuth 로그인). self-contained는 런타임 패치가 자동 적용되지 않으므로 .NET 10 패치 릴리스 시 재발행 가능성 문서화.

## 검수 체크리스트(회사 PC)

1. exe 실행 → 트레이 아이콘 표시, 숫자·색 정상, **콘솔창 안 뜸**
2. DPI 100/125/150%에서 아이콘 가독성(모니터 배율 변경)
3. 툴팁 3개 수치, 메뉴 카운트다운, Refresh now
4. Start at login ON → 재부팅 후 자동 시작(LOCALAPPDATA 복사본), OFF → 등록 해제
5. `.credentials.json` 임시 리네임 → `!` + 마지막 데이터 유지 + 사유 표시, 복원 → 다음 폴링 복귀
6. 중복 실행 시 두 번째 인스턴스 조용히 종료
7. 1시간+ 방치 후 작업관리자에서 GDI 개체 수 증가 없음(핸들 누수 체크)
