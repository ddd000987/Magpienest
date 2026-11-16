using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Magpienest.Core;
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
        var (dataDirectoryArg, waitPid, passThrough) = ParseArgs(args);
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
    /// Waits (up to 30 s) for the process that is restarting Magpienest to exit, so it has released the
    /// single-instance lock before this instance tries to take it.
    /// </summary>
    private static void WaitForExit(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit(30000);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    private static (string? DataDirectory, int? WaitPid, List<string> PassThrough) ParseArgs(string[] args)
    {
        string? dataDirectory = null;
        int? waitPid = null;
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
            else
            {
                passThrough.Add(args[i]);
            }
        }

        return (dataDirectory, waitPid, passThrough);
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
