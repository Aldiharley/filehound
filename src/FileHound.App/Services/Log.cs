using System.IO;
using System.Threading.Channels;

namespace FileHound.App.Services;

/// <summary>Tiny append-only file logger: %LOCALAPPDATA%\FileHound\logs\filehound-yyyyMMdd.log, 7-day retention.</summary>
public static class Log
{
    private static readonly Channel<string> s_channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private static string? s_dir;
    private static Task? s_writer;

    public static string Directory => s_dir ?? string.Empty;

    public static void Init(string directory)
    {
        s_dir = directory;
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            foreach (var f in new DirectoryInfo(directory).GetFiles("filehound-*.log"))
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7)) f.Delete();
        }
        catch (IOException) { }
        s_writer = Task.Run(WriteLoopAsync);
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message) =>
        s_channel.Writer.TryWrite($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");

    public static async Task FlushAsync()
    {
        s_channel.Writer.TryComplete();
        if (s_writer is not null) await s_writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private static async Task WriteLoopAsync()
    {
        await foreach (var line in s_channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await File.AppendAllTextAsync(Path.Combine(s_dir!, $"filehound-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine).ConfigureAwait(false); }
            catch (IOException) { }
        }
    }
}
