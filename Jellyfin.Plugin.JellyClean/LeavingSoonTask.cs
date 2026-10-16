using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean;

/// <summary>
/// Every hour: empties what has been in the recycle bin long enough, runs the saved views whose time has come,
/// and removes due items that are set to go on their own. Everything else waits for you.
/// </summary>
public class LeavingSoonTask : IScheduledTask
{
    private readonly LeavingSoon _leavingSoon;
    private readonly RecycleBin _bin;
    private readonly Catalog _catalog;
    private readonly IHttpClientFactory _http;
    private readonly IUserManager _users;
    private readonly ILogger<LeavingSoonTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeavingSoonTask"/> class.
    /// </summary>
    /// <param name="leavingSoon">Leaving soon list.</param>
    /// <param name="bin">Recycle bin.</param>
    /// <param name="catalog">Catalog, for the digest.</param>
    /// <param name="http">HTTP client factory, for the digest.</param>
    /// <param name="users">User manager, for the digest.</param>
    /// <param name="logger">Logger.</param>
    public LeavingSoonTask(LeavingSoon leavingSoon, RecycleBin bin, Catalog catalog, IHttpClientFactory http, IUserManager users, ILogger<LeavingSoonTask> logger)
    {
        _leavingSoon = leavingSoon;
        _bin = bin;
        _catalog = catalog;
        _http = http;
        _users = users;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Leaving soon and recycle bin";

    /// <inheritdoc />
    public string Key => "JellyCleanLeavingSoon";

    /// <inheritdoc />
    public string Description => "Empties what has been in the recycle bin long enough. Runs the scheduled views whose time has come, putting their matches on the Leaving soon list. Removes due items only where that is switched on, and sends the digest when it is time.";

    /// <inheritdoc />
    public string Category => "JellyClean";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _bin.PurgeAsync(all: false, cancellationToken).ConfigureAwait(false);
        await _leavingSoon.RunAsync(cancellationToken).ConfigureAwait(false);
        await SendDigestAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SendDigestAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var now = DateTime.Now;
        if ((string.IsNullOrWhiteSpace(config.DigestUrl) && !Digest.MailOn(config.Mail)) || config.Digest.NextRun(now) is not { } next || next > now)
        {
            return;
        }

        if (config.Digest.LastRun is null)
        {
            // Just switched on: the first one goes out at its own day and hour, not right away.
            config.Digest.LastRun = now;
            Plugin.Instance.SaveConfiguration();
            return;
        }

        var (sent, problems) = await Digest.SendAllAsync(config, _http.CreateClient(), Digest.Write(config, _catalog.Get(), Stats.For(_catalog, _users), DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        if (sent.Length > 0)
        {
            Activity.Info(_logger, "Sent the digest to {To}", string.Join(" and ", sent));
        }

        foreach (var problem in problems)
        {
            Activity.Log(_logger, LogLevel.Warning, null, "Could not send the digest. {Problem}", problem);
        }

        config.Digest.LastRun = now;
        Plugin.Instance.SaveConfiguration();
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }];
}
