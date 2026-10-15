using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.JellyClean.Configuration;
using Jellyfin.Plugin.JellyClean.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public sealed class RemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyclean-" + Guid.NewGuid().ToString("N"));
    private readonly Movie _movie = new() { Id = Guid.NewGuid(), Name = "Alien", Path = "/nowhere/Alien (1979)/Alien.mkv", DateCreated = DateTime.UtcNow.AddYears(-3) };
    private readonly Mock<ILibraryManager> _library = new();
    private readonly PluginConfiguration _config = new();
    private string _radarrUrl = "http://radarr:7878";
    private bool _radarrCalled;

    public RemoverTests()
    {
        var appPaths = Mock.Of<IApplicationPaths>(p => p.PluginsPath == Path.Combine(_root, "plugins") && p.PluginConfigurationsPath == Path.Combine(_root, "config"));
        var xml = new Mock<IXmlSerializer>();
        xml.Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(_config);
        _ = new Plugin(appPaths, xml.Object);

        _movie.ProviderIds["Tmdb"] = "348";
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([_movie]);
        _library.Setup(l => l.GetVirtualFolders()).Returns([]);
        _library.Setup(l => l.GetItemById(_movie.Id)).Returns(_movie);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private Remover Remover()
    {
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUsers()).Returns([]);
        var catalog = new Catalog(_library.Object, users.Object, Mock.Of<IUserDataManager>(), NullLogger<Catalog>.Instance);

        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new Radarr(this)));
        var sessions = new Mock<ISessionManager>();
        sessions.Setup(s => s.Sessions).Returns([]);

        _config.Servers = [new ArrServer { Kind = "Radarr", Url = _radarrUrl, ApiKey = "key" }];
        return new Remover(catalog, new ArrClient(factory.Object), _library.Object, sessions.Object, Mock.Of<IActivityManager>(), NullLogger<Remover>.Instance);
    }

    [Fact]
    public async Task Removes_from_radarr_first_then_deletes_from_disk()
    {
        var plan = await Remover().RemoveAsync([_movie.Id], Guid.Empty, CancellationToken.None);

        Assert.True(_radarrCalled);
        Assert.Single(plan.Remove);
        _library.Verify(l => l.DeleteItem(_movie, It.Is<DeleteOptions>(o => o.DeleteFileLocation)), Times.Once);
    }

    [Fact]
    public async Task Start_runs_in_the_background_and_reports_progress()
    {
        var remover = Remover();
        Assert.True(remover.Start([_movie.Id], Guid.Empty));
        for (var i = 0; i < 100 && remover.Progress.Running; i++)
        {
            await Task.Delay(50);
        }

        Assert.False(remover.Progress.Running);
        Assert.Equal(1, remover.Progress.Done);
        Assert.Single(remover.Progress.Result!.Remove);
    }

    [Fact]
    public async Task Files_stay_when_radarr_cannot_be_reached()
    {
        _radarrUrl = "http://down:7878";
        var plan = await Remover().RemoveAsync([_movie.Id], Guid.Empty, CancellationToken.None);

        Assert.Empty(plan.Remove);
        Assert.StartsWith("Could not reach Radarr", plan.Skip.Single().Reason, StringComparison.Ordinal);
        _library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task Preview_changes_nothing()
    {
        var plan = await Remover().PlanAsync([_movie.Id], CancellationToken.None);

        Assert.Equal("Remove the movie from Radarr", plan.Remove.Single().Arr.Single().Description);
        Assert.False(_radarrCalled);
        _library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    [Fact]
    public async Task Kept_items_are_never_deleted()
    {
        _config.KeptIds = [_movie.Id];
        var plan = await Remover().RemoveAsync([_movie.Id], Guid.Empty, CancellationToken.None);

        Assert.Empty(plan.Remove);
        Assert.False(_radarrCalled);
        _library.Verify(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()), Times.Never);
    }

    // Lists one movie; records the delete call.
    private sealed class Radarr(RemoverTests test) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "down")
            {
                throw new HttpRequestException("Connection refused");
            }

            if (request.Method == HttpMethod.Delete)
            {
                test._radarrCalled = true;
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.Method != HttpMethod.Get ? string.Empty
                    : request.RequestUri.AbsolutePath.EndsWith("/queue", StringComparison.Ordinal) ? """{"records":[]}"""
                    : """[{"id":7,"tmdbId":348}]""")
            });
        }
    }
}
