using Baton.Host.Media;

namespace Baton.Tests;

public sealed class SharedFoldersTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "baton-shared-" + Guid.NewGuid().ToString("N"));
    private readonly string _shared;
    private readonly SharedFolders _folders = new();

    public SharedFoldersTests()
    {
        _shared = Path.Combine(_base, "Shared");
        Directory.CreateDirectory(Path.Combine(_shared, "Photos"));
        File.WriteAllText(Path.Combine(_shared, "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(_shared, "Photos", "cat.jpg"), "meow");
        File.WriteAllText(Path.Combine(_base, "secret.txt"), "private");
        var hidden = Path.Combine(_shared, "hidden.txt");
        File.WriteAllText(hidden, "hidden");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        _folders.Roots = [_shared + Path.DirectorySeparatorChar];
    }

    [Fact]
    public void ListsSharedFoldersThenTheirContents()
    {
        var roots = _folders.List("r1", null);
        Assert.Equal(["Shared"], roots.Entries.Select(entry => entry.Name));
        Assert.Null(roots.Error);

        var listing = _folders.List("r2", roots.Entries[0].Path);
        // Folders first; hidden files left out.
        Assert.Equal(["Photos", "notes.txt"], listing.Entries.Select(entry => entry.Name));
        Assert.True(listing.Entries[0].Folder);
        Assert.Equal(5, listing.Entries[1].Size);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public void NothingSharedSaysWhereToShare()
    {
        var listing = new SharedFolders().List("r", null);
        Assert.Empty(listing.Entries);
        Assert.NotNull(listing.Error);
    }

    [Fact]
    public void RefusesEverythingOutsideTheSharedFolders()
    {
        Assert.NotNull(_folders.ResolveFile(Path.Combine(_shared, "Photos", "cat.jpg")));
        Assert.Null(_folders.ResolveFile(Path.Combine(_base, "secret.txt")));
        Assert.Null(_folders.ResolveFile(Path.Combine(_shared, "..", "secret.txt")));
        Assert.Null(_folders.ResolveFile(Path.Combine(_shared, "hidden.txt")));
        Assert.Null(_folders.ResolveFile(_shared + "-other" + Path.DirectorySeparatorChar + "x.txt"));
        Assert.Null(_folders.ResolveFile(Path.Combine(_shared, "notes.txt:stream")));
        Assert.Null(_folders.ResolveFile(@"\\?\" + Path.Combine(_shared, "notes.txt")));
        Assert.Null(_folders.ResolveFile("notes.txt"));
        Assert.Null(_folders.ResolveFile(""));
        Assert.NotNull(_folders.List("r", Path.Combine(_base, "secret.txt")).Error);
        Assert.NotNull(_folders.List("r", _base).Error);
    }

    [Fact]
    public void RefusesAPathThroughALink()
    {
        var link = Path.Combine(_shared, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, _base);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Symbolic links need Developer Mode or admin rights; without them there is nothing to test.
            return;
        }

        Assert.Null(_folders.ResolveFile(Path.Combine(link, "secret.txt")));
        Assert.DoesNotContain(_folders.List("r", _shared).Entries, entry => entry.Name == "escape");
    }

    [Fact]
    public void ADriveIsNamedByItsLetter()
    {
        Assert.Equal("D:", SharedFolders.DisplayName(@"D:\"));
        Assert.Equal("Music", SharedFolders.DisplayName(@"D:\Music"));
        Assert.True(SharedFolders.IsWithin(@"D:\", @"D:\Music\a.mp3"));
        Assert.False(SharedFolders.IsWithin(@"D:\Music", @"D:\Musicals\a.mp3"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_base, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
