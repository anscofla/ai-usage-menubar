# AI Usage Menubar

맥 상단바에 클로드(+코덱스) 사용률을 배터리 게이지 픽토그램으로 상시 표시하는 메뉴바 앱 — 채움 폭·숫자 크기가 사용률에 비례. 데이터는 키체인의 Claude Code OAuth 토큰으로 Anthropic usage 엔드포인트를 60초 폴링.

## 빌드·설치

```bash
bash make_app.sh          # → dist/AI Usage.app
open "dist/AI Usage.app"
```

- 첫 실행 시 키체인 접근 허용 프롬프트 1회("항상 허용" 권장).
- 로그인 자동실행: 시스템 설정 → 일반 → 로그인 항목에 `dist/AI Usage.app` 추가(수동).
- 재설치 시 기존 인스턴스를 먼저 종료할 것(중복 실행 가드 없음 — 아이콘 2개 뜸).
- 재빌드는 ad-hoc 재서명이라 키체인 "항상 허용"이 리셋될 수 있음 — 빌드마다 허용 1회.

## 테스트

```bash
swift run AIUsageTests    # CLT-only 환경이라 XCTest 대신 assert 하니스 (실패 시 exit 1)
```

## 알려진 한계 (v1)

- 비공식 엔드포인트 의존 — 스키마 변경 시 상단바에 `⚠︎` 표시로 강등(크래시 없음).
- `weekly_scoped` 한도가 정확히 1개라는 가정 — 스코프 모델이 2종이 되면 스키마 오류(⚠︎) 처리.
- 토큰은 Claude Code가 갱신한 값을 재사용 — 장기 미사용으로 만료되면 Claude Code를 한 번 실행.
- 코덱스는 `~/.codex/auth.json` + 비공식 ChatGPT 엔드포인트 의존 — Codex CLI 미사용 시 섹션 자체가 안 뜸.


## Windows (시스템 트레이)

`windows/`에 .NET 포팅판 — 트레이 아이콘이 최고 사용률을 신호등 색으로 표시하고, 우클릭으로 상세를 볼 수 있다.

- 요구사항: Windows 10+, **Windows 네이티브로** Claude Code 설치·구독(OAuth) 로그인. WSL 설치와 API 키/Bedrock/Vertex 인증은 미지원(토큰 파일이 앱이 볼 수 있는 위치에 없음).
- 토큰 소스: `%USERPROFILE%\.claude\.credentials.json` (또는 `%CLAUDE_CONFIG_DIR%`). `%USERPROFILE%\.codex\auth.json`(또는 `%CODEX_HOME%`)이 있으면 코덱스 사용률도 함께 표시 — 아이콘 숫자·색은 두 프로바이더 통틀어 최악 한도 기준.
- 소스 빌드(권장): .NET 10 SDK 설치 후
  `dotnet publish windows/AIUsage.Tray -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- 빌드된 exe: Releases 참조(약 45MB zip, self-contained — .NET 설치 불필요; SHA-256 체크섬 첨부). 서명이 없어 SmartScreen 경고가 뜬다 — 소스 빌드로 회피 가능. self-contained 빌드는 .NET 런타임 패치가 자동 반영되지 않아 필요 시 재발행.
