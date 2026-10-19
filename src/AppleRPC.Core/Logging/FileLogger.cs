// SPDX-License-Identifier: AGPL-3.0-only
using System.Text;

namespace AppleRPC.Logging;

public enum LogLevel { Trace, Debug, Information, Warning, Error }

/// <summary>
/// Minimal thread-safe file logger with size-based rotation:
/// applerpc.log -> applerpc.1.log -> applerpc.2.log ... (at most <c>maxFiles</c> files in total).
/// Logging must never take the app down, so every I/O failure is swallowed.
/// </summary>
public sealed class FileLogger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;

    public LogLevel MinimumLevel { get; }
    public string CurrentFile => Path.Combine(_directory, _baseName + ".log");

    public FileLogger(string directory, LogLevel minimumLevel = LogLevel.Information,
        long maxBytes = 1024 * 1024, int maxFiles = 3, string baseName = "applerpc")
    {
        _directory = directory;
        _baseName = baseName;
        _maxBytes = Math.Max(1024, maxBytes);
        _maxFiles = Math.Max(1, maxFiles);
        MinimumLevel = minimumLevel;
    }

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (level < MinimumLevel) return;

        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
          .Append(" [").Append(level.ToString()[..3].ToUpperInvariant()).Append("] ")
          .Append(message);
        if (exception is not null) sb.AppendLine().Append(exception);
        sb.AppendLine();
        var line = sb.ToString();

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
            catch
            {
                // Never let logging crash the app.
            }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(CurrentFile);
        if (!info.Exists || info.Length + incomingBytes <= _maxBytes) return;

        if (_maxFiles == 1)
        {
            File.Delete(CurrentFile);
            return;
        }

        // Shift the oldest first: .(n-2) -> .(n-1), ..., .1 -> .2, current -> .1
        var oldest = PathFor(_maxFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = _maxFiles - 2; i >= 1; i--)
        {
            var from = PathFor(i);
            if (File.Exists(from)) File.Move(from, PathFor(i + 1));
        }
        File.Move(CurrentFile, PathFor(1));
    }

    private string PathFor(int index) => Path.Combine(_directory, $"{_baseName}.{index}.log");
}
