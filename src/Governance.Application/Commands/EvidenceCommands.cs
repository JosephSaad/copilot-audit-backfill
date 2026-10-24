using System.Security.Cryptography;
using Dapper;
using Governance.Application.Data;
using Governance.Application.Integrations;
using Microsoft.Extensions.DependencyInjection;

namespace Governance.Application.Commands;

public sealed record UploadedFile(string FileName, string? ContentType, long Length, Func<Stream> Open);

public sealed record EvidenceLinkTarget(string LinkType, long LinkId);

/// <summary>
/// Evidence storage: allow-listed types, size cap, content sniffing, anti-malware scan, SHA-256 in the ledger
/// (evd.EvidenceItem) and the bytes in a regular table so they can be purged at the end of retention.
/// </summary>
public sealed class EvidenceStore(IServiceProvider services, IClock clock, ICurrentPrincipal principal)
{
    public async Task<long> SaveAsync(Uow uow, UploadedFile file, string evidenceType, string? description, int? fundingOwnerId, IEnumerable<EvidenceLinkTarget> links)
    {
        var settings = (await uow.QueryAsync<(string Key, string Value)>("SELECT SettingKey, SettingValue FROM ref.Setting WHERE SettingKey IN ('Evidence.MaxUploadMb','Evidence.AllowedTypes')"))
            .ToDictionary(x => x.Key, x => x.Value);
        var maxMb = int.TryParse(settings.GetValueOrDefault("Evidence.MaxUploadMb"), out var m) ? m : 20;
        var allowed = (settings.GetValueOrDefault("Evidence.AllowedTypes") ?? ".png,.jpg,.jpeg,.pdf,.csv,.json,.xlsx,.docx,.txt")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = Path.GetFileName(file.FileName ?? "");
        var ext = Path.GetExtension(name);
        if (string.IsNullOrWhiteSpace(name) || !allowed.Contains(ext)) throw new DomainException($"Evidence files must be one of: {string.Join(", ", allowed)}.");
        if (file.Length <= 0) throw new DomainException("The file is empty.");
        if (file.Length > maxMb * 1024L * 1024L) throw new DomainException($"Evidence files are limited to {maxMb} MB.");

        byte[] bytes;
        await using (var s = file.Open())
        using (var ms = new MemoryStream())
        {
            await s.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        if (!ContentMatches(ext, bytes)) throw new DomainException("The file's content does not match its type.");

        var scanner = services.GetService<IEvidenceScanner>();
        if (scanner is not null)
        {
            using var scanStream = new MemoryStream(bytes, writable: false);
            var result = await scanner.ScanAsync(scanStream, name, uow.Cancellation);
            if (!result.Clean) throw new DomainException("The anti-malware scan refused the file.");
        }
        var hash = SHA256.HashData(bytes);
        var contentId = await uow.ScalarAsync<long>("INSERT evd.EvidenceContent (Bytes) OUTPUT inserted.ContentId VALUES (@bytes)", new { bytes });
        var publicId = Guid.NewGuid();
        var evidenceId = await uow.ScalarAsync<long>(
            """
            INSERT evd.EvidenceItem (PublicId, ContentId, FundingOwnerId, EvidenceType, FileName, ContentType, SizeBytes, Sha256, Description, SourceSystem, UploadedByPersonId, UploadedUtc)
            OUTPUT inserted.EvidenceId
            VALUES (@publicId, @contentId, @fundingOwnerId, @evidenceType, @name, @contentType, @size, @hash, @description, @source, @person, @now)
            """,
            new
            {
                publicId, contentId, fundingOwnerId, evidenceType, name, contentType = ContentTypeFor(ext), size = (long)bytes.Length, hash,
                description = CommandRunner.TrimN(description, 1000), source = scanner is null ? "UPLOAD_UNSCANNED" : "UPLOAD", person = principal.PersonId, now = clock.UtcNow,
            });
        foreach (var l in links) await LinkAsync(uow, evidenceId, l);
        return evidenceId;
    }

    public Task LinkAsync(Uow uow, long evidenceId, EvidenceLinkTarget link)
        => uow.ExecuteAsync("INSERT evd.EvidenceLink (EvidenceId, LinkType, LinkId, LinkedUtc, LinkedByPersonId) VALUES (@evidenceId, @LinkType, @LinkId, @now, @p)",
            new { evidenceId, link.LinkType, link.LinkId, now = clock.UtcNow, p = principal.PersonId });

    private static bool ContentMatches(string ext, byte[] b)
    {
        bool Starts(params byte[] sig) => b.Length >= sig.Length && b.AsSpan(0, sig.Length).SequenceEqual(sig);
        switch (ext.ToLowerInvariant())
        {
            case ".png": return Starts(0x89, 0x50, 0x4E, 0x47);
            case ".jpg": case ".jpeg": return Starts(0xFF, 0xD8, 0xFF);
            case ".pdf": return Starts(0x25, 0x50, 0x44, 0x46);
            case ".xlsx": case ".docx": return Starts(0x50, 0x4B, 0x03, 0x04);
            case ".csv": case ".json": case ".txt":
                // Text: no NUL bytes in the first 8 KB, and not a script or executable header.
                var n = Math.Min(b.Length, 8192);
                for (var i = 0; i < n; i++) if (b[i] == 0) return false;
                return !Starts(0x4D, 0x5A);
            default: return false;
        }
    }

    public static string ContentTypeFor(string ext) => ext.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".pdf" => "application/pdf", ".csv" => "text/csv", ".json" => "application/json",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        _ => "text/plain",
    };
}

