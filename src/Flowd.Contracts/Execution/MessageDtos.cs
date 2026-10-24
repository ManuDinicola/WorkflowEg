using System.Text.Json.Nodes;

namespace Flowd.Contracts.Execution;

public sealed record PublishMessageRequestDto(string Name, string CorrelationKey, JsonObject? Variables, long? TtlMs, string? MessageId);

public sealed record PublishMessageResponseDto(string MessageKey, int Correlations);
