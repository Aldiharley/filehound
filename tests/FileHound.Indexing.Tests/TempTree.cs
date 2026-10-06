using System.Diagnostics;

namespace FileHound.Indexing.Tests;

/// <summary>Disposable temp directory with helpers for building test trees.</summary>
public sealed class TempTree : IDisposable
{
    public TempTree() => Root = Directory.CreateTempSubdirectory("fh-tree-").FullName;

    public string Root { get; }

    public string File(string relative, int bytes = 1)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Root, relative)).FullName;

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    /// <summary>Creates a directory junction (no admin rights needed). Returns false if mklink is unavailable.</summary>
    public bool Junction(string relative, string targetRelative)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path(relative)}\" \"{Path(targetRelative)}\"")
        { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        return p.ExitCode == 0;
    }

    public static void WaitUntil(Func<bool> condition, int timeoutMs = 3000, string? because = null)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out after {timeoutMs} ms{(because is null ? "" : ": " + because)}");
            Thread.Sleep(25);
        }
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Reverse())
                if (new DirectoryInfo(d).Attributes.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class ListProgress<T> : IProgress<T>
{
    public readonly List<T> Items = [];
    public void Report(T value) { lock (Items) Items.Add(value); }
}
