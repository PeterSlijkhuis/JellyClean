using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>One card in the digest.</summary>
/// <param name="Title">What it is about, like "Due now".</param>
/// <param name="Value">The number that matters, like "3 items, 120 GB".</param>
/// <param name="Detail">A line with more, or null.</param>
/// <param name="Color">The accent color of the card in the email.</param>
public sealed record DigestCard(string Title, string Value, string? Detail, string Color);

/// <summary>The digest, ready to send.</summary>
/// <param name="Text">Plain text, for webhooks and mail clients without HTML.</param>
/// <param name="Html">The email, with cards.</param>
/// <param name="Link">Where the Open JellyClean button goes, or null.</param>
public sealed record DigestMessage(string Text, string Html, string? Link);

/// <summary>
/// A short summary sent to a webhook or by email, with the parts the user picks.
/// </summary>
public static class Digest
{
    /// <summary>The parts a digest can have, in the order they are written. The settings page lists the same ones.</summary>
    public static readonly string[] Parts = ["due", "week", "coming", "rules", "added", "bin", "freed", "disk", "never", "abandoned", "shame", "gem", "users"];

    private const string Red = "#e05a5a";
    private const string Amber = "#f0b429";
    private const string Green = "#66bb6a";
    private const string Blue = "#00a4dc";
    private const string Purple = "#aa5cc3";

    /// <summary>
    /// Writes the digest as text and as an email.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="items">Every catalog item, for sizes.</param>
    /// <param name="stats">The insights.</param>
    /// <param name="now">Now (UTC).</param>
    /// <returns>The message.</returns>
    public static DigestMessage Write(PluginConfiguration config, IReadOnlyDictionary<Guid, CatalogItem> items, StatsResult stats, DateTime now)
    {
        var cards = Cards(config, items, stats, now);
        var link = string.IsNullOrWhiteSpace(config.JellyfinUrl) ? null : config.JellyfinUrl.Trim().TrimEnd('/') + "/web/#/configurationpage?name=JellyClean";
        return new DigestMessage(Text(cards, link), Html(cards, link, now), link);
    }

