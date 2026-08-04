namespace Sol.Application.Contracts.Realtime;

/// <summary>Envelope for every server-to-client realtime message.</summary>
public sealed record RealtimeEnvelope(
    string Type,
    string Payload,
    DateTimeOffset SentAt);
