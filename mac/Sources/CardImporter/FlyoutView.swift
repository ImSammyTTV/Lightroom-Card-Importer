import SwiftUI
import AppKit

struct FlyoutView: View {
    @EnvironmentObject var model: ImportModel

    private var showPercent: Bool { model.busy || model.finished }
    private var barColor: Color { model.finished ? .green : .accentColor }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack {
                Text("Card Importer").font(.headline)
                Spacer()
                Button("Import now") { model.importNow() }
                    .buttonStyle(.link)
                    .disabled(model.busy)
            }

            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(model.phase)
                        .font(.title3.weight(.semibold))
                        .foregroundStyle(barColor)
                    Spacer()
                    if showPercent {
                        Text("\(Int(model.percent))%").font(.title3.weight(.semibold))
                    }
                }
                ProgressView(value: showPercent ? model.percent : 0, total: 100)
                    .tint(barColor)
                Text(model.detail)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(2)
            }

            Divider()

            VStack(alignment: .leading, spacing: 4) {
                Text("CARDS AND PHONES").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                if model.cards.isEmpty && model.phoneLines.isEmpty {
                    Text("Looking for cards…")
                } else {
                    ForEach(model.cards) { card in
                        Text("●  \(card.name)  ·  \(String(format: "%.1f", card.usedGB)) GB used")
                            .lineLimit(1)
                    }
                    ForEach(model.phoneLines, id: \.self) { line in
                        Text(line).lineLimit(1)
                    }
                }
            }

            VStack(alignment: .leading, spacing: 4) {
                Text("RECENT").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                if model.history.isEmpty {
                    Text("Nothing imported yet.").foregroundStyle(.secondary)
                } else {
                    ForEach(Array(model.history.prefix(4).enumerated()), id: \.offset) { _, line in
                        Text(line).font(.caption).foregroundStyle(.secondary).lineLimit(2)
                    }
                }
            }

            Divider()

            VStack(alignment: .leading, spacing: 6) {
                Text("IMPORT THESE FILE TYPES").font(.caption2.weight(.semibold)).foregroundStyle(.secondary)
                HStack(spacing: 14) {
                    Toggle("RAW", isOn: $model.importRaw)
                    Toggle("JPEG / HEIC", isOn: $model.importJpeg)
                    Toggle("Videos", isOn: $model.importVideo)
                }
            }

            Toggle("Delete files from card after import", isOn: $model.deleteAfter)
            Toggle("Start at login", isOn: Binding(
                get: { model.launchAtLogin },
                set: { model.setLaunchAtLogin($0) }
            ))

            Toggle("Check for updates automatically", isOn: $model.autoUpdate)

            HStack {
                Toggle("Import from phones", isOn: Binding(
                    get: { model.phonesEnabled },
                    set: { model.setPhonesEnabled($0) }
                ))
                Spacer()
                Button("Forget phones") { model.forgetPhones() }.buttonStyle(.link)
            }

            HStack {
                Text(model.libraryPath)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer()
                Button("Change…") { model.chooseLibrary() }
            }

            HStack {
                Button("Open photos folder") { model.openLibrary() }.buttonStyle(.link)
                Spacer()
                Button("Quit") { NSApplication.shared.terminate(nil) }.buttonStyle(.link)
            }

            HStack {
                Text(model.updateText)
                    .font(.caption)
                    .foregroundStyle(model.updateAvailable ? Color.accentColor : Color.secondary)
                    .lineLimit(2)
                Spacer()
                if model.updateAvailable {
                    Button("Install update") { model.installUpdate() }.buttonStyle(.link)
                } else {
                    Button("Check now") { model.checkForUpdate(manual: true) }.buttonStyle(.link)
                }
            }
        }
        .padding(16)
        .frame(width: 360)
    }
}
