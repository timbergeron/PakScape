import SwiftUI
import AppKit

final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        removeBlankFileMenuItem()
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
        DocumentGroup(newDocument: PakDocument()) { file in
            ContentView(document: file.$document, fileURL: file.fileURL)
        }
        .commands {
            PakAboutCommands()
            PakOpenCommands()
            PakNewCommands()
            PakSaveCommands()
            PakEditCommands()
            PakGoCommands()
            PakViewCommands()
        }
    }
}

struct PakSaveCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands
    
    var body: some Commands {
        CommandGroup(replacing: .saveItem) {
            Button {
                pakCommands?.save()
            } label: {
                Label("Save", systemImage: "square.and.arrow.down")
            }
            .keyboardShortcut("S")
            .disabled(!(pakCommands?.canSave ?? false))
        }
        CommandGroup(after: .saveItem) {
            Button {
                pakCommands?.saveAs()
            } label: {
                Label("Save As…", systemImage: "square.and.arrow.down.on.square")
            }
            .keyboardShortcut("S", modifiers: [.command, .shift])
            .disabled(pakCommands == nil)

            Divider()

            Button {
                NSApp.sendAction(#selector(NSWindow.performClose(_:)), to: nil, from: nil)
            } label: {
                Label("Close", systemImage: "xmark.circle")
            }
            .keyboardShortcut("W")

            Button {
                NSApp.windows.forEach { $0.performClose(nil) }
            } label: {
                Label("Close All", systemImage: "xmark.circle.fill")
            }
            .keyboardShortcut("W", modifiers: [.command, .option])
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
            .keyboardShortcut("X")
            .disabled(!(pakCommands?.canCutCopy ?? false))

            Button("Copy") {
                pakCommands?.copy()
            }
            .keyboardShortcut("C")
            .disabled(!(pakCommands?.canCutCopy ?? false))

            Button("Paste") {
                _ = pakCommands?.paste()
            }
            .keyboardShortcut("V")
            .disabled(!(pakCommands?.canPaste ?? false))

            Button("Select All") {
                pakCommands?.selectAll()
            }
            .keyboardShortcut("A")
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
    var body: some Commands {
        CommandGroup(after: .newItem) {
            Button {
                NSApp.sendAction(#selector(NSDocumentController.openDocument(_:)), to: nil, from: nil)
            } label: {
                Label("Open…", systemImage: "folder")
            }
            .keyboardShortcut("O")
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
            .keyboardShortcut("N")

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
        let parentWindow = NSApp.keyWindow ?? NSApp.mainWindow

        if window == nil {
            window = makeWindow()
        }

        guard let window else { return }
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        DispatchQueue.main.async { [weak self, weak window, weak parentWindow] in
            guard let self, let window else { return }
            self.centerWindow(window, relativeTo: parentWindow)
        }
    }

    private func makeWindow() -> NSWindow {
        let hostingController = NSHostingController(rootView: AboutView())
        let window = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 440, height: 360),
            styleMask: [.titled, .closable],
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

    private func centerWindow(_ window: NSWindow, relativeTo parent: NSWindow?) {
        let frame = window.frame

        if let parent {
            let parentFrame = parent.frame
            let origin = NSPoint(
                x: parentFrame.midX - frame.size.width / 2,
                y: parentFrame.midY - frame.size.height / 2
            )
            window.setFrame(NSRect(origin: origin, size: frame.size), display: true)
            return
        }

        let targetScreen = window.screen ?? NSScreen.main

        guard let screen = targetScreen else {
            window.center()
            return
        }

        let visibleFrame = screen.visibleFrame
        let origin = NSPoint(
            x: visibleFrame.midX - frame.size.width / 2,
            y: visibleFrame.midY - frame.size.height / 2
        )

        window.setFrame(NSRect(origin: origin, size: frame.size), display: true)
    }
}

private struct AboutView: View {
    private let urlString = "https://github.com/timbergeron/PakScape"
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

            LinkButtonRepresentable(title: displayString, url: URL(string: urlString)!)
                .fixedSize()
        }
        .padding(.vertical, 24)
        .padding(.horizontal, 28)
        .frame(minWidth: 380, idealWidth: 420)
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
