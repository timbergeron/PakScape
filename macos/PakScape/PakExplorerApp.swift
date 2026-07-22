import SwiftUI
import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let maximumVisibleDockRecents = 5

    func applicationDidFinishLaunching(_ notification: Notification) {
        FinderServiceManager.shared.applyInitialSettings()
        removeBlankFileMenuItem()
    }

    func applicationWillTerminate(_ notification: Notification) {
        PakQuickLook.shared.hide()
    }

    func applicationDockMenu(_ sender: NSApplication) -> NSMenu? {
        let recentURLs = NSDocumentController.shared.recentDocumentURLs
        let menu = NSMenu()
        menu.autoenablesItems = false

        let heading = NSMenuItem(title: "Recent", action: nil, keyEquivalent: "")
        heading.isEnabled = false
        menu.addItem(heading)

        for url in recentURLs.prefix(maximumVisibleDockRecents) {
            menu.addItem(recentMenuItem(for: url))
        }

        let remainingURLs = recentURLs.dropFirst(maximumVisibleDockRecents)
        if !remainingURLs.isEmpty {
            let moreMenu = NSMenu(title: "More")
            moreMenu.autoenablesItems = false
            for url in remainingURLs {
                moreMenu.addItem(recentMenuItem(for: url))
            }

            let moreItem = NSMenuItem(title: "More", action: nil, keyEquivalent: "")
            moreItem.submenu = moreMenu
            moreItem.isEnabled = true
            menu.addItem(moreItem)
        }

        return menu
    }

    private func recentMenuItem(for url: URL) -> NSMenuItem {
        let displayName = FileManager.default.displayName(atPath: url.path)
        let title = displayName.isEmpty ? url.lastPathComponent : displayName
        let item = NSMenuItem(
            title: title,
            action: #selector(openRecentDocument(_:)),
            keyEquivalent: ""
        )
        item.target = self
        item.representedObject = url
        item.toolTip = url.path
        item.isEnabled = true
        return item
    }

    @objc private func openRecentDocument(_ sender: NSMenuItem) {
        guard let url = sender.representedObject as? URL else { return }
        NSDocumentController.shared.openDocument(withContentsOf: url, display: true) { _, _, error in
            if let error {
                NSApp.presentError(error)
            }
        }
    }

    private func removeBlankFileMenuItem() {
        DispatchQueue.main.async {
            guard let fileMenu = NSApp.mainMenu?.item(withTitle: "File")?.submenu else { return }
            let blankItems = fileMenu.items.filter { item in
                let title = item.title.trimmingCharacters(in: .whitespacesAndNewlines)
                return title.isEmpty || title == "NSMenuItem"
            }
            for item in blankItems {
                fileMenu.removeItem(item)
            }
        }
    }
}

@main
struct PakScapeApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    init() {
        if #available(macOS 10.12, *) {
            NSWindow.allowsAutomaticWindowTabbing = false
        }
    }
    
    var body: some Scene {
        DocumentGroup(newDocument: { PakDocument() }) { file in
            ContentView(
                document: file.document,
                fileURL: file.fileURL,
                isEditable: file.isEditable
            )
                .id(ObjectIdentifier(file.document))
        }
        .commands {
            PakAboutCommands()
            PakOpenCommands()
            PakNewCommands()
            PakEditCommands()
            PakGoCommands()
            PakViewCommands()
        }

        Settings {
            PreferencesView()
        }
    }
}

struct PakEditCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandGroup(replacing: .pasteboard) {
            Button("Cut") {
                pakCommands?.cut()
            }
            .keyboardShortcut("x")
            .disabled(!(pakCommands?.canCut ?? false))

            Button("Copy") {
                pakCommands?.copy()
            }
            .keyboardShortcut("c")
            .disabled(!(pakCommands?.canCopy ?? false))

            Button("Paste") {
                _ = pakCommands?.paste()
            }
            .keyboardShortcut("v")
            .disabled(!(pakCommands?.canPaste ?? false))

            Button("Select All") {
                pakCommands?.selectAll()
            }
            .keyboardShortcut("a")
            .disabled(!(pakCommands?.canSelectAll ?? false))

            Button("Rename…") {
                pakCommands?.rename()
            }
            .disabled(!(pakCommands?.canRename ?? false))

            Divider()

            Button(role: .destructive) {
                pakCommands?.deleteFile()
            } label: {
                Text("Delete")
            }
            .keyboardShortcut(.delete, modifiers: [.command])
            .disabled(!(pakCommands?.canDeleteFile ?? false))
        }
    }
}

struct PakOpenCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandGroup(after: .newItem) {
            Button {
                NSApp.sendAction(#selector(NSDocumentController.openDocument(_:)), to: nil, from: nil)
            } label: {
                Label("Open…", systemImage: "folder")
            }
            .keyboardShortcut("o")

            Divider()

            Button("Get Info") {
                let handledByNativeView = NSApp.sendAction(
                    NSSelectorFromString("showPakItemInfo:"),
                    to: nil,
                    from: nil
                )
                if !handledByNativeView {
                    pakCommands?.getInfo()
                }
            }
            .keyboardShortcut("i", modifiers: [.command])
            .disabled(!(pakCommands?.canGetInfo ?? false))
        }

        CommandGroup(after: .saveItem) {
            Divider()

            Button {
                pakCommands?.openPakFolder()
            } label: {
                Label("Open PAK Folder", systemImage: "folder")
            }
            .disabled(!(pakCommands?.canOpenPakFolder ?? false))
        }
    }
}

struct PakNewCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button {
                NSApp.sendAction(#selector(NSDocumentController.newDocument(_:)), to: nil, from: nil)
            } label: {
                Label("New Pak", systemImage: "doc")
            }
            .keyboardShortcut("n")

