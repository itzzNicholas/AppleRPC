// SPDX-License-Identifier: AGPL-3.0-only
namespace AppleRPC.Logging;

/// <summary>Static facade so every class can log without plumbing a logger around.</summary>
public static class Log
{
    private static volatile FileLogger? _sink;

    public static void Init(FileLogger sink) => _sink = sink;

    public static void Trace(string message) => _sink?.Write(LogLevel.Trace, message);
    public static void Debug(string message) => _sink?.Write(LogLevel.Debug, message);
    public static void Info(string message) => _sink?.Write(LogLevel.Information, message);
    public static void Warn(string message, Exception? ex = null) => _sink?.Write(LogLevel.Warning, message, ex);
    public static void Error(string message, Exception? ex = null) => _sink?.Write(LogLevel.Error, message, ex);
}
