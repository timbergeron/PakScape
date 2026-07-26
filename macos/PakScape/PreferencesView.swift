import SwiftUI

struct PreferencesView: View {
    @AppStorage(FinderPreferencesKey.actionsEnabled) private var actionsEnabled: Bool = true
    @AppStorage(BspPreviewPreferencesKey.showArmors) private var showArmors: Bool = true
    @AppStorage(BspPreviewPreferencesKey.showMegaHealth) private var showMegaHealth: Bool = true
    @AppStorage(BspPreviewPreferencesKey.showPowerups) private var showPowerups: Bool = true
    @AppStorage(BspPreviewPreferencesKey.showMajorWeapons) private var showMajorWeapons: Bool = true
    @AppStorage(BspPreviewPreferencesKey.showFlags) private var showFlags: Bool = true

    var body: some View {
        Form {
            Section("Finder") {
                Toggle("Enable PakScape Finder services", isOn: $actionsEnabled)
                    .onChange(of: actionsEnabled) { _, newValue in
                        FinderServiceManager.shared.updateRegistration(isEnabled: newValue)
                    }

                Text("Use Finder's Services menu to extract selected PAK/PK3 archives or pack selected folders. PakScape will ask where to save the result. You can also manage Services in System Settings under Keyboard Shortcuts.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
                    .padding(.top, 4)
            }

            Section("BSP level previews") {
                Toggle("Show armor", isOn: $showArmors)
                Toggle("Show megahealth", isOn: $showMegaHealth)
                Toggle("Show Quad, Ring, and Pentagram", isOn: $showPowerups)
                Toggle("Show rocket launcher and lightning gun", isOn: $showMajorWeapons)
                Toggle("Show CTF flags", isOn: $showFlags)

                Text("Important items appear as labeled badges in Quick Look and archive thumbnails.")
                    .font(.footnote)
                    .foregroundStyle(.secondary)
            }
        }
        .padding()
        .frame(minWidth: 460)
    }
}