public sealed class EvidenceQueries(Db db, ICurrentPrincipal principal)
{
    /// <summary>
    /// Returns the file when the person can see something it is linked to (row-level security on the request,
    /// the workload or the event), or holds a central or auditor role.
    /// </summary>
    public async Task<(string FileName, string ContentType, byte[] Bytes, string Sha256)?> DownloadAsync(Guid publicId)
    {
        await using var c = await db.OpenUserAsync();
        var item = await c.QuerySingleOrDefaultAsync<(long EvidenceId, long? ContentId, string? FileName, string? ContentType, byte[]? Sha256)>(
            "SELECT EvidenceId, ContentId, FileName, ContentType, Sha256 FROM evd.EvidenceItem WHERE PublicId = @publicId", new { publicId });
        if (item.EvidenceId == 0) return null;
        var visible = principal.IsCentral || principal.AppRoles.Contains("Workbench.Auditor") || await c.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM evd.EvidenceLink l
            WHERE l.EvidenceId = @EvidenceId AND (
                (l.LinkType = 'REQUEST' AND EXISTS (SELECT 1 FROM wf.Request r WHERE r.RequestId = l.LinkId))
             OR (l.LinkType = 'DECISION' AND EXISTS (SELECT 1 FROM wf.Decision d JOIN wf.Request r ON r.RequestId = d.RequestId WHERE d.DecisionId = l.LinkId))
             OR (l.LinkType = 'VERIFICATION' AND EXISTS (SELECT 1 FROM evd.Verification v JOIN wf.Request r ON r.RequestId = v.RequestId WHERE v.VerificationId = l.LinkId))
             OR (l.LinkType = 'WORKLOAD' AND EXISTS (SELECT 1 FROM core.Workload w WHERE w.WorkloadId = l.LinkId))
             OR (l.LinkType = 'EVENT' AND EXISTS (SELECT 1 FROM ops.ConsumptionEvent e WHERE e.EventId = l.LinkId))
             OR (l.LinkType = 'CAMPAIGN_ITEM' AND EXISTS (SELECT 1 FROM wf.CampaignItem i WHERE i.CampaignItemId = l.LinkId)))
            """, new { item.EvidenceId }) > 0;
        if (!visible) return null;
        var bytes = item.ContentId is long cid ? await c.ExecuteScalarAsync<byte[]?>("SELECT Bytes FROM evd.EvidenceContent WHERE ContentId = @cid", new { cid }) : null;
        if (bytes is null) throw new DomainException("This file's content was purged at the end of its retention period; its hash and metadata remain.");
        return (item.FileName ?? "evidence", item.ContentType ?? "application/octet-stream", bytes, Convert.ToHexString(item.Sha256 ?? Array.Empty<byte>()));
    }
}
