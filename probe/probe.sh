#!/bin/bash
# probe.sh — 키체인 토큰으로 usage 엔드포인트 1회 호출, 스키마 확인용 (응답 저장·커밋 금지)
set -euo pipefail
CRED=$(security find-generic-password -s "Claude Code-credentials" -w)
TOKEN=$(echo "$CRED" | python3 -c "import sys,json;print(json.load(sys.stdin)['claudeAiOauth']['accessToken'])")
# 토큰을 argv에 노출하지 않기 위해 헤더는 stdin(-H @-)으로 전달
printf 'Authorization: Bearer %s\n' "$TOKEN" | curl -sS "https://api.anthropic.com/api/oauth/usage" \
  -H @- \
  -H "anthropic-beta: oauth-2025-04-20" | python3 -m json.tool
