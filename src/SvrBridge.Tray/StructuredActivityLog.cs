using System.Text.Json;
using System.Text.RegularExpressions;
using SvrBridge.Core;

namespace SvrBridge.Tray;

internal sealed partial class StructuredActivityLog
{
    private readonly object _writeGate = new();
    private readonly string _logDirectory;

    public StructuredActivityLog(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? AppPaths.LogDirectory;
        Directory.CreateDirectory(_logDirectory);
        RemoveExpiredLogs();
    }

    public string LogDirectory => _logDirectory;

    public void Write(BridgeActivity activity)
    {
        // The one thing this log will not keep. Redaction can only remove what
        // it recognises, and a chat message has no pattern to match on - the
        // whole line is somebody else's words. Dropping it here rather than at
        // each call site means a future caller cannot forget to.
        if (activity.ContainsUserContent)
        {
            return;
        }

        Write(activity.EventName, activity.Message, activity.Level);
    }

    public void Write(
        string eventName,
        string message,
        BridgeLogLevel level = BridgeLogLevel.Info)
    {
        try
        {
            var entry = new
            {
                timestamp = DateTimeOffset.Now.ToString("O"),
                level = level.ToString().ToLowerInvariant(),
                @event = eventName,
                message = Redact(message)
            };
            var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
            var path = Path.Combine(
                _logDirectory,
                $"svr-bridge-{DateTime.Now:yyyyMMdd}.jsonl");
            lock (_writeGate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // Logging must never stop controller input or action delivery.
        }
    }

    private void RemoveExpiredLogs()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-14);
            foreach (var path in Directory.EnumerateFiles(
                         _logDirectory,
                         "svr-bridge-*.jsonl"))
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Retention is best-effort.
        }
    }

    private static string Redact(string message) =>
        SecretValuePattern().Replace(message, "$1=[protected]");

    [GeneratedRegex(
        @"(?i)\b(password|authentication|token|secret)\s*[:=]\s*[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretValuePattern();
}
