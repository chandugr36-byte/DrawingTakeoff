using System;
using System.IO;
using System.Linq;

namespace DrawingTakeoff;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Thread-safe logger. Everything (incl. Debug + full stack traces) goes to a file in
/// %LocalAppData%\DrawingTakeoff\logs ; Info/Warn/Error are also pushed to the UI.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static int _pendingFlush;
    public static string LogDirectory { get; private set; } = "";
    public static string CurrentLogFile { get; private set; } = "";
    public static event Action<LogLevel, string>? Message;

    public static void Init()
    {
        try
        {
            LogDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                        "DrawingTakeoff", "logs");
            Directory.CreateDirectory(LogDirectory);
            CurrentLogFile = Path.Combine(LogDirectory, $"takeoff_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            var fs = new FileStream(CurrentLogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new System.Text.UTF8Encoding(false));
            foreach (var f in new DirectoryInfo(LogDirectory).GetFiles("takeoff_*.log")
                         .OrderByDescending(f => f.CreationTime).Skip(20))
                try { f.Delete(); } catch { }
        }
        catch { /* logging must never crash the app */ }
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg, null);
    public static void Info(string msg) => Write(LogLevel.Info, msg, null);
    public static void Warn(string msg, Exception? ex = null) => Write(LogLevel.Warn, msg, ex);
    public static void Error(string msg, Exception? ex = null) => Write(LogLevel.Error, msg, ex);

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    private static void Write(LogLevel lvl, string msg, Exception? ex)
    {
        var tag = lvl switch { LogLevel.Debug => "DBG", LogLevel.Info => "INF", LogLevel.Warn => "WRN", _ => "ERR" };
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{tag}] {msg}";

        lock (Gate)
        {
            try
            {
                if (_writer is not null)
                {
                    _writer.WriteLine(line);
                    if (ex is not null) _writer.WriteLine(ex);
                    // Keep the hot logging path cheap, but persist errors immediately.
                    if (++_pendingFlush >= 32 || lvl == LogLevel.Error)
                    {
                        _writer.Flush();
                        _pendingFlush = 0;
                    }
                }
                else if (CurrentLogFile.Length > 0)
                    File.AppendAllText(CurrentLogFile, line + (ex is null ? "" : Environment.NewLine + ex) + Environment.NewLine);
            }
            catch { }
        }

        if (lvl == LogLevel.Debug) return;
        var ui = ex is null ? line : $"{line}  ::  {ex.GetType().Name}: {ex.Message}";
        try { Message?.Invoke(lvl, ui); } catch { }
    }
}
