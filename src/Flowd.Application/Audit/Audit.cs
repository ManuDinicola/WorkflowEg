namespace Flowd.Application.Audit;

/// <summary>One mutating API call: who did what to which resource, and how it ended (FR-SEC-005).</summary>
public sealed record AuditEntry(long Seq, DateTimeOffset OccurredAt, string TenantId, string Actor, string Method, string Path, int Status);

/// <summary>A page of audit entries; <see cref="NextCursor"/> is null on the last page.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, long? NextCursor);

/// <summary>Port: append-only audit log.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken);

    Task<AuditPage> ListAsync(string tenantId, int pageSize, long? afterSeq, CancellationToken cancellationToken);
}
