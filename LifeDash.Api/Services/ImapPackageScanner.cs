using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace LifeDash.Api.Services;

public record ScannedPackageDto(
    string Id,
    string Title,
    string Carrier,
    string TrackingNumber,
    string? Sender,
    string Status,
    string? ExpectedDelivery,
    string LatestEvent,
    string LatestEventTime,
    string RecipientEmail,
    string Source,
    string UpdatedAt
)
{
    // Not serialized into the DB — only used to merge several mails about the same shipment.
    [System.Text.Json.Serialization.JsonIgnore] public DateTimeOffset MessageDate { get; init; }
    [System.Text.Json.Serialization.JsonIgnore] public bool StatusRecognized { get; init; }
}

public class ImapPackageScanner
{
    private readonly MailTrackingOptions _options;
    private readonly MicrosoftMailAuthService _msAuth;
    private readonly ILogger<ImapPackageScanner> _logger;

    public ImapPackageScanner(IOptions<MailTrackingOptions> options, MicrosoftMailAuthService msAuth, ILogger<ImapPackageScanner> logger)
    {
        _options = options.Value;
        _msAuth = msAuth;
        _logger = logger;
    }

    // fullHistory: true does a one-off deep scan of each folder's full history (slow, meant for
    // the initial backfill right after connecting). false (the normal, everyday case) only looks
    // at the last MailTracking:DefaultLookbackDays days, which is fast and avoids Exchange's IMAP
    // throttling on repeated scans.
    public async Task<List<ScannedPackageDto>> ScanMailboxAsync(bool fullHistory = false, CancellationToken ct = default)
    {
        var results = new Dictionary<string, ScannedPackageDto>(StringComparer.OrdinalIgnoreCase);
        var maxPerFolder = Math.Max(1, _options.MaxMessagesPerFolder);
        var lookbackSince = DateTime.UtcNow.AddDays(-Math.Max(1, _options.DefaultLookbackDays));

        foreach (var mailbox in _options.GetMailboxes())
        {
            var email = mailbox.Email.Trim();
            var password = mailbox.AppPassword?.Replace(" ", "").Trim();

            // Microsoft retired Basic Auth (app passwords) for Outlook.com/Live/Hotmail IMAP in
            // September 2024 — those mailboxes log in with the OAuth (XOAUTH2) token from the
            // Microsoft sign-in, everything else (e.g. Gmail) with its app password.
            var accessToken = mailbox.UseMicrosoftOAuth ? await _msAuth.TryGetAccessTokenAsync(ct) : null;

            if (accessToken is null && string.IsNullOrWhiteSpace(password))
            {
                _logger.LogWarning("Mailbox {Email}: neither a Microsoft OAuth connection nor an IMAP app password is configured.", email);
                continue;
            }

            var folderNames = mailbox.FoldersToScan is { Length: > 0 } own ? own
                : _options.FoldersToScan is { Length: > 0 } configured ? configured : ["INBOX"];

            // Exchange Online enforces some per-session cap on total IMAP commands/duration — scanning
            // several folders back-to-back on one connection can get disconnected mid-way (observed even
            // with a single SEARCH + a few hundred FETCHes per folder). Using one fresh connection per
            // folder keeps each session short, and a failure in one folder never discards results already
            // found in another.
            foreach (var folderName in folderNames)
            {
                try
                {
                    var folderResults = await ScanFolderAsync(mailbox, email, accessToken, password, folderName, fullHistory, lookbackSince, maxPerFolder, ct);
                    foreach (var parsed in folderResults)
                    {
                        var key = parsed.TrackingNumber.ToUpperInvariant();
                        if (!results.TryGetValue(key, out var current) || IsBetterUpdate(parsed, current))
                            results[key] = parsed;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scanning folder '{Folder}' of {Email} failed; keeping results from other folders.", folderName, email);
                }
            }
        }

        return results.Values.OrderByDescending(p => p.ExpectedDelivery ?? "").ToList();
    }

    // Several mails can mention the same tracking number (shipping confirmation, "kommt heute",
    // "zugestellt", review requests, ...). The newest mail with a recognisable status wins, so an
    // older "zugestellt" can never be overridden by stale info and — more importantly — a plain
    // status update can no longer win just because its body happened to contain a delivery keyword.
    private static bool IsBetterUpdate(ScannedPackageDto candidate, ScannedPackageDto current)
    {
        if (candidate.StatusRecognized != current.StatusRecognized) return candidate.StatusRecognized;
        // A real "delivered" mail is final; don't let a later unrelated mail (e.g. a rating request) undo it.
        if (current.Status == "delivered" && candidate.Status != "delivered") return false;
        if (candidate.Status == "delivered" && current.Status != "delivered") return true;
        return candidate.MessageDate > current.MessageDate;
    }

    private async Task<List<ScannedPackageDto>> ScanFolderAsync(
        MailboxOptions mailbox, string email, string? accessToken, string? password, string folderName,
        bool fullHistory, DateTime lookbackSince, int maxPerFolder, CancellationToken ct)
    {
        var found = new List<ScannedPackageDto>();

        using var client = new ImapClient();
        client.Timeout = 60000;

        await client.ConnectAsync(mailbox.ImapHost, mailbox.ImapPort, mailbox.UseSsl, ct);

        if (accessToken is not null)
            await client.AuthenticateAsync(new SaslMechanismOAuth2(email, accessToken), ct);
        else
            await client.AuthenticateAsync(email, password!, ct);

        var folder = string.Equals(folderName, "INBOX", StringComparison.OrdinalIgnoreCase)
            ? client.Inbox
            : await client.GetFolderAsync(folderName, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var uids = fullHistory
            ? await folder.SearchAsync(SearchQuery.All, ct)
            : await folder.SearchAsync(SearchQuery.DeliveredAfter(lookbackSince), ct);
        var targetUids = uids.OrderByDescending(u => u.Id).Take(maxPerFolder).ToList();

        foreach (var uid in targetUids)
        {
            try
            {
                var message = await folder.GetMessageAsync(uid, ct);
                if (message == null) continue;

                var parsed = ParsePackageFromMessage(message, email);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.TrackingNumber))
                    found.Add(parsed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to parse message UID {Uid} in folder '{Folder}'", uid, folderName);
            }
        }

        try
        {
            await client.DisconnectAsync(true, ct);
        }
        catch
        {
            // best-effort — we already have what we came for
        }

        return found;
    }

    private ScannedPackageDto? ParsePackageFromMessage(MimeMessage message, string recipientEmail)
    {
        var from = message.From.ToString();
        var subject = message.Subject ?? "";
        var textBody = message.TextBody ?? "";
        var htmlBody = message.HtmlBody ?? "";
        var combined = $"{from} {subject} {textBody} {htmlBody}";

        // 1. Detect carrier
        string carrier = DetectCarrier(from, subject, combined);

        // 2. Extract tracking number
        string? trackingNumber = ExtractTrackingNumber(carrier, combined);
        if (string.IsNullOrWhiteSpace(trackingNumber))
        {
            return null;
        }

        // 3. Detect merchant/sender
        string? senderName = DetectSender(from, subject);

        // 4. Derive clean title
        string title = subject.Replace("Fwd:", "", StringComparison.OrdinalIgnoreCase)
                              .Replace("Wg:", "", StringComparison.OrdinalIgnoreCase)
                              .Replace("Re:", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(senderName))
        {
            title = $"{senderName} Lieferung";
        }
        else if (title.Length > 45)
        {
            title = $"Sendung {carrier.ToUpper()} ({trackingNumber[^Math.Min(6, trackingNumber.Length)..]})";
        }

        // 5. Detect status — only from the subject and the visible body text. The raw HTML
        //    (hidden preheaders, footers, "Was tun, wenn ... nicht zugestellt wurde?" links) caused
        //    plain status-update mails to be marked as delivered.
        var visibleBody = !string.IsNullOrWhiteSpace(textBody) ? textBody : HtmlToText(htmlBody);
        var detectedStatus = DetectStatus(subject, visibleBody);
        string status = detectedStatus ?? "in_transit";

        // 6. Detect expected delivery date
        string? expectedDate = ExtractDeliveryDate(combined, message.Date.DateTime);

        var dateStr = message.Date.LocalDateTime.ToString("dd.MM., HH:mm") + " Uhr";

        return new ScannedPackageDto(
            Id: $"pkg-{trackingNumber}",
            Title: title,
            Carrier: carrier,
            TrackingNumber: trackingNumber,
            Sender: senderName,
            Status: status,
            ExpectedDelivery: expectedDate,
            LatestEvent: subject,
            LatestEventTime: dateStr,
            RecipientEmail: recipientEmail,
            Source: "email_scan",
            UpdatedAt: DateTime.UtcNow.ToString("o")
        )
        {
            MessageDate = message.Date,
            StatusRecognized = detectedStatus is not null,
        };
    }

    private static string DetectCarrier(string from, string subject, string content)
    {
        // Deutsche Post letter mail ("Brief"/"Einschreiben") — checked before the DHL/Deutsche
        // Post Paket check below, since deutschepost.de also sends DHL parcel notifications.
        var mentionsBrief = Regex.IsMatch(subject, @"\b(brief|einschreiben)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(content, @"\b(brief|einschreiben)\b", RegexOptions.IgnoreCase);
        var mentionsPaket = Regex.IsMatch(content, @"\bpaket\b", RegexOptions.IgnoreCase);
        if (mentionsBrief && !mentionsPaket) return "deutschepost";

        if (Regex.IsMatch(from, @"@(dhl|deutschepost|paket)\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bdhl\b", RegexOptions.IgnoreCase))
            return "dhl";

        if (Regex.IsMatch(from, @"@dpd\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bdpd\b", RegexOptions.IgnoreCase))
            return "dpd";

        if (Regex.IsMatch(from, @"@(myhermes|hermesworld)\.", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bhermes\b", RegexOptions.IgnoreCase))
            return "hermes";

        if (Regex.IsMatch(from, @"@gls-", RegexOptions.IgnoreCase) ||
            Regex.IsMatch(subject, @"\bgls\b", RegexOptions.IgnoreCase))
            return "gls";

        if (Regex.IsMatch(content, @"\bdhl\b", RegexOptions.IgnoreCase)) return "dhl";
        if (Regex.IsMatch(content, @"\bdpd\b", RegexOptions.IgnoreCase)) return "dpd";
        if (Regex.IsMatch(content, @"\bhermes\b", RegexOptions.IgnoreCase)) return "hermes";
        if (Regex.IsMatch(content, @"\bgls\b", RegexOptions.IgnoreCase)) return "gls";

        return "dhl";
    }

    private static string? ExtractTrackingNumber(string carrier, string content)
    {
        Match match;
        switch (carrier.ToLowerInvariant())
        {
            case "dhl":
                match = Regex.Match(content, @"(?:sendungsnummer|sendungscode|tracking(?:-?nr|\.?| number)?|sendung)[:\s#]*([0-9]{12,20}|JJD[0-9A-Z]{12,20}|JD[0-9]{18})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(0034[0-9]{16})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(JJD[0-9]{16,20})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "dpd":
                match = Regex.Match(content, @"(?:paketnummer|sendungsnummer|tracking(?:-?nr|\.?| number)?|dpd)[:\s#]*([0-9]{14})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(0142[0-9]{10}|0144[0-9]{10}|0134[0-9]{10}|0154[0-9]{10})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "hermes":
                match = Regex.Match(content, @"(?:sendungsnummer|quittungsnummer|auftragsnummer|tracking)[:\s#]*([0-9]{11,14}|H100[0-9]{16})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(H100[0-9]{16})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(01[0-9]{12})\b");
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "gls":
                match = Regex.Match(content, @"(?:paketnummer|sendungsnummer|tracking(?:-?nr|\.?| number)?|gls)[:\s#]*([0-9A-Z]{8,12})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                match = Regex.Match(content, @"\b(GLS[0-9A-Z]{8,10})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();
                break;

            case "deutschepost":
                // Einschreiben/registered-letter reference numbers follow the UPU S10 format
                // (2 letters + 9 digits + 2 letters, e.g. "RR123456785DE").
                match = Regex.Match(content, @"(?:sendungsnummer|einschreiben-?nr\.?|referenznummer)[:\s#]*([A-Z]{2}[0-9]{9}[A-Z]{2})", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().ToUpperInvariant();
                match = Regex.Match(content, @"\b([A-Z]{2}[0-9]{9}[A-Z]{2})\b", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim().ToUpperInvariant();
                break;
        }

        // Generic fallback — require a handful of digits so it doesn't latch onto a stray word
        // near "tracking"/"Sendung" in unrelated boilerplate (e.g. a legal footer).
        match = Regex.Match(content, @"(?:sendungsnummer|tracking(?:-?nr|\.?| number)?|tracking code)[:\s#]*([0-9A-Za-z]{10,24})", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var candidate = match.Groups[1].Value.Trim();
            if (candidate.Count(char.IsDigit) >= 3) return candidate;
        }

        return null;
    }

    private static string? DetectSender(string from, string subject)
    {
        var text = $"{from} {subject}";
        if (Regex.IsMatch(text, @"amazon", RegexOptions.IgnoreCase)) return "Amazon";
        if (Regex.IsMatch(text, @"zalando", RegexOptions.IgnoreCase)) return "Zalando";
        if (Regex.IsMatch(text, @"otto", RegexOptions.IgnoreCase)) return "OTTO";
        if (Regex.IsMatch(text, @"mediamarkt", RegexOptions.IgnoreCase)) return "MediaMarkt";
        if (Regex.IsMatch(text, @"saturn", RegexOptions.IgnoreCase)) return "Saturn";
        if (Regex.IsMatch(text, @"ikea", RegexOptions.IgnoreCase)) return "IKEA";
        if (Regex.IsMatch(text, @"notebooksbilliger", RegexOptions.IgnoreCase)) return "Notebooksbilliger";
        if (Regex.IsMatch(text, @"ebay", RegexOptions.IgnoreCase)) return "eBay";
        if (Regex.IsMatch(text, @"asos", RegexOptions.IgnoreCase)) return "ASOS";
        if (Regex.IsMatch(text, @"about\s*you", RegexOptions.IgnoreCase)) return "About You";
        return null;
    }

    private const RegexOptions Rx = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Problems: failed attempt, returned, delayed. Checked first because these mails usually also
    // contain the word "zugestellt" ("konnte nicht zugestellt werden").
    private static readonly Regex ExceptionPattern = new(
        @"(konnte|könnte)\s+(\w+\s+){0,3}nicht\s+(zugestellt|geliefert|übergeben)" +
        @"|zustellversuch\s+(war\s+)?(leider\s+)?(nicht\s+erfolgreich|erfolglos)|nicht\s+(angetroffen|antreffen)" +
        @"|sendung\s+(ist|wird)\s+verzögert|rücksendung\s+an|zurück\s+an\s+(den\s+)?absender" +
        @"|adresse\s+(ist\s+)?(unvollständig|fehlerhaft)|could\s+not\s+be\s+delivered|delivery\s+attempt\s+failed", Rx);

    // Only explicit past-tense confirmations count as delivered. "wird heute zugestellt",
    // "voraussichtlich zugestellt", "sobald ... zugestellt wurde" and "Ablage beim Nachbarn möglich"
    // are status updates, not a completed delivery.
    private static readonly Regex DeliveredPattern = new(
        @"\b(wurde|wurden|ist|sind|haben\s+wir|hat)\s+(heute\s+|gerade\s+|soeben\s+|bereits\s+|erfolgreich\s+|am\s+\d{1,2}\.\d{1,2}\.(\d{2,4})?\s+(um\s+\d{1,2}[:.]\d{2}\s*(uhr)?\s+)?)*" +
        @"(zugestellt|ausgeliefert|übergeben|abgegeben|abgestellt|eingelegt|geliefert)\b" +
        @"|\berfolgreich\s+(zugestellt|übergeben|ausgeliefert)" +
        @"|\b(wurde|ist)\s+(\w+\s+){0,4}(an|bei)\s+(ihren|ihrem|deinen|deinem|einen|einem|den|dem)\s+nachbarn?\s+(\w+\s+){0,3}(zugestellt|abgegeben|übergeben)" +
        @"|\b(has\s+been|was)\s+delivered\b", Rx);

    // Subjects are short and reliable ("Zugestellt: ...", "Paket zugestellt"). Not used on the body,
    // where FAQ footers like "Sendung zugestellt, aber nicht erhalten?" are common.
    private static readonly Regex DeliveredSubjectPattern = new(
        @"^\s*(\w+:\s*)?(zugestellt|geliefert|delivered)\b|\b(sendung|paket|lieferung|bestellung)\s+zugestellt\b", Rx);

    private static readonly Regex OutForDeliveryPattern = new(
        @"in\s+zustellung|zustellfahrzeug|wird\s+(voraussichtlich\s+)?heute\s+(zugestellt|geliefert)|heute\s+geliefert|kommt\s+heute" +
        @"|heute\s+bei\s+(ihnen|dir)|zustellung\s+heute|zustellzeitfenster|out\s+for\s+delivery|arriving\s+today" +
        @"|abholbereit|zur\s+abholung\s+bereit|liegt\s+(\w+\s+){0,4}zur\s+abholung|abholung\s+in\s+(der|einer)\s+(filiale|packstation)", Rx);

    private static readonly Regex InTransitPattern = new(
        @"im\s+paketzentrum|weitertransport|logistikzentrum|sortiert|auf\s+dem\s+weg|unterwegs|versandt|verschickt|versendet" +
        @"|bearbeitet|wird\s+(\w+\s+){0,3}(zugestellt|geliefert)|voraussichtlich" +
        @"|wurde\s+an\s+(den\s+)?(versanddienstleister|paketdienst|zusteller)\s+übergeben|shipped|in\s+transit", Rx);

    private static readonly Regex AnnouncedPattern = new(
        @"angekündigt|elektronisch\s+übermittelt|auftragsdaten\s+erhalten|sendungsdaten\s+erhalten|bestellbestätigung|vorbereitet", Rx);

    // Returns null when no status could be recognised, so callers can tell a real update apart
    // from an unrelated mail that merely mentions the tracking number.
    internal static string? DetectStatus(string subject, string body)
    {
        // The subject is the most reliable signal — evaluate it on its own first.
        var fromSubject = DetectStatusIn(subject, isSubject: true);
        if (fromSubject is not null) return fromSubject;
        return DetectStatusIn(body, isSubject: false);
    }

    private static string? DetectStatusIn(string text, bool isSubject)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Regex.Replace(text, @"\s+", " ");

        if (ExceptionPattern.IsMatch(text)) return "exception";
        if (OutForDeliveryPattern.IsMatch(text) && !DeliveredPattern.IsMatch(text)) return "out_for_delivery";
        if (DeliveredPattern.IsMatch(text) || (isSubject && DeliveredSubjectPattern.IsMatch(text))) return "delivered";
        if (OutForDeliveryPattern.IsMatch(text)) return "out_for_delivery";
        if (InTransitPattern.IsMatch(text)) return "in_transit";
        if (AnnouncedPattern.IsMatch(text)) return "announced";
        return null;
    }

    internal static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = Regex.Replace(html, @"<(style|script|head|title)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        // Hidden preheaders (display:none) often contain marketing/FAQ text, not the actual status.
        text = Regex.Replace(text, @"<(div|span|td|p)[^>]*display\s*:\s*none[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>|</(p|div|tr|li|h\d)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"[ \t\r\f\v\u00A0]+", " ").Trim();
    }

    private static string? ExtractDeliveryDate(string content, DateTime fallbackDate)
    {
        var isoMatch = Regex.Match(content, @"\b(202[4-9]-[01][0-9]-[0-3][0-9])\b");
        if (isoMatch.Success) return isoMatch.Groups[1].Value;

        var deMatch = Regex.Match(content, @"\b([0-3]?[0-9])\.([0-1]?[0-9])\.(202[4-9])?\b");
        if (deMatch.Success)
        {
            var day = deMatch.Groups[1].Value.PadLeft(2, '0');
            var month = deMatch.Groups[2].Value.PadLeft(2, '0');
            var year = !string.IsNullOrEmpty(deMatch.Groups[3].Value) ? deMatch.Groups[3].Value : DateTime.UtcNow.Year.ToString();
            return $"{year}-{month}-{day}";
        }

        if (Regex.IsMatch(content, @"heute|voraussichtlich heute", RegexOptions.IgnoreCase))
            return DateTime.UtcNow.ToString("yyyy-MM-dd");

        if (Regex.IsMatch(content, @"morgen|voraussichtlich morgen", RegexOptions.IgnoreCase))
            return DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

        return fallbackDate.ToString("yyyy-MM-dd");
    }
}
