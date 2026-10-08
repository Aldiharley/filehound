using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileHound.Core.Carving;
using FileHound.Core.Index;
using FileHound.Indexing.Recovery;

namespace FileHound.App.Services;

/// <summary>What the preview pane shows for one carved file.</summary>
public sealed record PreviewContent(string Kind, ImageSource? Image, string? Text, IReadOnlyList<string>? Entries, string Caption);

/// <summary>FR-23 previews: images through WPF decoders from memory (capped), a text snippet, a ZIP entry list, or just the validator's info.</summary>
public static class PreviewService
{
    public const int MaxBytes = 50 << 20;

    public static PreviewContent Build(VolumeReader reader, CarvedFile f, int maxBytes = MaxBytes)
    {
        string caption = "Validated" + (f.Info is null ? "" : " · " + f.Info);
        int take = (int)Math.Min(f.Size, maxBytes);
        byte[] bytes;
        try
        {
            bytes = new byte[take];
            reader.ReadBytes(f.StartLcn * reader.Geometry.BytesPerCluster, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or ObjectDisposedException)
        {
            return new PreviewContent("none", null, null, null, caption + " · preview unavailable");
        }
        return Build(f, bytes, f.Size > take, caption);
    }

    /// <summary>Pure part (tests): decides the preview kind from the bytes.</summary>
    public static PreviewContent Build(CarvedFile f, byte[] bytes, bool truncated, string caption)
    {
        if (f.Type.Category == FileCategory.Image && !truncated)
        {
            var image = DecodeImage(bytes, 0);
            if (image is not null) return new PreviewContent("image", image, null, null, caption);
        }
        if (f.Type.Id is "zip")
        {
            var entries = ZipListing.Entries(bytes, 60);
            if (entries.Count > 0) return new PreviewContent("zip", null, null, entries, $"{caption} · {entries.Count} entries{(entries.Count == 60 ? "+" : "")}");
        }
        if (f.Type.Id is "rtf" or "pdf")
        {
            string text = Snippet(bytes, 1500);
            if (text.Length > 0) return new PreviewContent("text", null, text, null, caption);
        }
        return new PreviewContent("info", null, null, null, caption);
    }

    /// <summary>A 40 px-row thumbnail for carved images (reads at most 4 MB of the file).</summary>
    public static ImageSource? Thumbnail(VolumeReader reader, CarvedFile f)
    {
        if (f.Type.Category != FileCategory.Image) return null;
        int take = (int)Math.Min(f.Size, 4 << 20);
        if (take < f.Size && f.Type.Id != "jpg") return null;   // only JPEG decodes usefully from a prefix
        try
        {
            var bytes = new byte[take];
            reader.ReadBytes(f.StartLcn * reader.Geometry.BytesPerCluster, bytes);
            return DecodeImage(bytes, 80);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or ObjectDisposedException) { return null; }
    }

    /// <summary>Decodes an image from memory; <paramref name="decodeWidth"/> &gt; 0 asks the decoder for a thumbnail-sized bitmap.</summary>
    public static ImageSource? DecodeImage(byte[] bytes, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes, writable: false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Printable text from the start of the file: RTF control words stripped, PDF object noise skipped.</summary>
    internal static string Snippet(byte[] bytes, int maxChars)
    {
        var sb = new StringBuilder();
        var text = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 64 << 10));
        bool inWord = false;
        for (int i = 0; i < text.Length && sb.Length < maxChars; i++)
        {
            char ch = text[i];
            if (ch == '\\') { inWord = true; continue; }
            if (inWord) { if (!char.IsLetterOrDigit(ch) && ch != '-') { inWord = false; if (ch == ' ') continue; } else continue; }
            if (ch is '{' or '}') continue;
            if (ch == '\r' || ch == '\n') { if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' '); continue; }
            if (ch < ' ' || ch > '~') continue;
            sb.Append(ch);
        }
        return sb.ToString().Trim();
    }
}
