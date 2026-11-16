using System.Runtime.InteropServices;
using System.Text;

namespace Magpienest.Plugins.Game;

/// <summary>What a Windows shortcut (.lnk) starts.</summary>
/// <param name="TargetPath">The program the shortcut runs.</param>
/// <param name="Arguments">Its command-line arguments (may be empty).</param>
/// <param name="WorkingDirectory">Its working folder (may be empty).</param>
internal sealed record ShortcutTarget(string TargetPath, string Arguments, string WorkingDirectory);

/// <summary>Reads .lnk files through the shell's IShellLinkW / IPersistFile COM interfaces.</summary>
internal static class ShortcutResolver
{
    private const int MaxPath = 1024;

    /// <summary>True for a path that ends in ".lnk".</summary>
    public static bool IsShortcut(string path) => path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a shortcut without running it or letting the shell search for a moved target.
    /// Returns null when the file is not a shortcut or points to nothing a program can be started from.
    /// </summary>
    public static ShortcutTarget? Resolve(string lnkPath)
    {
        if (!File.Exists(lnkPath))
        {
            return null;
        }

        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(lnkPath, 0);
            var shell = (IShellLinkW)link;
            var target = new StringBuilder(MaxPath);
            var arguments = new StringBuilder(MaxPath);
            var folder = new StringBuilder(MaxPath);
            shell.GetPath(target, target.Capacity, nint.Zero, 0);
            shell.GetArguments(arguments, arguments.Capacity);
            shell.GetWorkingDirectory(folder, folder.Capacity);

            var path = Environment.ExpandEnvironmentVariables(target.ToString());
            return path.Length == 0
                ? null
                : new ShortcutTarget(path, arguments.ToString(), Environment.ExpandEnvironmentVariables(folder.ToString()));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            return null;
        }
        finally
        {
            if (link is not null)
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
    }
}

[ComImport, Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLink
{
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int maxChars, nint findData, uint flags);

    void GetIDList(out nint idList);

    void SetIDList(nint idList);

    void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxChars);

    void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

    void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxChars);

    void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

    void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxChars);

    void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

    void GetHotkey(out ushort hotkey);

    void SetHotkey(ushort hotkey);

    void GetShowCmd(out int showCommand);

    void SetShowCmd(int showCommand);

    void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxChars, out int iconIndex);

    void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

    void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);

    void Resolve(nint window, uint flags);

    void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
internal interface IPersistFile
{
    void GetClassID(out Guid classId);

    [PreserveSig]
    int IsDirty();

    void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

    void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

    void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

    void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
}
