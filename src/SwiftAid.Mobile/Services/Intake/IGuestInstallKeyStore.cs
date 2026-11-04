namespace SwiftAid.Mobile.Services.Intake;

public interface IGuestInstallKeyStore
{
    Task<byte[]> GetOrCreateKeyAsync(CancellationToken cancellationToken);
}
