import Foundation

// 초경량 테스트 하니스 — CLT-only 환경용 (XCTest/Testing 부재)
var testFailures = 0

func expect(_ cond: Bool, _ label: String, file: String = #fileID, line: Int = #line) {
    if cond {
        print("PASS  \(label)")
    } else {
        testFailures += 1
        print("FAIL  \(label)  (\(file):\(line))")
    }
}

func expectThrows(_ label: String, _ body: () throws -> Void) {
    do {
        try body()
        testFailures += 1
        print("FAIL  \(label) — 오류가 나야 하는데 성공함")
    } catch {
        print("PASS  \(label)")
    }
}

func finish() -> Never {
    if testFailures > 0 {
        print("== \(testFailures) FAILURE(S) ==")
        exit(1)
    }
    print("== ALL PASS ==")
    exit(0)
}
