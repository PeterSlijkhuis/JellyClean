using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyClean.Configuration;
using Jellyfin.Plugin.JellyClean.Library;
using Xunit;

namespace Jellyfin.Plugin.JellyClean.Tests;

public sealed class DigestTests
{
    [Fact]
    public async Task Digest_goes_out_by_email()
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var received = Smtp(server);
        var config = new PluginConfiguration { Mail = new MailSettings { User = "anna@example.com", Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port, Ssl = false } };

        using var http = new HttpClient();
        var (sent, problems) = await Digest.SendAllAsync(config, http, new DigestMessage("Freed so far: 1 TB.", "<b>Freed so far: 1 TB.</b>", null), TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        Assert.Equal(["anna@example.com"], sent);
        var mail = await received;
        Assert.Contains("RCPT TO:<anna@example.com>", mail, StringComparison.Ordinal);
        Assert.Contains("Freed so far: 1 TB.", mail, StringComparison.Ordinal);
        Assert.Contains("text/html", mail, StringComparison.Ordinal);
        server.Stop();
    }

    [Fact]
    public void Digest_has_only_the_parts_you_picked()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var due = new CatalogItem { Id = Guid.NewGuid(), Name = "Alien", Type = "Movie", Year = 1979, Size = 5_000_000_000, Added = now.AddYears(-2), Rating = 8.5f, RunTimeTicks = TimeSpan.FromHours(2).Ticks };
        var soon = new CatalogItem { Id = Guid.NewGuid(), Name = "Dune", Type = "Movie", Year = 2021, Size = 3_000_000_000, Added = now.AddYears(-2) };
        var items = new Dictionary<Guid, CatalogItem> { [due.Id] = due, [soon.Id] = soon };
        var config = new PluginConfiguration
        {
            LeavingSoon =
            [
                new LeavingItem { Id = due.Id, Title = "Alien (1979)", Due = now.AddDays(-1) },
                new LeavingItem { Id = soon.Id, Title = "Dune (2021)", Due = now.AddDays(3) },
            ],
            DigestParts = ["week", "gem", "disk"],
            JellyfinUrl = "http://jellyfin:8096/",
        };
        var stats = Stats.Build(items.Values.ToList(), [], [], now) with { Disks = [new DiskSpace("/media", 10_000_000_000_000, 1_000_000_000_000, ["Movies"], 0)] };

        var digest = Digest.Write(config, items, stats, now);

        Assert.DoesNotContain("Due now", digest.Text, StringComparison.Ordinal);
        Assert.Contains("Due in the next 7 days: 1 item, 3.0 GB. Dune (2021).", digest.Text, StringComparison.Ordinal);
        Assert.Contains("Hidden gem to watch: Alien (1979). Rated 8.5", digest.Text, StringComparison.Ordinal);
        Assert.Contains("Disk: Movies: 1.0 TB free of 10 TB.", digest.Text, StringComparison.Ordinal);
        Assert.EndsWith("Open JellyClean: http://jellyfin:8096/web/#/configurationpage?name=JellyClean", digest.Text, StringComparison.Ordinal);
        Assert.Contains("href=\"http://jellyfin:8096/web/#/configurationpage?name=JellyClean\"", digest.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Insights_find_gems_decades_and_genres()
    {
        var now = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var gem = new CatalogItem { Id = Guid.NewGuid(), Name = "Heat", Type = "Movie", Year = 1995, Rating = 8.3f, Size = 40, Genres = ["Crime"], Added = now.AddYears(-3), RunTimeTicks = TimeSpan.FromHours(3).Ticks };
        var seen = new CatalogItem { Id = Guid.NewGuid(), Name = "Up", Type = "Movie", Year = 2009, Rating = 8.0f, Size = 60, Genres = ["Crime"], Added = now, AnyPlayed = true, LastWatched = now.AddYears(-3), RunTimeTicks = TimeSpan.FromHours(1).Ticks };

        var lib = Stats.Build([gem, seen], [], [], now).Library;

        Assert.Same(gem, Assert.Single(lib.Gems));
        Assert.Equal(["1990s", "2000s"], lib.Decades.Select(d => d.Name));
        Assert.Equal(new GenreInsight("Crime", 2, 100, 1, 40), Assert.Single(lib.Genres));
        Assert.Equal(0.6, lib.PlayedShare);
        Assert.Same(seen, lib.Hogs[0]);
        Assert.Same(gem, lib.Longest);
        Assert.Equal((1, 60L), (lib.Stale.Count, lib.Stale.Size));
        Assert.Equal((1, 40L), (lib.Untouched.Count, lib.Untouched.Size));
    }

    [Fact]
    public void Disks_come_from_where_the_files_are()
    {
        var now = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc);
        var root = System.IO.Path.GetTempPath();
        CatalogItem File(string library, string type, long size, int monthsAgo) => new() { Id = Guid.NewGuid(), Name = "x", Type = type, Library = library, Size = size, Added = now.AddMonths(-monthsAgo), Path = System.IO.Path.Combine(root, library, "x.mkv") };

        var disks = Stats.Disks(
            [File("Shows", "Episode", 600, 1), File("Movies", "Movie", 600, 2), File("Movies", "Movie", 999, 30), new CatalogItem { Type = "BoxSet", Library = "Collections", Path = "/config/data/collections/x" }],
            now);

        var disk = Assert.Single(disks);
        Assert.Equal(["Movies", "Shows"], disk.Libraries);
        Assert.Equal(100, disk.AddedPerMonth);
        Assert.True(disk.Total > 0);
    }

    // Just enough SMTP to accept one message; returns everything the client said.
    private static async Task<string> Smtp(TcpListener server)
    {
        using var client = await server.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
        var log = new StringBuilder();
        await writer.WriteLineAsync("220 fake");
        var data = false;
        while (await reader.ReadLineAsync() is { } line)
        {
            log.AppendLine(line);
            if (data)
            {
                if (line == ".")
                {
                    data = false;
                    await writer.WriteLineAsync("250 queued");
                }

                continue;
            }

            if (line.StartsWith("DATA", StringComparison.Ordinal))
            {
                data = true;
                await writer.WriteLineAsync("354 go on");
            }
            else if (line.StartsWith("QUIT", StringComparison.Ordinal))
            {
                await writer.WriteLineAsync("221 bye");
                break;
            }
            else
            {
                await writer.WriteLineAsync("250 ok");
            }
        }

        return log.ToString();
    }
}
