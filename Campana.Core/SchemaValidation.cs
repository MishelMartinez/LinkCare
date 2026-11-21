using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Campana.Core;

/// <summary>Esquemas incluidos en el ensamblado. Se copian al DataRoot la primera vez.</summary>
public static class DefaultSchemas
{
    public static IReadOnlyDictionary<string, string> All()
    {
        var asm = typeof(DefaultSchemas).Assembly;
        var result = new Dictionary<string, string>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("schemas/", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            // schemas/envelope.v1.json -> envelope.v1 ; schemas/mama/v1.json -> mama/v1
            var name = res["schemas/".Length..];
            if (name.EndsWith(".json", StringComparison.Ordinal)) name = name[..^5];
            result[name] = r.ReadToEnd();
        }
        return result;
    }
}

public sealed class DictionarySchemaSource : ISchemaSource
{
    private readonly IReadOnlyDictionary<string, string> _schemas;
    public DictionarySchemaSource(IReadOnlyDictionary<string, string>? schemas = null) => _schemas = schemas ?? DefaultSchemas.All();
    public string? Load(string name) => _schemas.TryGetValue(name, out var v) ? v : null;
}

public sealed class JsonSchemaValidator : ISchemaValidator
{
    private readonly ISchemaSource _source;
    private readonly ConcurrentDictionary<string, JsonSchema> _cache = new();
    private static readonly EvaluationOptions Options = new() { OutputFormat = OutputFormat.List };

    public JsonSchemaValidator(ISchemaSource source) => _source = source;

    public SchemaValidationResult ValidateEnvelope(JsonElement envelope) => Validate("envelope.v1", envelope);

    public SchemaValidationResult ValidatePayload(string tipo, int version, JsonElement payload)
    {
        Ids.RequireTipo(tipo);
        if (version < 1 || version > 999) throw DomainException.Validation("tipoSchemaVersion inválido.");
        return Validate($"{tipo}/v{version}", payload);
    }

    private SchemaValidationResult Validate(string name, JsonElement instance)
    {
        var schema = _cache.GetOrAdd(name, n =>
        {
            var text = _source.Load(n) ?? throw DomainException.Validation($"No existe el esquema '{n}'.");
            return JsonSchema.FromText(text);
        });

        var node = JsonNode.Parse(instance.GetRawText());
        var results = schema.Evaluate(node, Options);
        if (results.IsValid) return SchemaValidationResult.Ok;

        var errors = new List<SchemaError>();
        foreach (var detail in results.Details)
        {
            if (detail.Errors is null) continue;
            foreach (var kv in detail.Errors)
                errors.Add(new SchemaError(detail.InstanceLocation.ToString(), kv.Value));
        }
        if (errors.Count == 0) errors.Add(new SchemaError("", "El contenido no cumple el esquema."));
        return new SchemaValidationResult(false, errors);
    }
}
