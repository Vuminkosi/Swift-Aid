namespace SwiftAid.Mobile.Services.Sessions;

public interface IMobileSessionPlatform
{
    Task ShareAsync(string text);
    Task<bool> OpenCalendarAsync(Guid sessionId);
}
