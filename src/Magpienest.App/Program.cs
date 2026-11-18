using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Magpienest.Core;
using Magpienest.Core.Update;
using Magpienest.Sdk;
using Magpienest.Sdk.Shell;

namespace Magpienest.App;

/// <summary>Host: resolves the data folder, enforces a single instance, starts WPF and the kernel. No features.</summary>
internal static class Program
{
    private const string NoWindowMessage =
        "Magpienest could not open its main window. See the log:\n{0}\n\nIf a plugin you installed causes this, start Magpienest with --safe-mode.\n\n"
        + "Magpienest 無法開啟主視窗，請查看記錄檔。\n如果是您安裝的外掛造成的，請加上 --safe-mode 參數啟動 Magpienest。";

    [STAThread]
    private static int Main(string[] args)
    {
        var appDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var (dataDirectoryArg, waitPid, update, passThrough) = ParseArgs(args);
        if (update.Target is not null)
        {
            return ApplyUpdate(appDirectory, update.Target, update.ResultFile, waitPid, dataDirectoryArg, passThrough);
        }

        if (waitPid is not null)
        {
            WaitForExit(waitPid.Value);
        }

        var dataDirectory = ResolveDataDirectory(dataDirectoryArg, appDirectory);
        Directory.CreateDirectory(dataDirectory);

        var smokeTest = HasFlag(passThrough, "--smoke-test");
        var screenshot = HasFlag(passThrough, "--screenshot");

        Mutex? mutex = null;
        EventWaitHandle? activation = null;
        if (!smokeTest && !screenshot)
        {
            var suffix = Hash(dataDirectory);
            mutex = new Mutex(initiallyOwned: true, "Magpienest:" + suffix, out var createdNew);
            if (!createdNew)
            {
                try
                {
                    using var other = EventWaitHandle.OpenExisting("Magpienest:activate:" + suffix);
                    other.Set();
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    // The first instance is still starting or already exiting.
                }

                mutex.Dispose();
                return 0;
            }

            activation = new EventWaitHandle(false, EventResetMode.AutoReset, "Magpienest:activate:" + suffix);
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Kernel? kernel = null;
        var shuttingDown = false;

        void RequestShutdown(int code) => app.Dispatcher.BeginInvoke(() =>
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            try
            {
                kernel?.Shutdown();
            }
            catch (Exception ex)
            {
                kernel?.Log.Error("Shutdown failed.", ex);
            }

            app.Shutdown(code);
        });

        app.DispatcherUnhandledException += (_, e) =>
        {
            kernel?.Log.Error("Unhandled UI exception.", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            kernel?.Log.Error("Unhandled exception.", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            kernel?.Log.Error("Unobserved task exception.", e.Exception);
            e.SetObserved();
        };

        // Plugins create the windows and cannot see App resources, so the host gives every window without an icon the app icon.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(ApplyWindowIcon));

        app.Startup += (_, _) =>
        {
            kernel = new Kernel(new KernelOptions
            {
                AppDirectory = appDirectory,
                DataDirectory = dataDirectory,
                Args = passThrough,
                Dispatcher = new WpfDispatcher(app.Dispatcher),
                AppVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 1, 0),
                RequestShutdown = RequestShutdown,
            });
            kernel.Log.Info($"Magpienest starting; app '{appDirectory}', data '{dataDirectory}'.");
            kernel.Start();

            if (activation is not null)
            {
                var started = kernel;
                new Thread(() =>
                {
                    while (activation.WaitOne())
                    {
                        started.Emit(new AppActivationRequested());
                    }
                })
                { IsBackground = true, Name = "Magpienest activation listener" }.Start();
            }

            if (smokeTest)
            {
                var started = kernel;
                app.Dispatcher.InvokeAsync(() => RequestShutdown(WriteSmokeReport(started, dataDirectory)), DispatcherPriority.ApplicationIdle);
                return;
            }

            if (app.Windows.Count == 0)
            {
                kernel.Log.Error("No window exists after start-up (is the Shell plugin missing or failed?).");
                if (!screenshot)
                {
                    MessageBox.Show(string.Format(NoWindowMessage, kernel.LogFile), "Magpienest", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                RequestShutdown(1);
            }
        };

        var exitCode = app.Run();
        if (mutex is not null)
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }

        return exitCode;
    }

    private static void ApplyWindowIcon(object sender, RoutedEventArgs e)
    {
        if (sender is not Window { Icon: null } window || !ReferenceEquals(e.OriginalSource, window))
        {
            return;
        }

        try
        {
            window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Magpienest;component/Assets/Magpienest.ico"));
        }
        catch (Exception)
        {
            // A missing icon must never stop a window from opening.
        }
    }

    /// <summary>Writes &lt;data&gt;/smoke.json; 0 when nothing failed and every enabled plugin is active.</summary>
    private static int WriteSmokeReport(Kernel kernel, string dataDirectory)
    {
        var plugins = kernel.Plugins.Plugins;
        var report = plugins.Select(p => new
        {
            id = p.Manifest.Id,
            state = p.State.ToString(),
            error = p.Error,
            missingServices = p.MissingServices,
        });
        var path = Path.Combine(dataDirectory, "smoke.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        var ok = plugins.All(p => p.State != PluginState.Failed && (!p.IsEnabled || p.State == PluginState.Active));
        kernel.Log.Info($"Smoke test {(ok ? "passed" : "failed")}; report '{path}'.");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Waits (up to 30 s by default) for the process that is restarting Magpienest to exit, so it has released
    /// the single-instance lock before this instance tries to take it. True when it is gone.
    /// </summary>
    private static bool WaitForExit(int processId, int milliseconds = 30000)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(milliseconds);
        }
        catch (ArgumentException)
        {
            return true; // Already gone.
        }
        catch (InvalidOperationException)
        {
            return true; // Already gone.
        }
    }

    /// <summary>
    /// --apply-update: this exe is a new version unpacked by the store plugin. Waits for the old app to exit,
    /// copies this folder over <paramref name="target"/>, writes the outcome for the store plugin to read, and
    /// starts the installed exe again (the new version, or the old one when the update was undone).
    /// </summary>
    private static int ApplyUpdate(string appDirectory, string target, string? resultFile, int? waitPid, string? dataDirectory, List<string> passThrough)
    {
        var logFile = resultFile is null ? null : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultFile))!, "apply.log");
        void Log(string line)
        {
            try
            {
                if (logFile is not null)
                {
                    File.AppendAllText(logFile, $"{DateTimeOffset.Now:O} {line}{Environment.NewLine}", new UTF8Encoding(false));
                }
            }
            catch (IOException)
            {
                // Logging must never stop the update.
            }
        }

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
        Log($"Applying Magpienest {version} from '{appDirectory}' to '{target}'.");
        UpdateApplyResult result;
        if (waitPid is not null && !WaitForExit(waitPid.Value, 120000))
        {
            result = new UpdateApplyResult(false, "The running Magpienest did not exit.");
            Log(result.Error!);
            WriteUpdateResult(resultFile, version, result);
            return 1;
        }

