import Foundation

public struct LimitReading: Equatable {
    public let name: String
    public let utilization: Int
    public let resetsAt: Date?

    public init(name: String, utilization: Int, resetsAt: Date?) {
        self.name = name
        self.utilization = utilization
        self.resetsAt = resetsAt
    }

    /// 상단바 표기: "64 79 80%", 빈 배열이면 "--"
    public static func barSummary(_ readings: [LimitReading]) -> String {
        guard !readings.isEmpty else { return "--" }
        return readings.map { String($0.utilization) }.joined(separator: " ") + "%"
    }
}

public protocol UsageProvider {
    var id: String { get }
    func fetch() async throws -> [LimitReading]
}
