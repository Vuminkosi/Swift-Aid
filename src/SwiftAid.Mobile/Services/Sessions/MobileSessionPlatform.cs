using SwiftAid.Client.Sessions;

namespace SwiftAid.Mobile.Services.Sessions;

public sealed class MobileSessionPlatform(CommunitySessionHttpClient calendarClient) : IMobileSessionPlatform
{
    public Task ShareAsync(string text) => Share.Default.RequestAsync(new ShareTextRequest(text, "Community session invitation"));

    public async Task<bool> OpenCalendarAsync(Guid sessionId)
    {
        var calendar = await calendarClient.GetCalendarFileAsync(sessionId);
        if (calendar is null || calendar.Content.Length == 0 || calendar.Content.Length > 256 * 1024 ||
            !string.Equals(calendar.ContentType, "text/calendar", StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.Combine(FileSystem.CacheDirectory, $"swiftaid-session-{sessionId:N}-{Guid.NewGuid():N}.ics");
        await File.WriteAllBytesAsync(path, calendar.Content);
        return await Launcher.Default.OpenAsync(new OpenFileRequest("Open community session calendar", new ReadOnlyFile(path, "text/calendar")));
    }
}
