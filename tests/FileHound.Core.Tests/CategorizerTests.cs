namespace FileHound.Core.Tests;

public class CategorizerTests
{
    [Theory]
    [InlineData("report.pdf", FileCategory.Document)]
    [InlineData("photo.jpeg", FileCategory.Image)]
    [InlineData("movie.mkv", FileCategory.Video)]
    [InlineData("song.flac", FileCategory.Audio)]
    [InlineData("backup.7z", FileCategory.Archive)]
    [InlineData("setup.exe", FileCategory.App)]
    [InlineData("run.ps1", FileCategory.App)]
    [InlineData("program.cs", FileCategory.Code)]
    [InlineData("readme", FileCategory.Other)]
    [InlineData(".gitignore", FileCategory.Other)]
    public void FromName_maps_extension(string name, FileCategory expected)
        => Assert.Equal(expected, Categorizer.FromName(name, false));

    [Fact]
    public void Directories_are_folders() => Assert.Equal(FileCategory.Folder, Categorizer.FromName("src.cs", true));

    [Fact]
    public void Extension_of_dotfile_is_empty() => Assert.True(Categorizer.Extension(".gitignore").IsEmpty);

    [Fact]
    public void Extension_takes_last_dot() => Assert.Equal("gz", Categorizer.Extension("a.tar.gz").ToString());

    [Fact]
    public void Extension_of_trailing_dot_is_empty() => Assert.True(Categorizer.Extension("name.").IsEmpty);

    [Theory]
    [InlineData("pic", FileCategory.Image)]
    [InlineData("Documents", FileCategory.Document)]
    [InlineData("exe", FileCategory.App)]
    [InlineData("music", FileCategory.Audio)]
    public void TryParseCategory_accepts_aliases(string token, FileCategory expected)
    {
        Assert.True(Categorizer.TryParseCategory(token, out var c));
        Assert.Equal(expected, c);
    }

    [Fact]
    public void TryParseCategory_rejects_unknown() => Assert.False(Categorizer.TryParseCategory("banana", out _));
}
