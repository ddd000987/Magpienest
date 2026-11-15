using System.Globalization;
using Magpienest.Sdk;

namespace Magpienest.Core;

/// <summary>Daily log files &lt;data&gt;/logs/magpienest-YYYYMMDD.log; keeps the last 14.</summary>
internal sealed class FileLog
{
    private const int KeepFiles = 14;

    private readonly string _directory;
    private readonly Lock _lock = new();

    public FileLog(string directory, bool verbose)
    {
        _directory = directory;
        Verbose = verbose;
        Directory.CreateDirectory(directory);
        DeleteOldFiles();
    }

    public bool Verbose { get; }

    public string CurrentFile => PathFor(DateTime.Now);

    public void Write(string level, string source, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = $"{now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} {level,-5} [{source}] {message}";
        if (exception is not null)
        {
            line += System.Environment.NewLine + exception;
        }

        lock (_lock)
        {
            try
            {
                File.AppendAllText(PathFor(now), line + System.Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }

    private string PathFor(DateTime time) =>
        Path.Combine(_directory, $"magpienest-{time.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private void DeleteOldFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_directory, "magpienest-*.log").OrderDescending(StringComparer.OrdinalIgnoreCase).Skip(KeepFiles))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not important enough to fail start-up.
        }
    }
}

/// <summary><see cref="ILog"/> tagged with a source (plugin id or "kernel").</summary>
internal sealed class PluginLog : ILog
{
    private readonly FileLog _file;
    private readonly string _source;

    public PluginLog(FileLog file, string source)
    {
        _file = file;
        _source = source;
    }

    public void Debug(string message)
    {
        if (_file.Verbose)
        {
            _file.Write("DEBUG", _source, message, null);
        }
    }

    public void Info(string message) => _file.Write("INFO", _source, message, null);

    public void Warn(string message, Exception? ex = null) => _file.Write("WARN", _source, message, ex);

    public void Error(string message, Exception? ex = null) => _file.Write("ERROR", _source, message, ex);
}
