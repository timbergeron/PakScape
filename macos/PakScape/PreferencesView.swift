import SwiftUI
import AppKit

private let pakScapeAccent = Color(
    red: 54 / 255,
    green: 197 / 255,
    blue: 73 / 255
)

enum PakScapePreferencesKey {
    static let appearance = "appearance"
    static let defaultView = "defaultView"
    static let defaultSort = "defaultSort"
    static let defaultSortAscending = "defaultSortAscending"
    static let textSize = "textSize"
    static let confirmDeletion = "confirmDeletion"
    static let confirmOverwrite = "confirmOverwrite"
    static let backupBeforeSave = "backupBeforeSave"
    static let quickLookOnSelection = "quickLookOnSelection"
    static let animateModels = "animateModels"
    static let showBspMarkers = "showBspMarkers"
    static let previewBackground = "previewBackground"
    static let quakeEnginePath = "quakeEnginePath"
    static let useQuakeEngineForDemos = "useQuakeEngineForDemos"
}

func pakScapePreviewAppearance() -> NSAppearance {
    switch UserDefaults.standard.string(forKey: PakScapePreferencesKey.previewBackground) {
    case "light":
        return NSAppearance(named: .aqua) ?? NSApp.effectiveAppearance
    case "dark":
        return NSAppearance(named: .darkAqua) ?? NSApp.effectiveAppearance
    default:
        return NSApp.effectiveAppearance
    }
}

@MainActor
func pakScapeConfirmOverwriteIfNeeded(at url: URL) -> Bool {
    guard FileManager.default.fileExists(atPath: url.path) else { return true }
    let shouldConfirm = UserDefaults.standard.object(
        forKey: PakScapePreferencesKey.confirmOverwrite
    ) as? Bool ?? true
    guard shouldConfirm else { return true }

    let alert = NSAlert()
    alert.alertStyle = .warning
    alert.messageText = "Replace Existing File?"
    alert.informativeText = "\(url.lastPathComponent) already exists. Do you want to replace it?"
    alert.addButton(withTitle: "Replace")
    alert.addButton(withTitle: "Cancel")
    return alert.runModal() == .alertFirstButtonReturn
}

private enum PreferencesTab: Hashable {
    case general
    case archive
    case preview
    case finder

    var title: String {
        switch self {
        case .general: return "General"
        case .archive: return "Archive"
        case .preview: return "Preview"
        case .finder: return "Finder"
        }
    }
}

struct PreferencesView: View {
    @State private var selectedTab: PreferencesTab = .general
    @AppStorage(PakScapePreferencesKey.textSize) private var textSize = 13.0

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                PreferencesTabButton(
                    title: "General",
                    systemImage: "slider.horizontal.3",
                    isSelected: selectedTab == .general
                ) {
                    selectedTab = .general
                }
                PreferencesTabButton(
                    title: "Archive",
                    systemImage: "shippingbox",
                    isSelected: selectedTab == .archive
                ) {
                    selectedTab = .archive
                }
                PreferencesTabButton(
                    title: "Preview",
                    systemImage: "eye",
                    isSelected: selectedTab == .preview
                ) {
                    selectedTab = .preview
                }
                PreferencesTabButton(
                    title: "Finder",
                    systemImage: "arrow.down.doc",
                    isSelected: selectedTab == .finder
                ) {
                    selectedTab = .finder
                }
            }
            .frame(maxWidth: .infinity)
            .padding(.top, 6)
            .padding(.bottom, 8)

            Group {
                switch selectedTab {
                case .general:
                    GeneralPreferencesView()
                case .archive:
                    ArchivePreferencesView()
                case .preview:
                    PreviewPreferencesView()
                case .finder:
                    FinderPreferencesView()
                }
            }
            .frame(width: 540)
            .frame(maxWidth: .infinity, alignment: .center)
        }
        .padding(.horizontal, 24)
        .padding(.bottom, 16)
        .frame(width: 620)
        .fixedSize(horizontal: false, vertical: true)
        .font(.system(size: textSize))
        .background(PreferencesWindowTitle(title: selectedTab.title))
        .tint(pakScapeAccent)
    }
}

private struct PreferencesWindowTitle: NSViewRepresentable {
    let title: String

    func makeNSView(context: Context) -> TitleView {
        TitleView(title: title)
    }

    func updateNSView(_ nsView: TitleView, context: Context) {
        nsView.update(title: title)
    }

    final class TitleView: NSView {
        private var title: String
        private weak var titleLabel: NSTextField?

        private static let accessoryIdentifier = NSUserInterfaceItemIdentifier(
            "PakScapeSettingsTitle"
        )

        init(title: String) {
            self.title = title
            super.init(frame: .zero)
        }

