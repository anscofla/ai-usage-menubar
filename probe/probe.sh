#!/bin/bash
# probe.sh — 키체인 토큰으로 usage 엔드포인트 1회 호출, 스키마 확인용.
# 보안: 토큰·응답 바디를 출력하지 않는다 — HTTP 상태와 스키마 판정만 출력.
set -euo pipefail
CRED=$(security find-generic-password -s "Claude Code-credentials" -w)
TOKEN=$(echo "$CRED" | python3 -c "import sys,json;print(json.load(sys.stdin)['claudeAiOauth']['accessToken'])")
# 토큰을 argv에 노출하지 않기 위해 헤더는 stdin(-H @-)으로 전달
printf 'Authorization: Bearer %s\n' "$TOKEN" | curl -sS -w '\n%{http_code}' "https://api.anthropic.com/api/oauth/usage" \
  -H @- \
  -H "anthropic-beta: oauth-2025-04-20" | python3 -c '
import sys, json
*body, status = sys.stdin.read().rsplit("\n", 1)
print("HTTP", status)
try:
    limits = json.loads(body[0]).get("limits")
except Exception:
    print("schema FAIL: not JSON"); sys.exit(1)
if not isinstance(limits, list):
    print("schema FAIL: limits missing"); sys.exit(1)
kinds = [l.get("kind") for l in limits if isinstance(l, dict)]
ok = all(isinstance(l.get("percent"), (int, float)) for l in limits if isinstance(l, dict))
print("schema OK" if ok else "schema FAIL: percent type", "— kinds:", kinds)
'
