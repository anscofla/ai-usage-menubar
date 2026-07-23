import Foundation
import AIUsageCore

// ── ModelsTests ──
let rs = [
    LimitReading(name: "시간한도", utilization: 64, resetsAt: nil),
    LimitReading(name: "주별한도", utilization: 79, resetsAt: nil),
    LimitReading(name: "Fable한도", utilization: 80, resetsAt: nil),
]
expect(LimitReading.barSummary(rs) == "64 79 80%", "barSummary 3값 나열")
expect(LimitReading.barSummary([]) == "--", "barSummary 빈 배열")

// ── ClaudeProviderTests ──
runClaudeProviderTests()
await runClaudeProviderFetchTests()

// ── CodexProviderTests ──
runCodexProviderTests()
await runCodexProviderFetchTests()

// ── UsageModelTests ──
await runUsageModelTests()
await runMultiProviderTests()

finish()
