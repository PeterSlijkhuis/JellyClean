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
/// A short summary sent to a webhook: what is due, what is in the recycle bin, what was freed.
/// </summary>
public static class Digest
{
    /// <summary>
    /// Writes the digest.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="items">Every catalog item, for sizes.</param>
    /// <param name="now">Now (UTC).</param>
    /// <returns>The text.</returns>
    public static string Text(PluginConfiguration config, IReadOnlyDictionary<Guid, CatalogItem> items, DateTime now)
    {
        long Size(Guid id) => items.TryGetValue(id, out var i) ? i.Size : 0;
        var due = config.LeavingSoon.Where(l => l.Due <= now).ToList();
        var coming = config.LeavingSoon.Where(l => l.Due > now).ToList();
        var files = items.Values.Where(i => i.Type is "Movie" or "Episode").ToList();
        var month = config.FreedByMonth.FirstOrDefault(m => m.Month == now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        var lines = new List<string>
        {
            "JellyClean digest",
            (due.Count > 0 ? $"Leaving soon: {due.Count} due now ({Remover.Bytes(due.Sum(l => Size(l.Id)))}), waiting for your review" : "Leaving soon: nothing due")
                + (coming.Count > 0 ? $", {coming.Count} coming up ({Remover.Bytes(coming.Sum(l => Size(l.Id)))})." : "."),
        };
        if (config.Recycled.Length > 0)
        {
            lines.Add($"Recycle bin: {config.Recycled.Length} items, {Remover.Bytes(config.Recycled.Sum(r => r.Size))}, can still be recovered.");
        }

        lines.Add($"Freed this month: {Remover.Bytes(month?.Bytes ?? 0)}. Freed so far: {Remover.Bytes(config.FreedBytes)}.");
        lines.Add($"Never played: {Remover.Bytes(files.Where(i => !i.AnyPlayed).Sum(i => i.Size))} of {Remover.Bytes(files.Sum(i => i.Size))}.");
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
