using System.Security.Cryptography;
using System.Text;
using Governance.Application.Data;

namespace Governance.Notifications;

public enum Reservation
{
    /// <summary>This node may send now; it records the outcome afterwards.</summary>
    Reserved,
    /// <summary>Already delivered, recorded, skipped or set aside: do not send again.</summary>
    Done,
    /// <summary>Another node reserved it moments ago and may be sending: retry the row later.</summary>
    PendingElsewhere,
}

/// <summary>
/// One ntf.Delivery row per event, recipient and channel (section 8.2), reserved as PENDING before anything is sent.
/// The reservation also renews this node's lease on the outbox row and checks that the node still holds it, so a
/// node whose lease ran out during a slow fan-out stops instead of sending alongside the node that took over.
/// </summary>
public sealed class DeliveryLedger(Db db)
{
    /// <summary>Statuses after which a recipient is never notified again for the same event.</summary>
    public static readonly IReadOnlySet<string> Final = new HashSet<string> { "SENT", "RECORDED", "SKIPPED", "SUPPRESSED", "QUIET", "DEAD_LETTER" };

    /// <summary>A PENDING older than this belongs to a node that stopped mid-send; it may be taken over.</summary>
    public static readonly TimeSpan StalePending = TimeSpan.FromMinutes(10);

    public async Task<Reservation> ReserveAsync(OutboxRow row, int? personId, string? address, string channel, string subject, CancellationToken ct)
    {
        var key = Key(row, personId?.ToString() ?? "-", channel);
        var result = await db.InTransactionAsync(u => u.ScalarAsync<string>(
            """
            SET XACT_ABORT ON;
            IF @node IS NOT NULL
            BEGIN
                UPDATE ntf.Outbox SET LockedUntilUtc = DATEADD(SECOND, 60, SYSUTCDATETIME())
                WHERE OutboxId = @OutboxId AND LockedBy = @node AND ProcessedAtUtc IS NULL AND DeadLetteredUtc IS NULL;
                IF @@ROWCOUNT = 0 BEGIN SELECT 'LEASE_LOST'; RETURN; END
            END
            DECLARE @status varchar(20), @utc datetime2;
            SELECT @status = Status, @utc = Utc FROM ntf.Delivery WITH (UPDLOCK, HOLDLOCK) WHERE DedupeKey = @key;
            IF @status IS NULL
            BEGIN
                INSERT ntf.Delivery (OutboxId, RecipientPersonId, RecipientAddress, Channel, DedupeKey, Subject, Status, Detail)
                VALUES (@OutboxId, @personId, @address, @channelShort, @key, @subject, 'PENDING', @node);
                SELECT 'RESERVED'; RETURN;
            END
            IF @status IN ('SENT','RECORDED','SKIPPED','SUPPRESSED','QUIET','DEAD_LETTER') BEGIN SELECT 'DONE'; RETURN; END
            IF @status = 'PENDING' AND @utc > DATEADD(SECOND, -@staleSeconds, SYSUTCDATETIME()) BEGIN SELECT 'PENDING'; RETURN; END
            UPDATE ntf.Delivery SET Status = 'PENDING', Detail = @node, Attempts = Attempts + 1, Utc = SYSUTCDATETIME() WHERE DedupeKey = @key;
            SELECT 'RESERVED';
            """,
            new
            {
                key, row.OutboxId, node = row.LockedBy, personId, address = Cut(address, 256), channelShort = channel.Split(':')[0],
                subject = Cut(subject, 400), staleSeconds = (int)StalePending.TotalSeconds,
            }), ct);
        return result switch
        {
            "LEASE_LOST" => throw new LeaseLostException(row.OutboxId),
            "RESERVED" => Reservation.Reserved,
            "PENDING" => Reservation.PendingElsewhere,
            _ => Reservation.Done,
        };
    }

    /// <summary>Records the outcome (SENT, RECORDED, FAILED...), creating the row when nothing was reserved (suppressed, skipped).</summary>
    public Task RecordAsync(OutboxRow row, int? personId, string? address, string channel, string status, string subject, string? detail)
    {
        var key = Key(row, personId?.ToString() ?? "-", channel);
        return db.InTransactionAsync(u => u.ExecuteAsync(
            """
            IF EXISTS (SELECT 1 FROM ntf.Delivery WITH (UPDLOCK, HOLDLOCK) WHERE DedupeKey = @key)
                UPDATE ntf.Delivery SET Status = @status, Detail = @detail, RecipientAddress = COALESCE(@address, RecipientAddress), Utc = SYSUTCDATETIME() WHERE DedupeKey = @key;
            ELSE
                INSERT ntf.Delivery (OutboxId, RecipientPersonId, RecipientAddress, Channel, DedupeKey, Subject, Status, Detail)
                VALUES (@OutboxId, @personId, @address, @channelShort, @key, @subject, @status, @detail);
            """,
            new
            {
                key, status, detail = Cut(detail, 1000), row.OutboxId, personId, address = Cut(address, 256), channelShort = channel.Split(':')[0],
                subject = Cut(subject, 400),
            }));
    }

    /// <summary>Event, recipient and channel (section 8.2). Long keys are hashed to fit the column.</summary>
    public static string Key(OutboxRow row, string recipient, string channel)
    {
        var key = $"{row.DedupeKey}|{recipient}|{channel}";
        return key.Length <= 300 ? key : $"{key[..200]}#{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]}";
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
