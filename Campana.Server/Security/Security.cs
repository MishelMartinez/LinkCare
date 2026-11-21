using System.IdentityModel.Tokens.Jwt;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Campana.Core;
using Microsoft.IdentityModel.Tokens;

namespace Campana.Server.Security;

/// <summary>
/// Perfiles del sistema. La paciente o prospecto no tiene perfil: no inicia sesión y solo usa el sitio público de autocapacitación.
/// </summary>
public static class Roles
{
    /// <summary>Ve todas las campañas, las crea, y administra usuarios, historial, minicubos y el fileshare.</summary>
    public const string CoordinadorGeneral = "coordinador-general";
    /// <summary>Administra su campaña: etapas, documentos, tablero, vínculos de expedientes y presentaciones.</summary>
    public const string CoordinadorCampana = "coordinador-campana";
    /// <summary>Registra pacientes y captura visitas, estudios y adjuntos en su consultorio.</summary>
    public const string Medico = "medico";

    public static readonly string[] All = { CoordinadorGeneral, CoordinadorCampana, Medico };
}

public static class Policies
{
    public const string Authenticated = nameof(Authenticated);
    public const string ClinicalRead = nameof(ClinicalRead);       // los tres perfiles
    public const string ClinicalWrite = nameof(ClinicalWrite);     // médico
    public const string CampaignAdmin = nameof(CampaignAdmin);     // coordinador de campaña y general (con alcance por campaña)
    public const string GlobalAdmin = nameof(GlobalAdmin);         // coordinador general
}

/// <summary>PBKDF2-SHA256. Formato: pbkdf2-sha256$iteraciones$sal(base64)$hash(base64).</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations) || iterations < 1) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    private static readonly string Dummy = Hash("dummy", 1000);
    /// <summary>Gasta un tiempo comparable cuando el usuario no existe, para no revelar qué usuarios hay.</summary>
    public static void BurnTime(string password) => Verify(password, Dummy);
}

public interface IUserStore
{
    Task<UserRecord?> FindAsync(string username, CancellationToken ct);
    Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct);
    /// <summary>Crea o reemplaza al usuario.</summary>
    Task UpsertAsync(UserRecord user, CancellationToken ct);
}

public static class UserRules
{
    private static readonly Regex NameRx = new("^[a-z0-9][a-z0-9._-]{2,39}$", RegexOptions.Compiled);

    public static bool IsValidUsername(string? u) => u is not null && NameRx.IsMatch(u);

    public static void Validate(UserRecord u)
    {
        if (!IsValidUsername(u.Username)) throw DomainException.Validation($"Nombre de usuario inválido '{u.Username}' (3 a 40 caracteres: minúsculas, números, punto, guion).");
        if (!Roles.All.Contains(u.Role)) throw DomainException.Validation($"Rol inválido '{u.Role}'. Opciones: {string.Join(", ", Roles.All)}.");
        if (u.ConsultorioId is not null && !Ids.IsConsultorioId(u.ConsultorioId)) throw DomainException.Validation($"consultorioId inválido para '{u.Username}'.");
        if (u.Role == Roles.Medico && u.ConsultorioId is null) throw DomainException.Validation($"El usuario '{u.Username}' (médico) necesita consultorioId.");
        foreach (var c in u.Campanas ?? new()) Ids.RequireCampaignId(c);
        if (u.Role != Roles.CoordinadorGeneral && (u.Campanas is null || u.Campanas.Count == 0))
            throw DomainException.Validation($"El usuario '{u.Username}' necesita al menos una campaña asignada.");
    }
}

public sealed class StaticUserStore : IUserStore
{
    private readonly List<UserRecord> _users;
    private readonly object _sync = new();
    public StaticUserStore(IEnumerable<UserRecord> users) => _users = users.ToList();

    public Task<UserRecord?> FindAsync(string username, CancellationToken ct)
    {
        lock (_sync) return Task.FromResult(_users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal)));
    }

    public Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct)
    {
        lock (_sync) return Task.FromResult<IReadOnlyList<UserRecord>>(_users.OrderBy(u => u.Username, StringComparer.Ordinal).ToList());
    }

    public Task UpsertAsync(UserRecord user, CancellationToken ct)
    {
        lock (_sync)
        {
            _users.RemoveAll(u => u.Username == user.Username);
            _users.Add(user);
        }
        return Task.CompletedTask;
    }
}