        required init?(coder: NSCoder) {
            fatalError("init(coder:) has not been implemented")
        }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            applyTitle()
        }

        func update(title: String) {
            self.title = title
            applyTitle()
        }

        private func applyTitle() {
            guard let window else { return }

            window.styleMask.insert(.fullSizeContentView)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.titlebarSeparatorStyle = .none

            if let titleLabel, titleLabel.window === window {
                titleLabel.stringValue = title
                positionTitleLabel(titleLabel)
                return
            }

            if let existing = window.titlebarAccessoryViewControllers.first(where: {
                $0.view.identifier == Self.accessoryIdentifier
            }), let existingLabel = existing.view.subviews.first as? NSTextField {
                existingLabel.stringValue = title
                positionTitleLabel(existingLabel)
                titleLabel = existingLabel
                return
            }

            let accessory = NSTitlebarAccessoryViewController()
            accessory.layoutAttribute = .top

            let container = NSView(
                frame: NSRect(x: 0, y: 0, width: window.frame.width, height: 24)
            )
            container.identifier = Self.accessoryIdentifier

            let label = NSTextField(labelWithString: title)
            label.alignment = .center
            label.font = NSFont.titleBarFont(ofSize: 0)
            label.textColor = .labelColor
            container.addSubview(label)
            positionTitleLabel(label)

            accessory.view = container
            window.addTitlebarAccessoryViewController(accessory)
            titleLabel = label

            DispatchQueue.main.async { [weak self, weak label] in
                guard let self, let label else { return }
                self.positionTitleLabel(label)
            }
        }

        private func positionTitleLabel(_ label: NSTextField) {
            guard let container = label.superview else { return }
            label.sizeToFit()
            let size = label.fittingSize
            let centerX: CGFloat
            if let window = container.window {
                container.frame.size.width = window.frame.width
                let containerOriginX = container.convert(.zero, to: nil).x
                centerX = window.frame.width / 2 - containerOriginX
            } else {
                centerX = container.bounds.midX
            }
            label.frame = NSRect(
                x: centerX - size.width / 2,
                y: (container.bounds.height - size.height) / 2,
                width: size.width,
                height: size.height
            )
        }
    }
}

private struct PreferencesTabButton: View {
    let title: String
    let systemImage: String
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: systemImage)
                    .font(.system(size: 24, weight: .regular))
                Text(title)
                    .font(.system(size: 12, weight: .medium))
            }
            .foregroundStyle(isSelected ? pakScapeAccent : .secondary)
            .frame(width: 85, height: 63)
            .background {
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .fill(isSelected ? Color.black.opacity(0.18) : .clear)
                    .overlay {
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .stroke(
                                isSelected ? Color.primary.opacity(0.14) : .clear,
                                lineWidth: 1
                            )
                    }
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

private struct GeneralPreferencesView: View {
    @AppStorage(PakScapePreferencesKey.appearance) private var appearance = "automatic"
    @AppStorage(PakScapePreferencesKey.defaultView) private var defaultView = "list"
    @AppStorage(PakScapePreferencesKey.defaultSort) private var defaultSort = "name"
    @AppStorage(PakScapePreferencesKey.defaultSortAscending) private var defaultSortAscending = true
    @AppStorage(PakScapePreferencesKey.textSize) private var textSize = 13.0

    var body: some View {
        PreferencesPage {
            PreferencesPickerRow("Appearance", selection: $appearance, labelAlignment: .trailing) {
                Text("Automatic").tag("automatic")
                Text("Light").tag("light")
                Text("Dark").tag("dark")
            }

            PreferencesPickerRow("Default view", selection: $defaultView, labelAlignment: .trailing) {
                Text("List").tag("list")
                Text("Large Icons").tag("icons")
            }

            PreferencesPickerRow("Default sort", selection: $defaultSort, labelAlignment: .trailing) {
                Text("Name").tag("name")
                Text("Type").tag("type")
                Text("Size").tag("size")
            }

            PreferencesPickerRow("Sort order", selection: $defaultSortAscending, labelAlignment: .trailing) {
                Text("Ascending").tag(true)
                Text("Descending").tag(false)
            }

            PreferencesTextSizeRow(value: $textSize)

            PreferencesNote("The default view applies to newly opened archive windows. PakScape follows the system appearance unless you choose a specific mode.")
        }
    }
}

private struct ArchivePreferencesView: View {
    @AppStorage(PakScapePreferencesKey.confirmDeletion) private var confirmDeletion = true
    @AppStorage(PakScapePreferencesKey.confirmOverwrite) private var confirmOverwrite = true
    @AppStorage(PakScapePreferencesKey.backupBeforeSave) private var backupBeforeSave = false

    var body: some View {
        PreferencesPage {
            Toggle("Confirm before deleting archive items", isOn: $confirmDeletion)
            Toggle("Confirm before overwriting exported files", isOn: $confirmOverwrite)
            Toggle("Create a backup before saving an archive", isOn: $backupBeforeSave)
        }
    }
}

private struct PreviewPreferencesView: View {
    @AppStorage(PakScapePreferencesKey.quickLookOnSelection) private var quickLookOnSelection = false
    @AppStorage(PakScapePreferencesKey.animateModels) private var animateModels = true
    @AppStorage(PakScapePreferencesKey.showBspMarkers) private var showBspMarkers = false

    var body: some View {
        PreferencesPage {
            Toggle("Open Quick Preview when selecting an item", isOn: $quickLookOnSelection)
            Toggle("Animate models when they open", isOn: $animateModels)
            Toggle("Show item markers on BSP level previews", isOn: $showBspMarkers)
        }
    }
}

private struct FinderPreferencesView: View {
    @AppStorage(FinderPreferencesKey.actionsEnabled) private var actionsEnabled = true
    @State private var isAssociated = false

    var body: some View {
        PreferencesPage {
            HStack {
                Text("PAK/PK3 file association")
                Spacer(minLength: 18)
                Button(isAssociated ? "Associated" : "Associate with PakScape") {
                    PakFileAssociationManager.associate()
                    isAssociated = PakFileAssociationManager.isAssociated
                }
                .disabled(isAssociated)
            }

            Toggle("Enable PakScape Finder services", isOn: $actionsEnabled)
                .onChange(of: actionsEnabled) { _, newValue in
                    FinderServiceManager.shared.updateRegistration(isEnabled: newValue)
                }

            PreferencesNote("Use Finder's Services menu to extract selected PAK/PK3 archives or pack selected folders. PakScape will ask where to save the result.")

            HStack {
                Spacer()
                Button("Manage Services…") {
                    NSWorkspace.shared.open(
                        URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension?Services")!
                    )
                }
                .controlSize(.large)
            }
        }
        .onAppear {
            isAssociated = PakFileAssociationManager.isAssociated
        }
    }
}

private struct PreferencesPage<Content: View>: View {
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            content()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 16)
        .padding(.top, 8)
        .padding(.bottom, 8)
        .toggleStyle(.checkbox)
    }
}

