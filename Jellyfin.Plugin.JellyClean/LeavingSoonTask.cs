using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyClean;

/// <summary>
/// Once a day: saved views marked "check daily" add new matches to Leaving soon, and due items are removed
/// only when "Remove when due" is switched on. Otherwise they wait for you.
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
    public string Name => "Daily clean-up";

    /// <inheritdoc />
    public string Key => "JellyCleanLeavingSoon";

    /// <inheritdoc />
    public string Description => "Empties what has been in the recycle bin long enough. Adds new matches of saved views marked \"check daily\" to the Leaving soon list. Removes due items only when \"Remove when due\" is on.";

    /// <inheritdoc />
    public string Category => "JellyClean";

    /// <inheritdoc />
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _bin.Purge(all: false);
        return _leavingSoon.RunAsync(cancellationToken);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        => [new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks }];
}
