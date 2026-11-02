using SwiftAid.Contracts.Ellai;

namespace SwiftAid.Application.Ellai;

/// <summary>Nonblocking notification of ephemeral output, separate from durable history.</summary>
public interface IEllaiResponseAudioPreparation
{
    void Published(EllaiSessionResponseDto response);
    void Clear(string sessionId);
}
