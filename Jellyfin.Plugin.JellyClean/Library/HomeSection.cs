using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyClean.Library;

/// <summary>
/// What the Home Screen Sections plugin sends when it asks for a row.
/// </summary>
public class HomeSectionPayload
{
    /// <summary>Gets or sets the user the home screen is for.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets extra data, unused.</summary>
    public string? AdditionalData { get; set; }
}

/// <summary>
/// The Leaving soon row on the home screen, through the Home Screen Sections plugin when it is installed.
/// </summary>
public class HomeSection
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IDtoService _dtoService;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeSection"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="dtoService">Dto service.</param>
    public HomeSection(ILibraryManager libraryManager, IUserManager userManager, IDtoService dtoService)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _dtoService = dtoService;
    }

    /// <summary>
    /// The items leaving soon that this user can see, the first to go first.
    /// </summary>
    /// <param name="payload">Who it is for.</param>
    /// <returns>The row.</returns>
    public QueryResult<BaseItemDto> GetResults(HomeSectionPayload payload)
    {
        var user = _userManager.GetUserById(payload.UserId);
        var items = (Plugin.Instance?.Configuration.LeavingSoon ?? [])
            .OrderBy(l => l.Due)
            .Select(l => _libraryManager.GetItemById(l.Id))
            .Where(i => i is not null && (user is null || i.IsVisible(user)))
            .Select(i => i!)
            .ToList();
        var dtos = _dtoService.GetBaseItemDtos(items, new DtoOptions(), user);
        return new QueryResult<BaseItemDto>(null, dtos.Count, dtos);
    }
}

/// <summary>
/// Adds the row to Home Screen Sections at startup. Does nothing when that plugin is not installed.
/// </summary>
public class HomeSectionRegistration : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<HomeSectionRegistration> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HomeSectionRegistration"/> class.
    /// </summary>
    /// <param name="lifetime">Server lifetime.</param>
    /// <param name="logger">Logger.</param>
    public HomeSectionRegistration(IHostApplicationLifetime lifetime, ILogger<HomeSectionRegistration> logger)
    {
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>
    /// Gets the fixed id of the row, so its order and settings stick.
    /// </summary>
    public static Guid Id { get; } = Guid.Parse("3c1f6a52-8d0e-4b7a-9f2c-5e4d1a6b7c80");

    /// <summary>
    /// The registration Home Screen Sections expects.
    /// </summary>
    /// <returns>The JSON.</returns>
    public static string Payload() => JsonSerializer.Serialize(new
    {
        id = Id.ToString(),
        displayText = "Leaving soon",
        limit = 1,
        additionalData = string.Empty,
        resultsAssembly = typeof(HomeSection).Assembly.FullName,
        resultsClass = typeof(HomeSection).FullName,
        resultsMethod = nameof(HomeSection.GetResults)
    });

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Wait until every plugin, Home Screen Sections included, has started.
        _lifetime.ApplicationStarted.Register(Register);
        return Task.CompletedTask;
    }

    private void Register()
    {
        try
        {
            var register = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.FullName?.Contains(".HomeScreenSections", StringComparison.Ordinal) == true)
                .Select(a => a.GetType("Jellyfin.Plugin.HomeScreenSections.PluginInterface"))
                .FirstOrDefault(t => t is not null)
                ?.GetMethod("RegisterSection", BindingFlags.Public | BindingFlags.Static);
            if (register is null)
            {
                return;
            }

            // It takes a Newtonsoft JObject; parse through its own type so we need no reference to it.
            var parse = register.GetParameters()[0].ParameterType.GetMethod("Parse", [typeof(string)]);
            register.Invoke(null, [parse!.Invoke(null, [Payload()])]);
            _logger.LogInformation("Added the Leaving soon row to Home Screen Sections");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not add the Leaving soon row to Home Screen Sections");
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
