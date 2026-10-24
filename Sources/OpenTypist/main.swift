import AppKit

let arguments = CommandLine.arguments
if arguments.contains("--selftest") {
    SelfTest.run(arguments: arguments)
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
