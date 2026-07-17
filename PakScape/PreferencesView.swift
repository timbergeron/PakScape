import SwiftUI

struct PreferencesView: View {
    @AppStorage(FinderPreferencesKey.actionsEnabled) private var actionsEnabled: Bool = true

    var body: some View {
        Form {
            Toggle("Enable PakScape Finder services", isOn: $actionsEnabled)
                .onChange(of: actionsEnabled) { _, newValue in
                    FinderServiceManager.shared.updateRegistration(isEnabled: newValue)
                }

            Text("Use Finder's Services menu to extract selected PAK/PK3 archives or pack selected folders. PakScape will ask where to save the result. You can also manage Services in System Settings under Keyboard Shortcuts.")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .padding(.top, 4)
        }
        .padding()
        .frame(minWidth: 420)
    }
}
