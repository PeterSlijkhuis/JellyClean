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
        return new Remover(catalog, new ArrClient(factory.Object), new RecycleBin(_library.Object, new ArrClient(Mock.Of<IHttpClientFactory>()), NullLogger<RecycleBin>.Instance), _library.Object, sessions.Object, Mock.Of<IActivityManager>(), NullLogger<Remover>.Instance);
    }

    [Fact]
    public async Task Leaving_soon_drops_items_that_became_protected()
    {
        var remover = Remover();
        var catalog = new Catalog(_library.Object, Mock.Of<IUserManager>(u => u.GetUsers() == Array.Empty<Jellyfin.Database.Implementations.Entities.User>()), Mock.Of<IUserDataManager>(), NullLogger<Catalog>.Instance);
        var leaving = new LeavingSoon(catalog, remover, Mock.Of<MediaBrowser.Controller.Collections.ICollectionManager>(), _library.Object, NullLogger<LeavingSoon>.Instance);
        _config.UseCollection = false;
        _config.LeavingSoon = [new LeavingItem { Id = _movie.Id, Title = "Alien", Source = "you" }];

        await leaving.ReconcileAsync(CancellationToken.None);
        Assert.Single(_config.LeavingSoon);

        _config.KeptIds = [_movie.Id];
        await leaving.ReconcileAsync(CancellationToken.None);
        Assert.Empty(_config.LeavingSoon);
    }

    [Fact]
    public async Task Recycle_bin_moves_restores_and_purges()
    {
        var lib = Path.Combine(_root, "movies");
        var folder = Path.Combine(lib, "Alien (1979)");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "Alien.mkv"), "x", TestContext.Current.CancellationToken);
        _library.Setup(l => l.GetVirtualFolders()).Returns([new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Movies", Locations = [lib] }]);
        var bin = new RecycleBin(_library.Object, new ArrClient(Mock.Of<IHttpClientFactory>()), NullLogger<RecycleBin>.Instance);
        var item = new CatalogItem { Id = _movie.Id, Name = "Alien", Type = "Movie" };

        var recycled = bin.Move(item, [folder]);
        RecycleBin.Add(recycled);
        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(recycled.Paths[0].To, "Alien.mkv")));

        Assert.Null(bin.Restore(item.Id));
        Assert.True(File.Exists(Path.Combine(folder, "Alien.mkv")));
        Assert.Empty(_config.Recycled);

        RecycleBin.Add(bin.Move(item, [folder]));
        _config.RecycleDays = 30;
        Assert.Equal(0, await bin.PurgeAsync(all: false, CancellationToken.None));
        Assert.Equal(1, await bin.PurgeAsync(all: true, CancellationToken.None));
        Assert.Empty(_config.Recycled);
        Assert.Equal([".ignore"], Directory.EnumerateFileSystemEntries(Path.Combine(lib, RecycleBin.FolderName)).Select(Path.GetFileName));
        Assert.Equal(1, Assert.Single(_config.FreedByMonth).Count);
    }

    [Fact]
    public async Task Undo_last_brings_back_only_the_last_removal()
    {
        var lib = Path.Combine(_root, "movies");
        _library.Setup(l => l.GetVirtualFolders()).Returns([new MediaBrowser.Model.Entities.VirtualFolderInfo { Name = "Movies", Locations = [lib] }]);
        var bin = new RecycleBin(_library.Object, new ArrClient(Mock.Of<IHttpClientFactory>()), NullLogger<RecycleBin>.Instance);
        RecycledItem Recycle(string name, DateTime when)
        {
            var folder = Path.Combine(lib, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".mkv"), "x");
            var r = bin.Move(new CatalogItem { Id = Guid.NewGuid(), Name = name, Type = "Movie" }, [folder]);
            (r.Removed, r.Batch) = (when, Guid.NewGuid());
            RecycleBin.Add(r);
            return r;
        }

        var older = Recycle("Alien", DateTime.UtcNow.AddDays(-1));
        Recycle("Dune", DateTime.UtcNow);

        var (restored, problems) = await bin.UndoLastAsync(CancellationToken.None);

        Assert.Equal(1, restored);
        Assert.Empty(problems);
        Assert.True(File.Exists(Path.Combine(lib, "Dune", "Dune.mkv")));
        Assert.Equal(older.Id, Assert.Single(_config.Recycled).Id);
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
            await Task.Delay(50, TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task A_season_without_its_own_folder_deletes_its_episodes()
    {
        var season = new MediaBrowser.Controller.Entities.TV.Season { Id = Guid.NewGuid(), Name = "Season 1", IndexNumber = 1 };
        MediaBrowser.Controller.Entities.TV.Episode Episode(int n) => new() { Id = Guid.NewGuid(), Name = "E" + n, SeasonId = season.Id, Path = $"/nowhere/Dark/Dark S01E0{n}.mkv" };
        var episodes = new[] { Episode(1), Episode(2) };
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([season, .. episodes]);
        _library.Setup(l => l.GetItemById(season.Id)).Returns(season);
        foreach (var e in episodes)
        {
            _library.Setup(l => l.GetItemById(e.Id)).Returns(e);
        }

        var plan = await Remover().RemoveAsync([season.Id], Guid.Empty, CancellationToken.None);

        Assert.Single(plan.Remove);
        Assert.Equal(episodes.Select(e => e.Path), plan.Remove[0].Paths);
        foreach (var e in episodes)
        {
            _library.Verify(l => l.DeleteItem(e, It.Is<DeleteOptions>(o => o.DeleteFileLocation)), Times.Once);
        }
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
