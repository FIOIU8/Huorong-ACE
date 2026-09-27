using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace HuorongAce.Native.Win32;

/// <summary>
/// Gives the unpackaged app the shell identity it needs in order to raise toast
/// notifications.
/// </summary>
/// <remarks>
/// <para>
/// This is the fix for "notifications never appear". The WinRT toast API looks
/// like it works for an unpackaged desktop app — <c>CreateToastNotifier</c>
/// returns a notifier and <c>Show</c> does not throw — but Windows silently
/// drops the notification unless the app has a real shell identity. Microsoft's
/// own documentation states it plainly: without a shortcut carrying an
/// AppUserModelID installed in the Start menu, a desktop app cannot raise a
/// toast.
/// </para>
/// <para>
/// Three things together make the identity complete:
/// </para>
/// <list type="number">
///   <item>A Start-menu shortcut whose <c>System.AppUserModel.ID</c> is our AUMID.</item>
///   <item>A registry entry giving that AUMID a display name and an icon.</item>
///   <item><c>SetCurrentProcessExplicitAppUserModelID</c>, so the running
///         process is attributed to the same identity.</item>
/// </list>
/// <para>
/// The shortcut is the only part that requires COM interop; it is created at
/// start-up and is cheap enough to redo whenever the executable path changes.
/// </para>
/// </remarks>
internal static class AppIdentity
{
    private const string Aumid = "com.huorong.ace";
    private const string DisplayName = "火绒ACE";

    /// <summary>Registry key where Windows looks up AUMID display details.</summary>
    private const string AumidRegistryPath = @"SOFTWARE\Classes\AppUserModelId\" + Aumid;

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static readonly PropertyKey PkeyAppUserModelId = new(
        new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>
    /// True when this run created the Start-menu shortcut, meaning the shell
    /// has not indexed the AUMID yet.
    /// </summary>
    /// <remarks>
    /// A toast sent in the same instant the shortcut appears is dropped: the
    /// shell resolves an AUMID to a shortcut through an index it updates
    /// asynchronously. Callers can wait briefly before sending the first
    /// notification when this is true.
    /// </remarks>
    public static bool ShortcutCreatedThisRun { get; private set; }

    /// <summary>
    /// Registers the identity. Safe to call repeatedly; returns false when the
    /// registration could not be completed.
    /// </summary>
    public static bool Register(string? iconPath = null)
    {
        try
        {
            SetCurrentProcessExplicitAppUserModelID(Aumid);
            WriteRegistryEntry(iconPath);
            ShortcutCreatedThisRun = TryCreateShortcut();
            return true;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string AppUserModelId => Aumid;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

    private static void WriteRegistryEntry(string? iconPath)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AumidRegistryPath);
        if (key is null)
        {
            return;
        }

        key.SetValue("DisplayName", DisplayName);

        // Format is ARGB hex without a leading '#', e.g. "FFC00000".
        key.SetValue("IconBackgroundColor", "FFC00000");

        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            key.SetValue("IconUri", iconPath);
        }
    }

    private static bool TryCreateShortcut()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData))
        {
            return false;
        }

        var linkPath = Path.Combine(
            appData, "Microsoft", "Windows", "Start Menu", "Programs", DisplayName + ".lnk");

        // Once the shortcut exists, leave it alone. Rewriting it on every start
        // would keep the shell's AUMID index churning, which is the same race
        // this property exists to describe.
        if (File.Exists(linkPath))
        {
            return false;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exePath);
            link.SetDescription(DisplayName);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
            link.SetShowCmd(7); // SW_SHOWMINNOACTIVE

            // The shell link object also implements IPropertyStore; the runtime
            // callable wrapper resolves it through QueryInterface.
            if (link is IPropertyStore store)
            {
                SetAppUserModelId(store);
            }

            ((IPersistFile)link).Save(linkPath, true);
            return true;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static void SetAppUserModelId(IPropertyStore store)
    {
        var value = new PropVariant(Aumid);
        try
        {
            var key = PkeyAppUserModelId;
            store.SetValue(ref key, ref value);
            store.Commit();
        }
        finally
        {
            value.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(Guid formatId, uint propertyId)
        {
            FormatId = formatId;
            PropertyId = propertyId;
        }
    }

    /// <summary>
    /// The subset of <c>PROPVARIANT</c> needed to carry a single string.
    /// </summary>
    /// <remarks>
    /// Only the <c>VT_LPWSTR</c> case is handled. The struct is padded to the
    /// full 24-byte size of <c>PROPVARIANT</c> on 64-bit so the COM call sees
    /// the layout it expects.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant : IDisposable
    {
        private const ushort VtLpwstr = 31;

        private ushort _vt;
        private ushort _reserved1;
        private ushort _reserved2;
        private ushort _reserved3;
        private nint _pointer;
        private nint _padding1;
        private nint _padding2;

        public PropVariant(string value)
        {
            _vt = VtLpwstr;
            _reserved1 = 0;
            _reserved2 = 0;
            _reserved3 = 0;
            _pointer = Marshal.StringToCoTaskMemUni(value);
            _padding1 = 0;
            _padding2 = 0;
        }

        public void Dispose()
        {
            if (_pointer != 0)
            {
                Marshal.ZeroFreeCoTaskMemUnicode(_pointer);
                _pointer = 0;
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, out nint pfd, uint fFlags);
        void GetIdList(out nint ppidl);
        void SetIdList(nint pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint propertyCount);
        void GetAt(uint propertyIndex, out PropertyKey key);
        void GetValue(ref PropertyKey key, out nint value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }
}
