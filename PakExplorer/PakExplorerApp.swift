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
struct PakExplorerApp: App {
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
            PakNewCommands()
            PakSaveCommands()
            PakViewCommands()
            // "Open" is handled by DocumentGroup automatically
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

            Button(role: .destructive) {
                pakCommands?.deleteFile()
            } label: {
                Label("Delete File", systemImage: "trash")
            }
            .keyboardShortcut(.delete, modifiers: [.command])
            .disabled(!(pakCommands?.canDeleteFile ?? false))
            
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
    var body: some Commands {
        CommandGroup(replacing: .appInfo) {
            Button("About PakExplorer") {
                showAbout()
            }
        }
    }

    private func showAbout() {
        let alert = NSAlert()
        alert.messageText = "PakExplorer"
        alert.informativeText = "Simple Quake `.pak` & `.pk3` explorer inspired by PakScape and originally developed by Peter Engström."
        alert.alertStyle = .informational
        if let icon = NSApp.applicationIconImage {
            alert.icon = icon
        }

        let urlString = "https://github.com/timbergeron/PakExplorer"
        let displayString = "github.com/timbergeron/PakExplorer"
        let linkButton = LinkButton(title: displayString, url: URL(string: urlString)!)
        linkButton.sizeToFit()
        alert.accessoryView = linkButton
        alert.runModal()
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
