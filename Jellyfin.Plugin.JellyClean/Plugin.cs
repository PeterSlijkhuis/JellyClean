using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyClean.Configuration;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyClean;

/// <summary>
/// The JellyClean plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "JellyClean";

    /// <inheritdoc />
    public override string Description => "See what is in your libraries, filter it, and clean up in bulk.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b0be751c-a689-46fd-bb83-4540a41cd649");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.libraryPage.html",
                EnableInMainMenu = true,
                MenuIcon = "cleaning_services"
            },
            new PluginPageInfo
            {
                Name = "JellyCleanSettings",
                DisplayName = "JellyClean settings",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.settingsPage.html"
            },
            new PluginPageInfo
            {
                Name = "JellyCleanLeaving",
                DisplayName = "JellyClean leaving soon",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.leavingPage.html"
            },
            new PluginPageInfo
            {
                Name = "JellyCleanRecover",
                DisplayName = "JellyClean recover",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.recoverPage.html"
            },
            new PluginPageInfo
            {
                Name = "JellyCleanActivity",
                DisplayName = "JellyClean activity",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.activityPage.html"
            }
        ];
    }
}

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<Catalog>();
        serviceCollection.AddSingleton<ArrClient>();
        serviceCollection.AddSingleton<RecycleBin>();
        serviceCollection.AddSingleton<Remover>();
        serviceCollection.AddSingleton<LeavingSoon>();
    }
}