    /// <summary>
    /// Works out the cards, with the parts chosen in the settings.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="items">Every catalog item, for sizes.</param>
    /// <param name="stats">The insights.</param>
    /// <param name="now">Now (UTC).</param>
    /// <returns>The cards, in order.</returns>
    public static List<DigestCard> Cards(PluginConfiguration config, IReadOnlyDictionary<Guid, CatalogItem> items, StatsResult stats, DateTime now)
    {
        long Size(Guid id) => items.TryGetValue(id, out var i) ? i.Size : 0;
        static string Count(int n, string one, string many) => n.ToString("N0", CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);
        static string? Names(string lead, IEnumerable<string> names, int count)
        {
            var list = names.Take(4).ToList();
            return list.Count == 0 ? null : lead + string.Join(", ", list) + (count > list.Count ? $" and {count - list.Count} more" : string.Empty) + ".";
        }

        var since = config.Digest.LastRun?.ToUniversalTime() ?? now.AddDays(-7);
        var lib = stats.Library;
        var cards = new List<DigestCard>();
        foreach (var key in Parts.Where(config.DigestParts.Contains))
        {
            switch (key)
            {
                case "due":
                    var due = config.LeavingSoon.Where(l => l.Due <= now).ToList();
                    cards.Add(new("Due now", due.Count > 0 ? $"{Count(due.Count, "item", "items")}, {Remover.Bytes(due.Sum(l => Size(l.Id)))}" : "Nothing",
                        Names("Waiting for your review: ", due.Select(l => l.Title), due.Count), due.Count > 0 ? Red : Green));
                    break;
                case "week":
                    var week = config.LeavingSoon.Where(l => l.Due > now && l.Due <= now.AddDays(7)).OrderBy(l => l.Due).ToList();
                    cards.Add(new("Due in the next 7 days", week.Count > 0 ? $"{Count(week.Count, "item", "items")}, {Remover.Bytes(week.Sum(l => Size(l.Id)))}" : "Nothing",
                        Names(string.Empty, week.Select(l => l.Title), week.Count), Amber));
                    break;
                case "coming":
                    cards.Add(new("On Leaving soon", $"{Count(config.LeavingSoon.Length, "item", "items")}, {Remover.Bytes(config.LeavingSoon.Sum(l => Size(l.Id)))}", null, Amber));
                    break;
                case "rules":
                    var runs = config.RuleRuns.Where(r => !r.Dry && r.When > since).GroupBy(r => r.Rule).Select(g => (Rule: g.Key, Added: g.Sum(r => r.Added))).ToList();
                    cards.Add(new("Rules since the last digest", Count(runs.Sum(r => r.Added), "item added", "items added"),
                        runs.Count > 0 ? string.Join(", ", runs.Select(r => $"{r.Rule} {r.Added}")) + "." : "No rule ran.", Purple));
                    break;
                case "added":
                    var added = items.Values.Where(i => i.Type is "Movie" or "Episode" && i.Added > since).ToList();
                    cards.Add(new("Added since the last digest", Remover.Bytes(added.Sum(i => i.Size)),
                        $"{Count(added.Count(i => i.Type == "Movie"), "movie", "movies")}, {Count(added.Count(i => i.Type == "Episode"), "episode", "episodes")}.", Blue));
                    break;
                case "bin":
                    cards.Add(new("Recycle bin", config.Recycled.Length > 0 ? $"{Count(config.Recycled.Length, "item", "items")}, {Remover.Bytes(config.Recycled.Sum(r => r.Size))}" : "Empty",
                        config.Recycled.Length > 0 ? "Can still be recovered on the Recover tab." : null, Green));
                    break;
                case "freed":
                    var month = config.FreedByMonth.FirstOrDefault(m => m.Month == now.ToString("yyyy-MM", CultureInfo.InvariantCulture));
                    cards.Add(new("Freed this month", Remover.Bytes(month?.Bytes ?? 0), $"{Remover.Bytes(stats.FreedThisYear)} this year, {Remover.Bytes(config.FreedBytes)} so far.", Green));
                    break;
                case "disk":
                    foreach (var disk in stats.Disks)
                    {
                        var months = disk.AddedPerMonth > 0 ? disk.Free / (double)disk.AddedPerMonth : double.PositiveInfinity;
                        cards.Add(new(
                            "Disk: " + string.Join(", ", disk.Libraries),
                            $"{Remover.Bytes(disk.Free)} free of {Remover.Bytes(disk.Total)}",
                            months < 1 ? "Full within a month at this pace."
                                : months < 1.5 ? "Full in about a month at this pace."
                                : months < 36 ? $"Full in about {Math.Round(months):0} months at this pace."
                                : "Plenty of room at this pace.",
                            months < 6 ? Red : Blue));
                    }

                    break;
                case "never":
                    var files = items.Values.Where(i => i.Type is "Movie" or "Episode").ToList();
                    cards.Add(new("Never played", Remover.Bytes(files.Where(i => !i.AnyPlayed).Sum(i => i.Size)),
                        $"Of {Remover.Bytes(files.Sum(i => i.Size))}, {Math.Round(stats.NeverPlayedHours):0} hours nobody started.", Purple));
                    break;
                case "abandoned":
                    cards.Add(new("Series left halfway", lib.AbandonedTotal.Count > 0 ? $"{Count(lib.AbandonedTotal.Count, "series", "series")}, {Remover.Bytes(lib.AbandonedTotal.Size)}" : "None",
                        Names(string.Empty, lib.Abandoned.Select(i => i.Title), lib.AbandonedTotal.Count), Amber));
                    break;
                case "shame":
                    if (stats.Shame.FirstOrDefault() is { } big)
                    {
                        cards.Add(new("Biggest never played", big.Title, $"{Remover.Bytes(big.Size)}, added {big.Added:d MMM yyyy}.", Red));
                    }

                    break;
                case "gem":
                    if (lib.Gems.Length > 0)
                    {
                        var gem = lib.Gems[now.DayOfYear % lib.Gems.Length];
                        cards.Add(new("Hidden gem to watch", gem.Title, $"Rated {gem.Rating:0.0}, and nobody has played it yet.", Amber));
                    }

                    break;
                case "users":
                    var people = stats.Users.Where(u => u.HoursThisYear > 0).ToList();
                    if (people.Count > 0)
                    {
                        cards.Add(new("Watched the most this year", $"{people[0].Name}, {Math.Round(people[0].HoursThisYear):0} h",
                            people.Count > 1 ? string.Join(", ", people.Skip(1).Select(u => $"{u.Name} {Math.Round(u.HoursThisYear):0} h")) + "." : null, Purple));
                    }

                    break;
            }
        }

        return cards;
    }

