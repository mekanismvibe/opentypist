import AppKit

let arguments = CommandLine.arguments
if arguments.contains("--selftest-offline") {
    SelfTest.runOffline(arguments: arguments)
}
if arguments.contains("--selftest") {
    SelfTest.run(arguments: arguments)
}
if arguments.contains("--snapshot-personal") {
    SelfTest.snapshotPersonal(arguments: arguments)
}
if arguments.contains("--calibrate") {
    SelfTest.calibrate()
}
if arguments.contains("--complete") {
    SelfTest.completeFiles(arguments: arguments)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
