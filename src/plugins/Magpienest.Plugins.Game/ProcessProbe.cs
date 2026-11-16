using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Magpienest.Plugins.Game;

/// <summary>
/// The real <see cref="IProcessProbe"/>. Program paths come from <c>QueryFullProcessImageName</c>, which also works
/// for elevated processes where <c>Process.MainModule</c> throws; processes that cannot be opened are skipped.
/// </summary>
internal sealed class ProcessProbe : IProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int StillActive = 259;
    private const int ErrorAccessDenied = 5;

    private readonly int _sessionId = CurrentSessionId();

    /// <inheritdoc />
    public IReadOnlyList<(int Pid, string Path)> Snapshot()
    {
        var result = new List<(int, string)>();
        var buffer = new char[32768];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != _sessionId)
                    {
                        continue;
                    }

                    if (PathOf(process.Id, buffer) is { } path)
                    {
                        result.Add((process.Id, path));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Exited meanwhile, or not inspectable.
                }
            }
        }

        return result;
    }

    /// <inheritdoc />
    public bool IsRunning(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            // A process we may not even query still exists; a missing one reports "invalid parameter".
            return Marshal.GetLastWin32Error() == ErrorAccessDenied;
        }

        try
        {
            return GetExitCodeProcess(handle, out var code) && code == StillActive;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string? PathOf(int pid, char[] buffer)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = buffer.Length;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static int CurrentSessionId()
    {
        using var current = Process.GetCurrentProcess();
        return current.SessionId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, [In, Out] char[] exeName, ref int size);
}
