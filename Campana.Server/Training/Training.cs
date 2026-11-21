using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Campana.Core;
using Campana.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Campana.Server.Training;

public sealed record TopicInfo(string Id, string Titulo, string? Descripcion, int Episodios);
public sealed record EpisodeInfo(string Id, string Tema, int Numero, string Titulo, string? Descripcion, long Bytes, string ContentType, string Url);
public sealed record DeckSlide(string Titulo, string Archivo, string? Notas);
public sealed record DeckInfo(string Id, string Titulo, string? Tema, IReadOnlyList<DeckSlide> Diapositivas);
public sealed record EpisodeText(string? Titulo, string? Descripcion);
public sealed record TopicText(string? Titulo, string? Descripcion);

/// <summary>
/// Contenido de la autocapacitación: audios tipo podcast (una carpeta por tema) y presentaciones (una carpeta por presentación).
/// Se lee del disco en cada consulta: basta copiar archivos a las carpetas para publicarlos. Nada de lo que llega del cliente forma parte de una ruta.
/// </summary>
public sealed class TrainingCatalog
{
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".wav"] = "audio/wav", [".ogg"] = "audio/ogg",
    };
    private static readonly Dictionary<string, string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
    };
    private static readonly Regex IdRx = new("^[a-z0-9][a-z0-9-]{0,80}$", RegexOptions.Compiled);

    public string AudioRoot { get; }
    public string SlidesRoot { get; }

    public TrainingCatalog(IOptions<CampanaOptions> opts)
    {
        var o = opts.Value;
        var data = Path.GetFullPath(o.DataRoot);
        AudioRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(o.Training.AudioRoot) ? Path.Combine(data, "capacitacion", "audio") : o.Training.AudioRoot);
        SlidesRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(o.Training.SlidesRoot) ? Path.Combine(data, "capacitacion", "diapositivas") : o.Training.SlidesRoot);
    }

    public static bool IsValidId(string? id) => id is not null && IdRx.IsMatch(id);

    /// <summary>"CancerMama" → "cancer-mama".</summary>
    public static string TopicIdFor(string folderName)
        => Normalizer.Slug(Regex.Replace(folderName, "(?<=[a-z0-9])(?=[A-Z])", " "));

    private static T? ReadJson<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), CampanaJson.Compact) : null; }
        catch (JsonException) { return null; }
    }

    private static string TitleFromFile(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        name = Regex.Replace(name, @"^\d+[-_ ]*", "");
        name = Regex.Replace(name, @"\s+\d+$", "");
        name = name.Replace('-', ' ').Replace('_', ' ').Trim();
        return name.Length == 0 ? file : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private IEnumerable<(string Dir, string TopicId)> TopicDirs()
    {
        if (!Directory.Exists(AudioRoot)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(AudioRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var id = TopicIdFor(Path.GetFileName(dir));
            if (IsValidId(id)) yield return (dir, id);
        }
    }

    public IReadOnlyList<TopicInfo> Topics() => TopicDirs().Select(t =>
    {
        var text = ReadJson<TopicText>(Path.Combine(t.Dir, "tema.json"));
        var count = Directory.EnumerateFiles(t.Dir).Count(f => AudioTypes.ContainsKey(Path.GetExtension(f)));
        return new TopicInfo(t.TopicId, text?.Titulo ?? TitleFromFile(Path.GetFileName(t.Dir)), text?.Descripcion, count);
    }).ToList();

    public IReadOnlyList<EpisodeInfo> Episodes(string? topicId = null)
    {
        var list = new List<EpisodeInfo>();
        foreach (var (dir, tid) in TopicDirs())
        {
            if (topicId is not null && tid != topicId) continue;
            var texts = ReadJson<Dictionary<string, EpisodeText>>(Path.Combine(dir, "episodios.json")) ?? new();
            var used = new HashSet<string>();
            var files = Directory.EnumerateFiles(dir).Where(f => AudioTypes.ContainsKey(Path.GetExtension(f))).OrderBy(f => f, StringComparer.Ordinal).ToList();
            var fallback = 0;
            foreach (var f in files)
            {
                fallback++;
                var m = Regex.Match(Path.GetFileName(f), @"^(\d+)");
                var number = m.Success ? int.Parse(m.Value) : fallback;
                var id = $"{tid}-{number:D2}";
                for (var n = 2; !used.Add(id); n++) id = $"{tid}-{number:D2}-{n}";
                texts.TryGetValue(Path.GetFileName(f), out var text);
                list.Add(new EpisodeInfo(id, tid, number, text?.Titulo ?? TitleFromFile(f), text?.Descripcion, new FileInfo(f).Length,
                    AudioTypes[Path.GetExtension(f)], $"/capacitacion/episodios/{id}/audio"));
            }
        }
        return list.OrderBy(e => e.Tema, StringComparer.Ordinal).ThenBy(e => e.Numero).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public (string Path, string ContentType)? Episode(string id)
    {
        if (!IsValidId(id)) return null;
        foreach (var (dir, tid) in TopicDirs())
        {
            if (!id.StartsWith(tid + "-", StringComparison.Ordinal)) continue;
            var ep = Episodes(tid).FirstOrDefault(e => e.Id == id);
            if (ep is null) continue;
            // Se vuelve a buscar el archivo recorriendo la carpeta: el id nunca se usa para armar la ruta.
            var used = new HashSet<string>();
            var fallback = 0;
            foreach (var f in Directory.EnumerateFiles(dir).Where(f => AudioTypes.ContainsKey(Path.GetExtension(f))).OrderBy(f => f, StringComparer.Ordinal))
            {
                fallback++;
                var m = Regex.Match(Path.GetFileName(f), @"^(\d+)");
                var number = m.Success ? int.Parse(m.Value) : fallback;
                var candidate = $"{tid}-{number:D2}";
                for (var n = 2; !used.Add(candidate); n++) candidate = $"{tid}-{number:D2}-{n}";
                if (candidate == id) return (f, AudioTypes[Path.GetExtension(f)]);
            }
        }
        return null;
    }

    public IReadOnlyList<DeckInfo> Decks()
    {
        var list = new List<DeckInfo>();
        if (!Directory.Exists(SlidesRoot)) return list;
        foreach (var dir in Directory.EnumerateDirectories(SlidesRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var deck = Deck(Path.GetFileName(dir));
            if (deck is not null) list.Add(deck);
        }
        return list;
    }

    public DeckInfo? Deck(string id)
    {
        if (!IsValidId(id)) return null;
        var dir = Path.Combine(SlidesRoot, id);
        var raw = ReadJson<DeckFile>(Path.Combine(dir, "deck.json"));
        if (raw is null) return null;
        var slides = (raw.Diapositivas ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Archivo)).ToList();
        return new DeckInfo(id, raw.Titulo ?? id, raw.Tema, slides);
    }

    private sealed record DeckFile(string? Titulo, string? Tema, List<DeckSlide>? Diapositivas);

    /// <summary>Número de diapositiva desde 1.</summary>
    public (string Path, string ContentType)? Slide(string deckId, int number)
    {
        var deck = Deck(deckId);
        if (deck is null || number < 1 || number > deck.Diapositivas.Count) return null;
        var file = deck.Diapositivas[number - 1].Archivo;
        try { SafePath.ValidateSegment(file); }
        catch (DomainException) { return null; }
        if (!ImageTypes.TryGetValue(Path.GetExtension(file), out var type)) return null;
        var full = Path.Combine(SlidesRoot, deckId, file);
        return File.Exists(full) ? (full, type) : null;
    }
}

public sealed record PresentationState(string CampaignId, string? DeckId, int Index, int Total, string? Titulo, DateTimeOffset UpdatedAt, string? UpdatedBy);

public static class PresentationEvents
{
    public const string SlideChanged = "DiapositivaCambiada";
}

/// <summary>Estado de la presentación en grupo de cada campaña. Es lo que ven en pantalla las personas de la comunidad.</summary>
public sealed class PresentationService
{
    private readonly TrainingCatalog _catalog;
    private readonly IClock _clock;
    private readonly ConcurrentDictionary<string, PresentationState> _states = new();

    public PresentationService(TrainingCatalog catalog, IClock clock) { _catalog = catalog; _clock = clock; }

    public PresentationState Get(string campaignId)
        => _states.TryGetValue(campaignId, out var s) ? s : new PresentationState(campaignId, null, 0, 0, null, default, null);

    /// <param name="index">Posición desde 1.</param>
    public PresentationState Set(string campaignId, string deckId, int index, string by)
    {
        var deck = _catalog.Deck(deckId) ?? throw DomainException.NotFound($"No existe la presentación '{deckId}'.");
        if (index < 1 || index > deck.Diapositivas.Count)
            throw DomainException.Validation($"La diapositiva debe estar entre 1 y {deck.Diapositivas.Count}.");
        var state = new PresentationState(campaignId, deckId, index, deck.Diapositivas.Count, deck.Diapositivas[index - 1].Titulo, _clock.UtcNow, by);
        _states[campaignId] = state;
        return state;
    }
}

/// <summary>Cambia la diapositiva y avisa a todas las pantallas conectadas. Lo usan tanto el hub como la API REST.</summary>
public sealed class PresentationCoordinator
{
    private readonly PresentationService _service;
    private readonly ICampaignStore _campaigns;
    private readonly IHubContext<PresentationHub> _hub;

    public PresentationCoordinator(PresentationService service, ICampaignStore campaigns, IHubContext<PresentationHub> hub)
    { _service = service; _campaigns = campaigns; _hub = hub; }

    public static string Group(string campaignId) => "campana:" + campaignId;

    public async Task<Campaign> RequireOperatingAsync(string campaignId, CancellationToken ct)
    {
        Ids.RequireCampaignId(campaignId);
        var c = await _campaigns.GetAsync(campaignId, ct) ?? throw DomainException.NotFound("No existe la campaña.");
        if (!c.IsOperating)
            throw new DomainException(ErrorCode.CampaignState, $"Las presentaciones solo se proyectan con la campaña en campo (etapas concientizacion o consulta). Etapa actual: '{c.Estado}'.");
        return c;
    }

    public async Task<PresentationState> SetAsync(ClaimsPrincipal principal, string campaignId, string deckId, int index, CancellationToken ct)
    {
        var user = CurrentUser.From(principal);
        if (user.Role is not (Roles.CoordinadorCampana or Roles.CoordinadorGeneral))
            throw new DomainException(ErrorCode.Forbidden, "Solo la coordinación puede controlar la presentación.");
        user.EnsureCampaignAccess(campaignId);
        await RequireOperatingAsync(campaignId, ct);
        var state = _service.Set(campaignId, deckId, index, user.Username);
        await _hub.Clients.Group(Group(campaignId)).SendAsync(PresentationEvents.SlideChanged, state, ct);
        return state;
    }

    public PresentationState Get(string campaignId) => _service.Get(campaignId);
}

/// <summary>
/// Hub de SignalR: las pantallas de la comunidad se unen sin iniciar sesión y reciben cada cambio de diapositiva;
/// quien presenta (coordinación de la campaña) lo controla con un token.
/// </summary>
public sealed class PresentationHub : Hub
{
    private readonly PresentationCoordinator _coordinator;
    public PresentationHub(PresentationCoordinator coordinator) => _coordinator = coordinator;

    public async Task<PresentationState> Unirse(string campaignId)
    {
        try
        {
            await _coordinator.RequireOperatingAsync(campaignId, Context.ConnectionAborted);
            await Groups.AddToGroupAsync(Context.ConnectionId, PresentationCoordinator.Group(campaignId));
            return _coordinator.Get(campaignId);
        }
        catch (DomainException ex) { throw new HubException(ex.Message); }
    }

    public Task Salir(string campaignId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, PresentationCoordinator.Group(campaignId));

    /// <param name="index">Posición desde 1.</param>
    public async Task<PresentationState> CambiarDiapositiva(string campaignId, string deckId, int index)
    {
        try { return await _coordinator.SetAsync(Context.User ?? new ClaimsPrincipal(), campaignId, deckId, index, Context.ConnectionAborted); }
        catch (DomainException ex) { throw new HubException(ex.Message); }
    }
}
