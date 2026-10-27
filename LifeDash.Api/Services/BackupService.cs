using System.IO.Compression;
using System.Net;
using System.Net.Mail;
using System.Text.Json;
using LifeDash.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LifeDash.Api.Services;

public record BackupArchive(string FileName, byte[] Content, int DocumentFiles, int MissingFiles);

public enum BackupMailResult { Sent, NotConfigured, Failed }

// Builds a ZIP with all of one user's records (data.json) plus the uploaded document files,
// for the manual download in the app and the weekly backup mail.
public class BackupService(LifeDashContext db, IOptions<ReminderEmailOptions> mailOptions, ILogger<BackupService> logger)
{
    // Attachments above this size are rejected by most mail providers (Gmail: 25 MB incl. encoding).
    private const long MaxMailAttachmentBytes = 18L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<BackupArchive> CreateAsync(int userId, CancellationToken ct)
    {
        var tripIds = await db.Trips.Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync(ct);
        var caseIds = await db.AuthorityCases.Where(c => c.UserId == userId).Select(c => c.Id).ToListAsync(ct);
        var appointmentIds = await db.Appointments.Where(a => a.UserId == userId).Select(a => a.Id).ToListAsync(ct);
        var documents = await db.Documents.AsNoTracking().Where(d => d.UserId == userId).ToListAsync(ct);

        var data = new Dictionary<string, object>
        {
            ["exportedAtUtc"] = DateTime.UtcNow,
            ["userId"] = userId,
            ["schemaVersion"] = db.Database.GetAppliedMigrations().LastOrDefault() ?? "",
            ["familyMembers"] = await db.FamilyMembers.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["documents"] = documents,
            ["appointments"] = await db.Appointments.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["appointmentAttendees"] = await db.AppointmentAttendees.AsNoTracking().Where(x => appointmentIds.Contains(x.AppointmentId)).ToListAsync(ct),
            ["importantDates"] = await db.ImportantDates.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["authorityCases"] = await db.AuthorityCases.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["requiredDocuments"] = await db.RequiredDocuments.AsNoTracking().Where(x => caseIds.Contains(x.AuthorityCaseId)).ToListAsync(ct),
            ["incomes"] = await db.Incomes.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["fixedCosts"] = await db.FixedCosts.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["subscriptions"] = await db.Subscriptions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["payments"] = await db.Payments.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["homeItems"] = await db.HomeItems.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["trips"] = await db.Trips.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["bookings"] = await db.Bookings.AsNoTracking().Where(x => tripIds.Contains(x.TripId)).ToListAsync(ct),
            ["packingItems"] = await db.PackingItems.AsNoTracking().Where(x => tripIds.Contains(x.TripId)).ToListAsync(ct),
            ["tasks"] = await db.Tasks.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["packages"] = await db.Packages.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["categoryOptions"] = await db.CategoryOptions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
            ["savingsEntries"] = await db.SavingsEntries.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct),
        };

        var files = 0;
        var missing = 0;
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("data.json", CompressionLevel.Optimal);
            await using (var stream = entry.Open())
                await JsonSerializer.SerializeAsync(stream, data, Json, ct);

            foreach (var doc in documents.Where(d => !string.IsNullOrWhiteSpace(d.StoragePath)))
            {
                if (!File.Exists(doc.StoragePath))
                {
                    missing++;
                    continue;
                }
                // Prefix with the document id so two uploads named "scan.pdf" don't collide.
                var name = $"documents/{doc.Id}_{SafeName(doc.OriginalName ?? Path.GetFileName(doc.StoragePath!))}";
                zip.CreateEntryFromFile(doc.StoragePath!, name, CompressionLevel.Optimal);
                files++;
            }
        }

        var fileName = $"gentlelife-backup-{DateTime.UtcNow:yyyy-MM-dd}.zip";
        return new BackupArchive(fileName, buffer.ToArray(), files, missing);
    }

    public async Task<BackupMailResult> MailAsync(int userId, CancellationToken ct)
    {
        var o = mailOptions.Value;
        if (string.IsNullOrWhiteSpace(o.SmtpHost) || string.IsNullOrWhiteSpace(o.SmtpUser)
            || string.IsNullOrWhiteSpace(o.SmtpPassword) || string.IsNullOrWhiteSpace(o.FromEmail)
            || string.IsNullOrWhiteSpace(o.ToEmail))
            return BackupMailResult.NotConfigured;

        var archive = await CreateAsync(userId, ct);
        var tooLarge = archive.Content.LongLength > MaxMailAttachmentBytes;
        // Too big for a mail attachment: send the records alone, the files stay downloadable in the app.
        var attachment = tooLarge ? await CreateDataOnlyAsync(archive, ct) : archive.Content;

        var note = tooLarge
            ? $"Das vollständige Backup ist {archive.Content.LongLength / 1024 / 1024} MB groß und passt nicht in eine Mail. " +
              "Angehängt sind deshalb nur die Daten (data.json); die Dokumente lädst du in GentleLife über „Backup herunterladen“."
            : $"Angehängt: alle Daten und {archive.DocumentFiles} Dokument(e).";
        if (archive.MissingFiles > 0)
            note += $" {archive.MissingFiles} Dokumentdatei(en) wurden auf dem Server nicht gefunden.";

        try
        {
            using var mail = new MailMessage
            {
                Subject = $"GentleLife-Backup vom {DateTime.UtcNow:dd.MM.yyyy}",
                Body = EmailTemplate.Render(
                    "#2f8a43", "📦", "Backup", "Dein wöchentliches Backup", tooLarge ? "Nur Daten" : "Vollständig",
                    note, [("Dateiname", archive.FileName), ("Dokumente", archive.DocumentFiles.ToString())],
                    o.AppBaseUrl, "GentleLife öffnen",
                    "GentleLife schickt dir jede Woche ein Backup deiner Daten."),
                IsBodyHtml = true,
                From = new MailAddress(o.FromEmail!, o.FromName),
            };
            mail.To.Add(new MailAddress(o.ToEmail!));
            mail.Attachments.Add(new Attachment(new MemoryStream(attachment), archive.FileName, "application/zip"));

            using var smtp = new SmtpClient(o.SmtpHost!, o.SmtpPort)
            {
                EnableSsl = true,
                Credentials = new NetworkCredential(o.SmtpUser!, o.SmtpPassword!),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                UseDefaultCredentials = false,
            };
            await smtp.SendMailAsync(mail, ct);
            logger.LogInformation("Backup mail sent ({Bytes} bytes, data only: {DataOnly}).", attachment.Length, tooLarge);
            return BackupMailResult.Sent;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not send the backup mail.");
            return BackupMailResult.Failed;
        }
    }

    private static async Task<byte[]> CreateDataOnlyAsync(BackupArchive full, CancellationToken ct)
    {
        using var source = new ZipArchive(new MemoryStream(full.Content), ZipArchiveMode.Read);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("data.json", CompressionLevel.Optimal);
            await using var from = source.GetEntry("data.json")!.Open();
            await using var to = entry.Open();
            await from.CopyToAsync(to, ct);
        }
        return buffer.ToArray();
    }

    private static string SafeName(string name)
    {
        var cleaned = Path.GetFileName(name.Replace("\r", "").Replace("\n", "").Trim());
        foreach (var c in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(c, '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "datei" : cleaned;
    }
}
