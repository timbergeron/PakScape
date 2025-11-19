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
            PakNewCommands()
            PakSaveCommands()
            // "Open" is handled by DocumentGroup automatically
        }
    }
}

struct PakSaveCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands
    
    var body: some Commands {
        CommandGroup(replacing: .saveItem) {
            Button("Save") {
                pakCommands?.save()
            }
            .keyboardShortcut("S")
            .disabled(!(pakCommands?.canSave ?? false))
        }
        CommandGroup(after: .saveItem) {
            Button("Save As…") {
                pakCommands?.saveAs()
            }
            .keyboardShortcut("S", modifiers: [.command, .shift])
            .disabled(pakCommands == nil)

            Button("Delete File", role: .destructive) {
                pakCommands?.deleteFile()
            }
            .keyboardShortcut(.delete, modifiers: [.command])
            .disabled(!(pakCommands?.canDeleteFile ?? false))
            
            Divider()

            Button("Close") {
                NSApp.sendAction(#selector(NSWindow.performClose(_:)), to: nil, from: nil)
            }
            .keyboardShortcut("W")

            Button("Close All") {
                NSApp.windows.forEach { $0.performClose(nil) }
            }
            .keyboardShortcut("W", modifiers: [.command, .option])
        }
    }
}

struct PakNewCommands: Commands {
    @FocusedValue(\.pakCommands) private var pakCommands

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Pak") {
                NSApp.sendAction(#selector(NSDocumentController.newDocument(_:)), to: nil, from: nil)
            }
            .keyboardShortcut("N")

            Button("New Folder") {
                pakCommands?.newFolder()
            }
            .disabled(!(pakCommands?.canNewFolder ?? false))
            Button("Add File(s)…") {
                pakCommands?.addFiles()
            }
            .disabled(!(pakCommands?.canAddFiles ?? false))
        }
    }
}
