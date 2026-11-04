namespace SwiftAid.Contracts.Sessions;

/// <summary>Short-lived, owner-bound identity for recovering an unconfirmed draft creation.</summary>
public sealed record SessionDraftReservation(Guid SessionId, string Token);