            Button {
                pakCommands?.newFolder()
            } label: {
                Label("New Folder", systemImage: "folder.badge.plus")
            }
            .disabled(!(pakCommands?.canNewFolder ?? false))

            Button {
                pakCommands?.addFiles()
            } label: {
                Label("Add File(s)…", systemImage: "doc.badge.plus")
            }
            .disabled(!(pakCommands?.canAddFiles ?? false))
        }
    }
}

struct PakAboutCommands: Commands {
    private let aboutPresenter = AboutWindowPresenter.shared

    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("About PakScape") {
                aboutPresenter.show()
            }
        }
    }
}

struct PakGoCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandMenu("Go") {
            Button("Enclosing Folder") {
                pakCommands?.enclosingFolder()
            }
            .keyboardShortcut(.upArrow, modifiers: [.command])
            .disabled(!(pakCommands?.canEnclosingFolder ?? false))

            Button("Open Selection") {
                pakCommands?.openSelection()
            }
            .keyboardShortcut(.downArrow, modifiers: [.command])
            .disabled(!(pakCommands?.canOpenSelection ?? false))
        }
    }
}

struct PakViewCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandMenu("View") {
            Button("Quick Look") {
                pakCommands?.quickLook()
            }
            .disabled(!(pakCommands?.canQuickLook ?? false))

            Divider()

            Button("Bigger Icons") {
                pakCommands?.zoomInIcons()
            }
            .keyboardShortcut("+", modifiers: [.command])
            .disabled(!(pakCommands?.canZoomInIcons ?? false))

            Button("Smaller Icons") {
                pakCommands?.zoomOutIcons()
            }
            .keyboardShortcut("-", modifiers: [.command])
            .disabled(!(pakCommands?.canZoomOutIcons ?? false))
        }
    }
}

private final class AboutWindowPresenter {
    static let shared = AboutWindowPresenter()
    private var window: NSWindow?

    func show() {
        let targetScreen = (NSApp.keyWindow ?? NSApp.mainWindow)?.screen ?? NSScreen.main

        if window == nil {
            window = makeWindow()
        }

        guard let window else { return }
        centerWindow(window, on: targetScreen)
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        DispatchQueue.main.async { [weak self, weak window] in
            guard let self, let window else { return }
            self.centerWindow(window, on: targetScreen)
        }
    }

    private func makeWindow() -> NSWindow {
        let hostingController = NSHostingController(rootView: AboutView())
        let window = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 440, height: 360),
            styleMask: [.titled, .closable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )

        window.title = "About PakScape"
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.standardWindowButton(.miniaturizeButton)?.isHidden = true
        window.standardWindowButton(.zoomButton)?.isHidden = true
        window.isReleasedWhenClosed = false
        window.contentViewController = hostingController

        return window
    }

    private func centerWindow(_ window: NSWindow, on screen: NSScreen?) {
        guard let screen else {
            window.center()
            return
        }

        let frame = window.frame
        let visibleFrame = screen.visibleFrame
        let origin = NSPoint(
            x: visibleFrame.midX - frame.size.width / 2,
            y: visibleFrame.midY - frame.size.height / 2
        )

        window.setFrame(NSRect(origin: origin, size: frame.size), display: true)
    }
}

private struct AboutView: View {
    private let projectURL = URL(string: "https://github.com/timbergeron/PakScape")
    private let displayString = "github.com/timbergeron/PakScape"
    private var versionText: String? {
        let bundle = Bundle.main
        guard let version = bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String else {
            return nil
        }
        return "Version \(version)"
    }

    var body: some View {
        VStack(spacing: 12) {
            if let icon = NSApp.applicationIconImage {
                Image(nsImage: icon)
                    .resizable()
                    .frame(width: 120, height: 120)
                    .cornerRadius(18)
                    .shadow(radius: 2)
            }

            Text("PakScape")
                .font(.title2.weight(.semibold))

            Text("Simple Quake `.pak` & `.pk3` explorer inspired by PakScape and originally developed by Peter Engström.")
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .foregroundStyle(.secondary)
                .font(.body)
                .frame(maxWidth: 360)

            if let versionText {
                Text(versionText)
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }

            if let projectURL {
                LinkButtonRepresentable(title: displayString, url: projectURL)
                    .fixedSize()
            }
        }
        .padding(.vertical, 24)
        .padding(.horizontal, 28)
        .frame(minWidth: 380, idealWidth: 420)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .center)
        .ignoresSafeArea(.container, edges: .top)
    }
}

private struct LinkButtonRepresentable: NSViewRepresentable {
    let title: String
    let url: URL

    func makeNSView(context: Context) -> NSButton {
        LinkButton(title: title, url: url)
    }

    func updateNSView(_ nsView: NSButton, context: Context) {}
}

private final class LinkButton: NSButton {
    private let url: URL

    init(title: String, url: URL) {
        self.url = url
        super.init(frame: .zero)
        isBordered = false
        bezelStyle = .inline
        font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        focusRingType = .none

        let color = NSColor(red: 54/255, green: 197/255, blue: 73/255, alpha: 1)
        contentTintColor = color
        let attributed = NSAttributedString(
            string: title,
            attributes: [
                .foregroundColor: color,
                .underlineStyle: NSUnderlineStyle.single.rawValue
            ]
        )
        attributedTitle = attributed
        target = self
        action = #selector(openLink)
        setButtonType(.momentaryChange)
        alignment = .center
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    @objc private func openLink() {
        NSWorkspace.shared.open(url)
    }

    override func resetCursorRects() {
        discardCursorRects()
        addCursorRect(bounds, cursor: .pointingHand)
    }
}
