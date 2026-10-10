import SwiftUI

@main
struct CardImporterApp: App {
    @StateObject private var model = ImportModel()

    var body: some Scene {
        MenuBarExtra {
            FlyoutView().environmentObject(model)
        } label: {
            Image(systemName: model.busy ? "arrow.down.circle.fill" : "sdcard")
        }
        .menuBarExtraStyle(.window)
    }
}
