using System;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace PakStudio.App.Helpers;

/// <summary>
/// WPF only ships the plain double arrow for horizontal resizing. Explorer and the
/// column headers everywhere else use the split bar instead - a pair of rails with an
/// arrow leaning off each side - and that artwork lives in WinForms, so it is borrowed
/// by handle rather than redrawn.
/// </summary>
public static class SplitCursors
{
    private static readonly Lazy<Cursor> LazyVerticalSplit = new(CreateVerticalSplit);

    /// <summary>The split bar shown over a seam that resizes columns.</summary>
    public static Cursor VerticalSplit => LazyVerticalSplit.Value;

    private static Cursor CreateVerticalSplit()
    {
        try
        {
            // WinForms caches its stock cursors for the life of the process, so the
            // handle outlives every wrapper we hand out and must not be closed here.
            var handle = System.Windows.Forms.Cursors.VSplit.Handle;
            return CursorInteropHelper.Create(new SafeFileHandle(handle, ownsHandle: false));
        }
        catch (Exception)
        {
            return Cursors.SizeWE;
        }
    }
}
