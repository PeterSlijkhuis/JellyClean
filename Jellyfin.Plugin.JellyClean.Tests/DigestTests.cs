using System;
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
        var config = new PluginConfiguration { Mail = new MailSettings { To = "anna@example.com", From = "jellyfin@example.com", Host = "127.0.0.1", Port = ((IPEndPoint)server.LocalEndpoint).Port, Ssl = false } };

        using var http = new HttpClient();
        var (sent, problems) = await Digest.SendAllAsync(config, http, "Freed so far: 1 TB.", TestContext.Current.CancellationToken);

        Assert.Empty(problems);
        Assert.Equal(["anna@example.com"], sent);
        var mail = await received;
        Assert.Contains("RCPT TO:<anna@example.com>", mail, StringComparison.Ordinal);
        Assert.Contains("Freed so far: 1 TB.", mail, StringComparison.Ordinal);
        server.Stop();
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
