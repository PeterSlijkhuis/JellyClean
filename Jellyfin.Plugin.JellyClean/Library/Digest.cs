using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// A short summary sent to a webhook or by email, with the parts the user picks.
/// </summary>
public static class Digest
{
    /// <summary>The parts a digest can have, in the order they are written. The settings page lists the same ones.</summary>
    public static readonly string[] Parts = ["due", "week", "coming", "rules", "added", "bin", "freed", "disk", "never", "abandoned", "shame", "gem", "users"];

    /// <summary>
    /// Writes the digest, with the parts chosen in the settings.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="items">Every catalog item, for sizes.</param>
    /// <param name="stats">The insights.</param>
    /// <param name="now">Now (UTC).</param>
    /// <returns>The text.</returns>
    public static string Text(PluginConfiguration config, IReadOnlyDictionary<Guid, CatalogItem> items, StatsResult stats, DateTime now)
    {
        long Size(Guid id) => items.TryGetValue(id, out var i) ? i.Size : 0;
        static string Names(IEnumerable<string> names, int count)
        {
            var list = names.Take(4).ToList();
            return list.Count == 0 ? string.Empty : ": " + string.Join(", ", list) + (count > list.Count ? $" and {count - list.Count} more" : string.Empty);
        }

        var since = config.Digest.LastRun?.ToUniversalTime() ?? now.AddDays(-7);
        var lib = stats.Library;
        var lines = new List<string> { "JellyClean digest" };
        foreach (var key in Parts.Where(config.DigestParts.Contains))
        {
            switch (key)
            {
                case "due":
                    var due = config.LeavingSoon.Where(l => l.Due <= now).ToList();
                    lines.Add(due.Count > 0 ? $"Due now: {due.Count} on Leaving soon, {Remover.Bytes(due.Sum(l => Size(l.Id)))}, waiting for your review{Names(due.Select(l => l.Title), due.Count)}." : "Due now: nothing.");
                    break;
                case "week":
                    var week = config.LeavingSoon.Where(l => l.Due > now && l.Due <= now.AddDays(7)).OrderBy(l => l.Due).ToList();
                    lines.Add(week.Count > 0 ? $"Due in the next 7 days: {week.Count}, {Remover.Bytes(week.Sum(l => Size(l.Id)))}{Names(week.Select(l => l.Title), week.Count)}." : "Due in the next 7 days: nothing.");
                    break;
                case "coming":
                    lines.Add($"Leaving soon in all: {config.LeavingSoon.Length} items, {Remover.Bytes(config.LeavingSoon.Sum(l => Size(l.Id)))}.");
                    break;
                case "rules":
                    var runs = config.RuleRuns.Where(r => !r.Dry && r.When > since).GroupBy(r => r.Rule).Select(g => $"{g.Key} {g.Sum(r => r.Added)}").ToList();
                    lines.Add(runs.Count > 0 ? "Rules added since the last digest: " + string.Join(", ", runs) + "." : "Rules added nothing since the last digest.");
                    break;
                case "added":
                    var added = items.Values.Where(i => i.Type is "Movie" or "Episode" && i.Added > since).ToList();
                    lines.Add($"Added since the last digest: {added.Count(i => i.Type == "Movie")} movies, {added.Count(i => i.Type == "Episode")} episodes, {Remover.Bytes(added.Sum(i => i.Size))}.");
                    break;
                case "bin":
                    lines.Add(config.Recycled.Length > 0 ? $"Recycle bin: {config.Recycled.Length} items, {Remover.Bytes(config.Recycled.Sum(r => r.Size))}, can still be recovered." : "Recycle bin: empty.");
                    break;
                case "freed":
                    var month = config.FreedByMonth.FirstOrDefault(m => m.Month == now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
                    lines.Add($"Freed: {Remover.Bytes(month?.Bytes ?? 0)} this month, {Remover.Bytes(stats.FreedThisYear)} this year, {Remover.Bytes(config.FreedBytes)} so far.");
                    break;
                case "disk":
                    foreach (var disk in stats.Disks)
                    {
                        var months = lib.AddedPerMonth > 0 ? disk.Free / (double)lib.AddedPerMonth : double.PositiveInfinity;
                        lines.Add($"Disk {disk.Name}: {Remover.Bytes(disk.Free)} free of {Remover.Bytes(disk.Total)}"
                            + (months < 1 ? ", full within a month at this pace." : months < 1.5 ? ", full in about a month at this pace." : months < 36 ? $", full in about {Math.Round(months):0} months at this pace." : "."));
                    }

                    break;
                case "never":
                    var files = items.Values.Where(i => i.Type is "Movie" or "Episode").ToList();
                    lines.Add($"Never played: {Remover.Bytes(files.Where(i => !i.AnyPlayed).Sum(i => i.Size))} of {Remover.Bytes(files.Sum(i => i.Size))}, {Math.Round(stats.NeverPlayedHours):0} hours.");
                    break;
                case "abandoned":
                    lines.Add(lib.AbandonedTotal.Count > 0 ? $"Series left halfway for half a year: {lib.AbandonedTotal.Count}, {Remover.Bytes(lib.AbandonedTotal.Size)}{Names(lib.Abandoned.Select(i => i.Title), lib.AbandonedTotal.Count)}." : "No series left halfway.");
                    break;
                case "shame":
                    if (stats.Shame.FirstOrDefault() is { } big)
                    {
                        lines.Add($"Biggest never played: {big.Title}, {Remover.Bytes(big.Size)}.");
                    }

                    break;
                case "gem":
                    if (lib.Gems.Length > 0)
                    {
                        var gem = lib.Gems[now.DayOfYear % lib.Gems.Length];
                        lines.Add($"Something you own and nobody played: {gem.Title}, rated {gem.Rating:0.0}.");
                    }

                    break;
                case "users":
                    var people = stats.Users.Where(u => u.HoursThisYear > 0).Select(u => $"{u.Name} {Math.Round(u.HoursThisYear):0} h").ToList();
                    if (people.Count > 0)
                    {
                        lines.Add("Hours this year: " + string.Join(", ", people) + ".");
                    }

                    break;
            }
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Sends the text: as {"content"} to Discord, {"text"} to Slack, plain text to ntfy, and both fields to anything else.
    /// </summary>
    /// <param name="client">The HTTP client.</param>
    /// <param name="url">The webhook address.</param>
    /// <param name="text">The text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task SendAsync(HttpClient client, string url, string text, CancellationToken cancellationToken)
    {
        using HttpContent body = url.Contains("ntfy", StringComparison.OrdinalIgnoreCase)
            ? new StringContent(text, Encoding.UTF8, "text/plain")
            : JsonContent.Create(new { content = text, text, title = "JellyClean digest" });
        using var response = await client.PostAsync(new Uri(url), body, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sends the digest by email.
    /// </summary>
    /// <param name="mail">The mail server and the address to send to.</param>
    /// <param name="text">The digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public static async Task MailAsync(MailSettings mail, string text, CancellationToken cancellationToken)
    {
        // SmtpClient does STARTTLS but not implicit TLS on port 465; MailKit would add 465 if it is ever needed.
        using var client = new SmtpClient(mail.Host.Trim(), mail.Port)
        {
            EnableSsl = mail.Ssl,
            Credentials = mail.User.Length > 0 ? new NetworkCredential(mail.User, mail.Password) : null,
        };
        using var message = new MailMessage(mail.From.Length > 0 ? mail.From.Trim() : mail.User.Trim(), mail.To.Trim()) { Subject = "JellyClean digest", Body = text };
        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the digest everywhere it is set up to go: the webhook, email, or both.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="client">An HTTP client for the webhook.</param>
    /// <param name="text">The digest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where it went, and what went wrong.</returns>
    public static async Task<(string[] Sent, string[] Problems)> SendAllAsync(PluginConfiguration config, HttpClient client, string text, CancellationToken cancellationToken)
    {
        var sent = new List<string>();
        var problems = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.DigestUrl))
        {
            try
            {
                await SendAsync(client, config.DigestUrl.Trim(), text, cancellationToken).ConfigureAwait(false);
                sent.Add("the webhook");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
            {
                problems.Add("Webhook: " + ex.Message);
            }
        }

        if (!string.IsNullOrWhiteSpace(config.Mail.To))
        {
            try
            {
                await MailAsync(config.Mail, text, cancellationToken).ConfigureAwait(false);
                sent.Add(config.Mail.To.Trim());
            }
            catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or ArgumentException or TaskCanceledException)
            {
                problems.Add("Email: " + (ex.InnerException?.Message ?? ex.Message));
            }
        }

        return (sent.ToArray(), problems.ToArray());
    }
}
