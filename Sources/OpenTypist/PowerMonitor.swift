import Combine
import Foundation
import IOKit.ps

/// What to do in a power situation (on battery, Low Power Mode).
enum PowerBehavior: String, CaseIterable, Identifiable {
    case normal, smallerModel, pause
    var id: String { rawValue }
    var title: String {
        switch self {
        case .normal: return "Keep going as usual"
        case .smallerModel: return "Use the smaller model"
        case .pause: return "Pause suggestions"
        }
    }
}

/// Watches the power source and Low Power Mode, and calls `onChange`.
final class PowerMonitor: ObservableObject {
    static let shared = PowerMonitor()

    @Published private(set) var onBattery = PowerMonitor.readOnBattery()
    @Published private(set) var lowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
    var onChange: (() -> Void)?

    private var source: CFRunLoopSource?
    private var observer: NSObjectProtocol?

    func start() {
        guard source == nil else { return }
        let context = Unmanaged.passUnretained(self).toOpaque()
        source = IOPSNotificationCreateRunLoopSource({ context in
            guard let context else { return }
            Unmanaged<PowerMonitor>.fromOpaque(context).takeUnretainedValue().refresh()
        }, context)?.takeRetainedValue()
        if let source { CFRunLoopAddSource(CFRunLoopGetMain(), source, .defaultMode) }
        observer = NotificationCenter.default.addObserver(
            forName: .NSProcessInfoPowerStateDidChange, object: nil, queue: .main
        ) { [weak self] _ in self?.refresh() }
    }

    private func refresh() {
        DispatchQueue.main.async {
            let battery = Self.readOnBattery()
            let lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
            guard battery != self.onBattery || lowPower != self.lowPowerMode else { return }
            self.onBattery = battery
            self.lowPowerMode = lowPower
            self.onChange?()
        }
    }

    /// The behavior that applies right now; Low Power Mode wins over battery.
    var behavior: PowerBehavior {
        let settings = Settings.shared
        if lowPowerMode, settings.lowPowerBehavior != .normal { return settings.lowPowerBehavior }
        if onBattery { return settings.batteryBehavior }
        return .normal
    }

    var situation: String {
        if lowPowerMode, Settings.shared.lowPowerBehavior != .normal { return "in Low Power Mode" }
        return "on battery"
    }

    private static func readOnBattery() -> Bool {
        guard let snapshot = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
              let type = IOPSGetProvidingPowerSourceType(snapshot)?.takeUnretainedValue() else { return false }
        return (type as String) == kIOPMBatteryPowerKey
    }
}
