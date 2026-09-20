// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PaulTechGuy.CN.App;

/// <summary>
/// Puts Chronora in Explorer's "Send to" menu, and takes it out again.
///
/// This is most of what a shell extension would buy, for almost none of the cost: right-
/// click a folder of photos, Send to, Chronora, and the files are loaded. The full
/// IExplorerCommand menu needs packaging, registration and an in-process DLL that Explorer
/// loads; a shortcut in a folder needs a file.
///
/// The app offers it rather than the installer doing it silently, because adding an entry
/// to somebody's shell menus without asking is the kind of thing installers get disliked
/// for - and it is reversible from the same place.
/// </summary>
internal static class SendToShortcut
{
    private const string ShortcutName = "Chronora.lnk";

    /// <summary>The per-user Send To folder. Not a path to guess - it is a known folder.</summary>
    private static string Folder => Environment.GetFolderPath(Environment.SpecialFolder.SendTo);

    private static string Path => System.IO.Path.Combine(Folder, ShortcutName);

    public static bool Exists => File.Exists(Path);

    /// <summary>
    /// Writes the shortcut, pointing at the running executable.
    ///
    /// The running one on purpose: whichever copy the person actually launched is the one
    /// they mean, which keeps a portable copy and an installed copy honest about which is
    /// which.
    /// </summary>
    public static bool Create()
    {
        try
        {
            string target = Environment.ProcessPath
                ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Chronora.exe");

            // Through object on purpose. The coclass does not declare the interface, so a
            // direct cast will not compile; going via object defers it to a runtime
            // QueryInterface, which is what a COM cast actually is.
            object created = new ShellLink();
            var link = (IShellLinkW)created;

            link.SetPath(target);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(target) ?? AppContext.BaseDirectory);
            link.SetDescription("Set dates on these files with Chronora");

            ((IPersistFile)link).Save(Path, fRemember: true);

            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or InvalidCastException)
        {
            Serilog.Log.Warning(ex, "Could not create the Send To shortcut.");

            return false;
        }
    }

    public static bool Remove()
    {
        try
        {
            if (Exists)
            {
                File.Delete(Path);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Could not remove the Send To shortcut.");

            return false;
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    /// <summary>
    /// Only the four methods used are given real signatures. The rest are declared so the
    /// vtable lines up and are never called - getting the ORDER right is what matters in a
    /// COM interface, not having every method typed.
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, nint findData, int flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int maxArgs);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder icon, int maxIcon, out int index);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);

        void Resolve(nint hwnd, int flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
