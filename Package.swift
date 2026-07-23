// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "AIUsageBar",
    platforms: [.macOS(.v13)],
    targets: [
        .target(name: "AIUsageCore", path: "Sources/AIUsageCore"),
        .executableTarget(name: "AIUsageBar", dependencies: ["AIUsageCore"], path: "Sources/AIUsageBar"),
        // CLT-only 환경(XCTest/Testing 모듈 부재)이라 assert 기반 실행형 테스트 타깃 사용.
        // 실행: swift run AIUsageTests (실패 시 exit 1)
        .executableTarget(name: "AIUsageTests", dependencies: ["AIUsageCore"], path: "Tests/AIUsageTests"),
    ]
)
