# AI Usage Menubar

맥 상단바에 클로드 사용률 3종(시간한도/주별한도/모델한도)을 `✳ 64 79 80%`로 상시 표시하는 메뉴바 앱. 데이터는 키체인의 Claude Code OAuth 토큰으로 Anthropic usage 엔드포인트를 60초 폴링.

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
- 코덱스 등 타 프로바이더는 v2 (UsageProvider 구현체 추가로 확장).