    /// <summary>
    /// Writes the cards as plain text.
    /// </summary>
    /// <param name="cards">The cards.</param>
    /// <param name="link">The Open JellyClean link, or null.</param>
    /// <returns>The text.</returns>
    public static string Text(IEnumerable<DigestCard> cards, string? link)
    {
        var lines = new List<string> { "JellyClean digest" };
        lines.AddRange(cards.Select(c => $"{c.Title}: {c.Value}." + (c.Detail is null ? string.Empty : " " + c.Detail)));
        if (link is not null)
        {
            lines.Add("Open JellyClean: " + link);
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Writes the cards as an email that looks like the JellyClean pages. Styles are inline, because mail clients drop style sheets.
    /// </summary>
    /// <param name="cards">The cards.</param>
    /// <param name="link">The Open JellyClean link, or null.</param>
    /// <param name="now">Now, for the date.</param>
    /// <returns>The HTML.</returns>
    public static string Html(IReadOnlyList<DigestCard> cards, string? link, DateTime now)
    {
        static string E(string text) => WebUtility.HtmlEncode(text);
        const string Font = "font-family:-apple-system,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;";
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><meta name=\"color-scheme\" content=\"dark\"></head>")
            .Append("<body style=\"margin:0;padding:0;background:#101014;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#101014;\"><tr><td align=\"center\" style=\"padding:24px 12px;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;\">")
            .Append("<tr><td style=\"border-radius:16px;padding:22px 24px;background:#5a6fd0;background-image:linear-gradient(135deg,#aa5cc3,#00a4dc);").Append(Font).Append("\">")
            .Append("<div style=\"color:#ffffff;font-size:13px;opacity:0.85;letter-spacing:0.06em;text-transform:uppercase;\">JellyClean digest</div>")
            .Append("<div style=\"color:#ffffff;font-size:24px;font-weight:700;margin-top:4px;\">").Append(E(now.ToLocalTime().ToString("dddd d MMMM", CultureInfo.InvariantCulture))).Append("</div>")
            .Append("</td></tr><tr><td style=\"height:14px;\"></td></tr>");

        for (var i = 0; i < cards.Count; i += 2)
        {
            html.Append("<tr><td><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\"><tr>");
            for (var j = i; j < i + 2; j++)
            {
                html.Append("<td width=\"50%\" valign=\"top\" style=\"padding:").Append(j == i ? "0 6px 12px 0" : "0 0 12px 6px").Append(";\">");
                if (j < cards.Count)
                {
                    var c = cards[j];
                    html.Append("<div style=\"background:#1c1c22;border:1px solid #2c2c34;border-left:4px solid ").Append(c.Color).Append(";border-radius:12px;padding:14px 16px;").Append(Font).Append("\">")
                        .Append("<div style=\"color:#9a9aa6;font-size:12px;text-transform:uppercase;letter-spacing:0.05em;\">").Append(E(c.Title)).Append("</div>")
                        .Append("<div style=\"color:#ffffff;font-size:19px;font-weight:700;margin-top:4px;line-height:1.3;\">").Append(E(c.Value)).Append("</div>");
                    if (c.Detail is not null)
                    {
                        html.Append("<div style=\"color:#b8b8c2;font-size:13px;margin-top:4px;line-height:1.45;\">").Append(E(c.Detail)).Append("</div>");
                    }

                    html.Append("</div>");
                }

                html.Append("</td>");
            }

            html.Append("</tr></table></td></tr>");
        }

        if (link is not null)
        {
            html.Append("<tr><td align=\"center\" style=\"padding:10px 0 4px;\">")
                .Append("<a href=\"").Append(E(link)).Append("\" style=\"display:inline-block;padding:13px 26px;border-radius:10px;background:#5a6fd0;background-image:linear-gradient(135deg,#aa5cc3,#00a4dc);color:#ffffff;text-decoration:none;font-weight:700;font-size:15px;").Append(Font).Append("\">Open JellyClean</a>")
                .Append("</td></tr>");
        }

        html.Append("<tr><td align=\"center\" style=\"padding:16px 0 0;color:#6e6e7a;font-size:12px;").Append(Font).Append("\">Change what this says under JellyClean, Settings, Digest.</td></tr>")
            .Append("</table></td></tr></table></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// Sends the text: as {"content"} to Discord, {"text"} to Slack, plain text to ntfy, and both fields to anything else.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The webhook address.</param>
    /// <param name="message">The digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task SendAsync(HttpClient client, string url, DigestMessage message, CancellationToken cancellationToken)
    {
        var ntfy = url.Contains("ntfy", StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url))
        {
            Content = ntfy ? new StringContent(message.Text, Encoding.UTF8, "text/plain") : JsonContent.Create(new { content = message.Text, text = message.Text, title = "JellyClean digest" }),
        };
        if (ntfy)
        {
            request.Headers.Add("Title", "JellyClean digest");
            if (message.Link is not null)
            {
                request.Headers.Add("Click", message.Link);
            }
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Whether email is set up: a mail server and someone to send to.
    /// </summary>
    /// <param name="mail">The mail settings.</param>
    /// <returns>True when it can send.</returns>
    public static bool MailOn(MailSettings mail)
        => !string.IsNullOrWhiteSpace(mail.Host) && !string.IsNullOrWhiteSpace(mail.To.Length > 0 ? mail.To : mail.User);

    /// <summary>
    /// Sends the digest by email, as HTML with the plain text next to it.
    /// </summary>
    /// <param name="mail">The mail server and the address to send to.</param>
    /// <param name="message">The digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task MailAsync(MailSettings mail, DigestMessage message, CancellationToken cancellationToken)
    {
        // SmtpClient does STARTTLS but not implicit TLS on port 465; MailKit would add 465 if it is ever needed.
        using var client = new SmtpClient(mail.Host.Trim(), mail.Port)
        {
            EnableSsl = mail.Ssl,
            Credentials = mail.User.Length > 0 ? new NetworkCredential(mail.User.Trim(), mail.Password.Replace(" ", string.Empty, StringComparison.Ordinal)) : null,
        };
        var to = mail.To.Trim().Length > 0 ? mail.To.Trim() : mail.User.Trim();
        using var email = new MailMessage(mail.From.Trim().Length > 0 ? mail.From.Trim() : mail.User.Trim(), to) { Subject = "JellyClean digest", Body = message.Text };
        email.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(message.Html, Encoding.UTF8, MediaTypeNames.Text.Html));
        await client.SendMailAsync(email, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the digest everywhere it is set up to go: the webhook, email, or both.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="client">An HTTP client for the webhook.</param>
    /// <param name="message">The digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where it went, and what went wrong.</returns>
    public static async Task<(string[] Sent, string[] Problems)> SendAllAsync(PluginConfiguration config, HttpClient client, DigestMessage message, CancellationToken cancellationToken)
    {
        var sent = new List<string>();
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.DigestUrl))
        {
            try
            {
                await SendAsync(client, config.DigestUrl.Trim(), message, cancellationToken).ConfigureAwait(false);
                sent.Add("the webhook");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or FormatException)
            {
                problems.Add("Webhook: " + ex.Message);
            }
        }

        if (MailOn(config.Mail))
        {
            try
            {
                await MailAsync(config.Mail, message, cancellationToken).ConfigureAwait(false);
                sent.Add(config.Mail.To.Trim().Length > 0 ? config.Mail.To.Trim() : config.Mail.User.Trim());
            }
            catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or ArgumentException or TaskCanceledException)
            {
                problems.Add("Email: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        return (sent.ToArray(), problems.ToArray());
    }
}
