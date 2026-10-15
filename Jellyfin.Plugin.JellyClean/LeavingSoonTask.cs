using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyClean;

/// <summary>
/// Every hour: empties what has been in the recycle bin long enough, runs the saved views whose time has come,
/// and removes due items that are set to go on their own. Everything else waits for you.
/// </summary>
public class LeavingSoonTask : IScheduledTask
{
    private readonly LeavingSoon _leavingSoon;
    private readonly RecycleBin _bin;

    /// <summary>
    /// Initializes a new instance of the <see cref="LeavingSoonTask"/> class.
    /// </summary>
    /// <param name="leavingSoon">Leaving soon list.</param>
    /// <param name="bin">Recycle bin.</param>
    public LeavingSoonTask(LeavingSoon leavingSoon, RecycleBin bin)
    {
        _leavingSoon = leavingSoon;
        _bin = bin;
    }

    /// <inheritdoc />
    public string Name => "Leaving soon and recycle bin";

    /// <inheritdoc />
    public string Key => "JellyCleanLeavingSoon";

    /// <inheritdoc />
    public string Description => "Empties what has been in the recycle bin long enough. Runs the scheduled views whose time has come, putting their matches on the Leaving soon list. Removes due items only where that is switched on.";

    /// <inheritdoc />
    public string Category => "JellyClean";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await _bin.PurgeAsync(all: false, cancellationToken).ConfigureAwait(false);
        await _leavingSoon.RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(1).Ticks }];
}