private struct PreferencesPickerRow<SelectionValue: Hashable, Content: View>: View {
    let title: String
    let labelAlignment: Alignment
    @Binding var selection: SelectionValue
    @ViewBuilder let content: () -> Content

    init(
        _ title: String,
        selection: Binding<SelectionValue>,
        labelAlignment: Alignment = .leading,
        @ViewBuilder content: @escaping () -> Content
    ) {
        self.title = title
        self.labelAlignment = labelAlignment
        self._selection = selection
        self.content = content
    }

    var body: some View {
        HStack(spacing: 18) {
            Text(title)
                .frame(width: 150, alignment: labelAlignment)
            Picker(title, selection: $selection, content: content)
                .labelsHidden()
                .frame(width: 230, alignment: .leading)
        }
        .frame(maxWidth: .infinity, alignment: .center)
        .padding(.vertical, 4)
    }
}

private struct PreferencesTextSizeRow: View {
    @Binding var value: Double

    private let range = 11.0...18.0

    var body: some View {
        HStack(alignment: .top, spacing: 18) {
            Text("Text size")
                .frame(width: 150, alignment: .trailing)

            VStack(spacing: 4) {
                HStack(spacing: 8) {
                    Text("A")
                        .font(.system(size: 12))

                    Slider(value: $value, in: range, step: 1)

                    Text("A")
                        .font(.system(size: 20))
                }

                HStack(spacing: 0) {
                    ForEach(0..<8, id: \.self) { _ in
                        Circle()
                            .fill(Color.secondary.opacity(0.55))
                            .frame(width: 3, height: 3)
                            .frame(maxWidth: .infinity)
                    }
                }
                .padding(.horizontal, 25)

                Text(value == 13 ? "Default" : "\(Int(value)) pt")
                    .font(.callout.weight(.medium))
                    .foregroundStyle(.secondary)
            }
            .frame(width: 230)
        }
        .frame(maxWidth: .infinity, alignment: .center)
        .padding(.vertical, 4)
    }
}

private struct PreferencesNote: View {
    let text: String

    init(_ text: String) {
        self.text = text
    }

    var body: some View {
        Text(text)
            .font(.footnote)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 14)
            .padding(.vertical, 12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(Color.primary.opacity(0.08))
            }
    }
}