/// <summary>{DataRoot}/_config/users.json. Se vuelve a leer solo cuando cambia el archivo, así un cambio de usuarios surte efecto sin reiniciar.</summary>
public sealed class FileUserStore : IUserStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<UserRecord> _cache = new();
    private DateTime _cacheStamp = DateTime.MinValue;

    public FileUserStore(string path) => _path = path;

    private async Task<List<UserRecord>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new List<UserRecord>();
        var stamp = File.GetLastWriteTimeUtc(_path);
        if (stamp == _cacheStamp) return _cache;
        _cache = CampanaJson.Deserialize<List<UserRecord>>(await File.ReadAllBytesAsync(_path, ct)) ?? new();
        _cacheStamp = stamp;
        return _cache;
    }

    public async Task<UserRecord?> FindAsync(string username, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.Ordinal)); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<UserRecord>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).OrderBy(u => u.Username, StringComparer.Ordinal).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task UpsertAsync(UserRecord user, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = (await LoadAsync(ct)).Where(u => u.Username != user.Username).Append(user).OrderBy(u => u.Username, StringComparer.Ordinal).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            await File.WriteAllBytesAsync(tmp, CampanaJson.SerializeToUtf8(list), ct);
            File.Move(tmp, _path, overwrite: true);
            _cache = list;
            _cacheStamp = File.GetLastWriteTimeUtc(_path);
        }
        finally { _gate.Release(); }
    }

    public static void Validate(IEnumerable<UserRecord> users)
    {
        foreach (var u in users)
        {
            try { UserRules.Validate(u); }
            catch (DomainException ex) { throw new InvalidOperationException(ex.Message, ex); }
        }
    }
}

public sealed class TokenService
{
    private readonly JwtOptions _opts;
    private readonly SymmetricSecurityKey _key;
    private readonly IClock _clock;

    public TokenService(JwtOptions opts, SymmetricSecurityKey key, IClock clock) { _opts = opts; _key = key; _clock = clock; }

    public SymmetricSecurityKey Key => _key;

    public (string Token, DateTimeOffset ExpiresAt) Issue(UserRecord user)
    {
        var now = _clock.UtcNow;
        var expires = now.AddHours(_opts.ExpiresHours);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Username),
            new("role", user.Role),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        if (user.ConsultorioId is not null) claims.Add(new Claim("consultorioId", user.ConsultorioId));
        foreach (var c in user.Campanas ?? new()) claims.Add(new Claim("campana", c));
        var token = new JwtSecurityToken(_opts.Issuer, _opts.Audience, claims, now.UtcDateTime, expires.UtcDateTime,
            new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public static SymmetricSecurityKey BuildKey(string? signingKey, bool allowEphemeral)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            if (!allowEphemeral)
                throw new InvalidOperationException("Falta Campana:Jwt:SigningKey (variable de entorno Campana__Jwt__SigningKey, mínimo 32 caracteres).");
            signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        }
        if (Encoding.UTF8.GetByteCount(signingKey) < 32) throw new InvalidOperationException("Campana:Jwt:SigningKey debe tener al menos 32 caracteres.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));
    }
}

/// <summary>Bloquea un usuario unos minutos tras varios intentos fallidos seguidos.</summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _state = new();
    private readonly IClock _clock;
    public int MaxFailures { get; init; } = 5;
    public TimeSpan LockDuration { get; init; } = TimeSpan.FromMinutes(1);

    public LoginThrottle(IClock clock) => _clock = clock;

    public bool IsLocked(string username) => _state.TryGetValue(username, out var s) && s.LockedUntil > _clock.UtcNow;

    public void Failure(string username) => _state.AddOrUpdate(username,
        _ => (1, default),
        (_, s) =>
        {
            var failures = s.Failures + 1;
            return failures >= MaxFailures ? (0, _clock.UtcNow + LockDuration) : (failures, s.LockedUntil);
        });

    public void Success(string username) => _state.TryRemove(username, out _);
}

public sealed record CurrentUser(string Username, string Role, string? ConsultorioId, IReadOnlyList<string> Campanas)
{
    public bool IsGeneral => Role == Roles.CoordinadorGeneral;

    public static CurrentUser From(ClaimsPrincipal p)
    {
        var name = p.FindFirstValue("sub") ?? p.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new DomainException(ErrorCode.Forbidden, "Sesión sin usuario.");
        var role = p.FindFirstValue("role") ?? p.FindFirstValue(ClaimTypes.Role) ?? "";
        return new CurrentUser(name, role, p.FindFirstValue("consultorioId"), p.FindAll("campana").Select(c => c.Value).ToList());
    }

    public bool CanAccessCampaign(string campaignId) => IsGeneral || Campanas.Contains(campaignId);

    /// <summary>El coordinador general ve todas las campañas; los demás solo las que tienen asignadas.</summary>
    public void EnsureCampaignAccess(string campaignId)
    {
        if (!CanAccessCampaign(campaignId))
            throw new DomainException(ErrorCode.Forbidden, "No tiene asignada esta campaña.");
    }

    /// <summary>Un médico solo escribe con su propio consultorio.</summary>
    public void EnsureCanWriteAs(string consultorioId)
    {
        if (Role == Roles.Medico && ConsultorioId is null)
            throw new DomainException(ErrorCode.Forbidden, "El usuario no tiene consultorio asignado.");
        if (ConsultorioId is not null && ConsultorioId != consultorioId)
            throw new DomainException(ErrorCode.Forbidden, "Solo puede registrar atenciones de su propio consultorio.");
    }
}