        var backup = Path.Combine(Path.GetDirectoryName(appDirectory) ?? appDirectory, "backup");
        if (resultFile is not null)
        {
            backup = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultFile))!, "backup");
        }

        try
        {
            result = UpdateApplier.Apply(appDirectory, target, backup, Log);
        }
        catch (Exception ex)
        {
            result = new UpdateApplyResult(false, ex.Message);
        }

        Log(result.Success ? "Update applied." : $"Update failed: {result.Error}");
        WriteUpdateResult(resultFile, version, result);
        try
        {
            var info = new ProcessStartInfo(Path.Combine(target, UpdateApplier.ExeName)) { UseShellExecute = false, WorkingDirectory = target };
            if (!string.IsNullOrWhiteSpace(dataDirectory))
            {
                info.ArgumentList.Add("--data-dir");
                info.ArgumentList.Add(dataDirectory);
            }

            foreach (var argument in passThrough)
            {
                info.ArgumentList.Add(argument);
            }

            using var started = Process.Start(info);
        }
        catch (Exception ex)
        {
            Log($"Starting '{target}' failed: {ex.Message}");
            return 1;
        }

        return result.Success ? 0 : 1;
    }

    private static void WriteUpdateResult(string? resultFile, string version, UpdateApplyResult result)
    {
        if (resultFile is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new { version, success = result.Success, error = result.Error });
            File.WriteAllText(resultFile, json, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The store plugin then reports nothing; the files are as the log says.
        }
    }

    private static (string? DataDirectory, int? WaitPid, (string? Target, string? ResultFile) Update, List<string> PassThrough) ParseArgs(string[] args)
    {
        string? dataDirectory = null;
        int? waitPid = null;
        string? updateTarget = null;
        string? updateResult = null;
        var passThrough = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--data-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                dataDirectory = args[++i];
            }
            else if (string.Equals(args[i], "--wait-for-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid))
                {
                    waitPid = pid;
                }
            }
            else if (string.Equals(args[i], "--apply-update", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                updateTarget = args[++i];
            }
            else if (string.Equals(args[i], "--update-result", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                updateResult = args[++i];
            }
            else
            {
                passThrough.Add(args[i]);
            }
        }

        return (dataDirectory, waitPid, (updateTarget, updateResult), passThrough);
    }

    /// <summary>Resolves the data folder: --data-dir, MAGPIENEST_DATA_DIR, portable.txt, %APPDATA%\Magpienest.</summary>
    private static string ResolveDataDirectory(string? argument, string appDirectory)
    {
        if (!string.IsNullOrWhiteSpace(argument))
        {
            return Path.GetFullPath(argument);
        }

        var variable = Environment.GetEnvironmentVariable("MAGPIENEST_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(variable))
        {
            return Path.GetFullPath(variable);
        }

        if (File.Exists(Path.Combine(appDirectory, "portable.txt")))
        {
            return Path.Combine(appDirectory, "data");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Magpienest");
    }

    private static bool HasFlag(IEnumerable<string> args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static string Hash(string path) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(path).ToUpperInvariant())));

    /// <summary><see cref="IUiDispatcher"/> over the WPF dispatcher.</summary>
    private sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
    {
        public bool CheckAccess() => dispatcher.CheckAccess();

        public void Post(Action action) => dispatcher.BeginInvoke(action);

        public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

        public Task<T> InvokeAsync<T>(Func<T> func) => dispatcher.InvokeAsync(func).Task;
    }
}
