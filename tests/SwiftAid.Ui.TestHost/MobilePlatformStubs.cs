namespace SwiftAid.Ui.TestHost.MobilePlatforms;

public sealed record ShareTextRequest(string Text, string Title);
public sealed class Share
{
    public static Share Default { get; } = new();
    public Task RequestAsync(ShareTextRequest request) => Task.CompletedTask;
}
public static class FileSystem { public static string CacheDirectory => Path.GetTempPath(); }
public sealed record ReadOnlyFile(string Path, string ContentType);
public sealed record OpenFileRequest(string Title, ReadOnlyFile File);
public sealed class Launcher
{
    public static Launcher Default { get; } = new();
    public Task<bool> OpenAsync(OpenFileRequest request) => Task.FromResult(false);
}
