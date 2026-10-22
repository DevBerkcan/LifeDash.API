using System.Text.Json;
using LifeDash.Api.Data;
using LifeDash.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeDash.Api.Services;

public record PackageSyncResult(int Scanned, int Added, int Updated);

// Shared by the authenticated manual "Postfach scannen" button (POST /api/deliveries/scan) and the
// anonymous, key-protected cron endpoint (POST /api/jobs/mail-scan) so both go through the same
// scan -> upsert path instead of duplicating it.
public class PackageSyncService
{
    private readonly ImapPackageScanner _scanner;

    public PackageSyncService(ImapPackageScanner scanner)
    {
        _scanner = scanner;
    }

    public async Task<PackageSyncResult> SyncAsync(int userId, bool full, LifeDashContext db, CancellationToken ct)
    {
        var scanned = await _scanner.ScanMailboxAsync(full, ct);
        var existing = await db.Packages.Where(p => p.UserId == userId).ToListAsync(ct);
        var byTracking = existing.ToDictionary(p => p.TrackingNumber.ToUpperInvariant(), p => p);

        var added = 0;
        var updated = 0;

        foreach (var s in scanned)
        {
            var key = s.TrackingNumber.ToUpperInvariant();
            var newEntry = new PackageHistoryEntry(s.Status, s.LatestEvent, s.LatestEventTime, s.UpdatedAt);

            if (byTracking.TryGetValue(key, out var existingPkg))
            {
                var history = DeserializeHistory(existingPkg.HistoryJson);
                var sameIndex = history.FindIndex(h => h.text == newEntry.text && h.time == newEntry.time);
                if (sameIndex >= 0)
                {
                    // Same mail seen before. If it was the latest entry and the old parser read it as
                    // "delivered" but it isn't, correct it. Manual changes always add a newer history
                    // entry, so they are never overwritten here.
                    var same = history[sameIndex];
                    var isLatest = sameIndex == history.Count - 1;
                    if (isLatest && same.status == "delivered" && s.Status != "delivered" && existingPkg.Status == "delivered")
                    {
                        history[sameIndex] = same with { status = s.Status };
                        existingPkg.Status = s.Status;
                        existingPkg.HistoryJson = JsonSerializer.Serialize(history);
                        existingPkg.UpdatedAt = DateTime.UtcNow;
                        updated++;
                    }
                    continue;
                }

                history.Add(newEntry);
                // An unrelated mail that only mentions the tracking number (no recognisable status)
                // must not move the package back or forward — just record it in the history.
                if (s.StatusRecognized) existingPkg.Status = s.Status;
                existingPkg.Sender ??= s.Sender;
                existingPkg.ExpectedDelivery = ParseDateOnly(s.ExpectedDelivery) ?? existingPkg.ExpectedDelivery;
                existingPkg.LatestEvent = s.LatestEvent;
                existingPkg.LatestEventTime = s.LatestEventTime;
                existingPkg.HistoryJson = JsonSerializer.Serialize(history);
                existingPkg.UpdatedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                var pkg = new Package
                {
                    UserId = userId,
                    Title = s.Title,
                    Carrier = s.Carrier,
                    TrackingNumber = s.TrackingNumber,
                    Sender = s.Sender,
                    Status = s.Status,
                    ExpectedDelivery = ParseDateOnly(s.ExpectedDelivery),
                    LatestEvent = s.LatestEvent,
                    LatestEventTime = s.LatestEventTime,
                    Source = "email_scan",
                    UpdatedAt = DateTime.UtcNow,
                    HistoryJson = JsonSerializer.Serialize(new[] { newEntry }),
                };
                db.Packages.Add(pkg);
                byTracking[key] = pkg;
                added++;
            }
        }

        await db.SaveChangesAsync(ct);
        return new PackageSyncResult(scanned.Count, added, updated);
    }

    private record PackageHistoryEntry(string status, string text, string time, string at);

    private static List<PackageHistoryEntry> DeserializeHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<PackageHistoryEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<PackageHistoryEntry>>(json) ?? new List<PackageHistoryEntry>();
        }
        catch
        {
            return new List<PackageHistoryEntry>();
        }
    }

    private static DateOnly? ParseDateOnly(string? iso) => DateOnly.TryParse(iso, out var d) ? d : null;
}
