// Copyright (c) 2026 Paul Carver
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PaulTechGuy.CN.Abstractions;
using PaulTechGuy.CN.Domain;

namespace PaulTechGuy.CN.Repositories;

/// <summary>
/// How a template looks on disk.
///
/// A separate shape from <see cref="DateTemplate" /> on purpose. The domain record is
/// free to change as the app grows; this one is a file format that other versions of
/// Chronora have to keep reading, and a template someone posted in a forum three years
/// ago should still load. Keeping them apart means a refactor cannot silently change the
/// format, and it keeps serialisation attributes out of a domain project whose whole
/// point is having no dependencies.
///
/// Flat rather than polymorphic for the same reason: a discriminated union is tidy in C#
/// and brittle in JSON.
/// </summary>
/// <param name="Id">Stable identity.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">Why you would use it.</param>
/// <param name="SourceKind">absolute | shift | copyFrom | fromFileName | zoneChange.</param>
/// <param name="AbsoluteUtc">For absolute.</param>
/// <param name="ShiftTicks">For shift.</param>
/// <param name="ShiftBasis">For shift.</param>
/// <param name="Aggregate">For copyFrom.</param>
/// <param name="SourceFields">For copyFrom.</param>
/// <param name="PatternId">For fromFileName.</param>
/// <param name="FromZone">For zoneChange.</param>
/// <param name="ToZone">For zoneChange.</param>
/// <param name="Targets">Fields written.</param>
/// <param name="OnlyIfTargetEmpty">Guard.</param>
/// <param name="OnlyIfNewer">Guard.</param>
/// <param name="OnlyIfOlder">Guard.</param>
public sealed record TemplateDto(
    string Id,
    string Name,
    string Description,
    string SourceKind,
    DateTimeOffset? AbsoluteUtc,
    long? ShiftTicks,
    string? ShiftBasis,
    string? Aggregate,
    IReadOnlyList<string>? SourceFields,
    string? PatternId,
    string? FromZone,
    string? ToZone,
    IReadOnlyList<string> Targets,
    bool OnlyIfTargetEmpty,
    bool OnlyIfNewer,
    bool OnlyIfOlder);

/// <summary>The templates file as a whole.</summary>
/// <param name="SchemaVersion">
/// Bumped when the shape changes. A file from a newer build opens read-only rather than
/// being rewritten in an older shape, which is the failure that only shows up on a
/// rollback and takes the user's data with it.
/// </param>
/// <param name="Templates">The user's own. Built-ins are compiled in, never stored.</param>
public sealed record TemplateFile(int SchemaVersion, IReadOnlyList<TemplateDto> Templates);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TemplateFile))]
[JsonSerializable(typeof(TemplateDto))]
internal sealed partial class TemplateJsonContext : JsonSerializerContext;

/// <summary>
/// The user's templates, plus the ones that ship with the app.
///
/// Built-ins are never written to disk. Storing them would freeze whatever wording and
/// behaviour they had on the day they were first saved, so a later improvement would
/// reach new users and silently skip everyone who had run the app once.
/// </summary>
public sealed class TemplateStore
{
    /// <summary>The format this build writes.</summary>
    public const int SchemaVersion = 1;

    private readonly JsonFileStore<TemplateFile> _file;
    private readonly ILogger _logger;
    private List<DateTemplate> _user = [];

    public TemplateStore(IAppPaths paths, ILogger<TemplateStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        this._logger = logger ?? NullLogger<TemplateStore>.Instance;
        this._file = new JsonFileStore<TemplateFile>(
            paths.TemplatesFilePath,
            TemplateJsonContext.Default.TemplateFile,
            SchemaVersion,
            f => f.SchemaVersion,
            this._logger);

        this.Reload();
    }

    /// <summary>True when the file came from a newer build and must not be overwritten.</summary>
    public bool IsReadOnly => this._file.IsReadOnly;

    public string? ReadOnlyReason => this._file.ReadOnlyReason;

    /// <summary>Built-ins first, then the user's own, each group in its own order.</summary>
    public IReadOnlyList<DateTemplate> All => [.. BuiltInTemplates.All, .. this._user];

    public IReadOnlyList<DateTemplate> UserTemplates => this._user;

    public void Reload()
    {
        TemplateFile? file = this._file.Read();

        this._user = file is null
            ? []
            : [.. file.Templates.Select(ToDomain).OfType<DateTemplate>()];
    }

    /// <summary>
    /// Adds a template, or replaces one with the same name.
    ///
    /// Replacing by name rather than refusing is the kinder behaviour: someone who saves
    /// "My camera fix" twice means to update it, and being told "that name is taken" when
    /// it is their own template is a pointless obstacle.
    /// </summary>
    /// <returns>False when the file could not be written, so the caller can say so.</returns>
    public bool Save(DateTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.IsBuiltIn)
        {
            throw new ArgumentException("A built-in template cannot be saved over. Duplicate it first.", nameof(template));
        }

        int existing = this._user.FindIndex(t => string.Equals(t.Name, template.Name, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
        {
            // Keeps the original id, so anything already pointing at it still resolves.
            this._user[existing] = template with { Id = this._user[existing].Id };
        }
        else
        {
            this._user.Add(template);
        }

        return this.Flush();
    }

    public bool Delete(string id)
    {
        int removed = this._user.RemoveAll(t => string.Equals(t.Id, id, StringComparison.Ordinal));
        return removed == 0 || this.Flush();
    }

    /// <summary>
    /// A copy of a template, editable. The route by which a built-in becomes a starting
    /// point rather than a dead end.
    /// </summary>
    public static DateTemplate Duplicate(DateTemplate template, string newName)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        return template with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = newName,
            IsBuiltIn = false,
        };
    }

    /// <summary>Writes one template to a file, so it can be shared in a forum post.</summary>
    public bool Export(DateTemplate template, string path)
    {
        ArgumentNullException.ThrowIfNull(template);

        try
        {
            using FileStream stream = File.Create(path);
            JsonSerializer.Serialize(stream, ToDto(template), TemplateJsonContext.Default.TemplateDto);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            this._logger.LogError(ex, "Could not export a template to {Path}.", path);
            return false;
        }
    }

    /// <summary>
    /// Reads a shared template. Always lands as a new user template with a fresh id, so
    /// importing can never overwrite something already here.
    /// </summary>
    public DateTemplate? Import(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            TemplateDto? dto = JsonSerializer.Deserialize(stream, TemplateJsonContext.Default.TemplateDto);

            if (dto is null || ToDomain(dto) is not { } template)
            {
                this._logger.LogWarning("{Path} is not a Chronora template.", path);
                return null;
            }

            return template with { Id = Guid.NewGuid().ToString("N"), IsBuiltIn = false };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            this._logger.LogWarning(ex, "Could not import a template from {Path}.", path);
            return null;
        }
    }

    private bool Flush() => this._file.Write(new TemplateFile(SchemaVersion, [.. this._user.Select(ToDto)]));

    private static TemplateDto ToDto(DateTemplate template)
    {
        // Only the fields the source kind actually uses are filled; the rest stay null and
        // are omitted from the JSON, so a stored template reads as what it is rather than
        // as a wall of nulls.
        var dto = new TemplateDto(
            template.Id,
            template.Name,
            template.Description,
            SourceKind: "absolute",
            AbsoluteUtc: null,
            ShiftTicks: null,
            ShiftBasis: null,
            Aggregate: null,
            SourceFields: null,
            PatternId: null,
            FromZone: null,
            ToZone: null,
            Targets: [.. template.Targets.Select(t => t.ToString())],
            template.Guards.OnlyIfTargetEmpty,
            template.Guards.OnlyIfNewer,
            template.Guards.OnlyIfOlder);

        return template.Source switch
        {
            DateSource.Absolute a => dto with { SourceKind = "absolute", AbsoluteUtc = a.Value },

            DateSource.Shift s => dto with
            {
                SourceKind = "shift",
                ShiftTicks = s.Delta.Ticks,
                ShiftBasis = s.Basis.ToString(),
            },

            DateSource.CopyFrom c => dto with
            {
                SourceKind = "copyFrom",
                Aggregate = c.How.ToString(),
                SourceFields = [.. c.Fields.Select(f => f.ToString())],
            },

            DateSource.FromFileName f => dto with { SourceKind = "fromFileName", PatternId = f.PatternId },

            DateSource.ZoneChange z => dto with
            {
                SourceKind = "zoneChange",
                FromZone = z.From.Id,
                ToZone = z.To.Id,
            },

            // A source kind added later that nobody taught this method about. Writing it
            // as an absolute epoch would be a quiet lie, so it is named instead and the
            // reader refuses it rather than loading something that does the wrong thing.
            _ => dto with { SourceKind = "unsupported" },
        };
    }

    /// <summary>
    /// Rebuilds a template from a stored one, or null when it cannot be understood.
    ///
    /// Null rather than an exception, and rather than a partly-built template. A file
    /// naming a field this build does not have, or a zone this machine does not know, is
    /// one Chronora cannot honour - and a template that quietly does less than its name
    /// promises is worse than one that does not appear.
    /// </summary>
    private static DateTemplate? ToDomain(TemplateDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name))
        {
            return null;
        }

        var targets = new HashSet<DateField>();

        foreach (string name in dto.Targets)
        {
            if (!Enum.TryParse(name, out DateField field))
            {
                return null;
            }

            _ = targets.Add(field);
        }

        if (targets.Count == 0)
        {
            return null;
        }

        DateSource? source = ToSource(dto);

        return source is null
            ? null
            : new DateTemplate(
                dto.Id,
                dto.Name,
                dto.Description ?? string.Empty,
                source,
                targets,
                new RuleGuards(dto.OnlyIfTargetEmpty, dto.OnlyIfNewer, dto.OnlyIfOlder));
    }

    private static DateSource? ToSource(TemplateDto dto)
    {
        switch (dto.SourceKind)
        {
            case "absolute":
                return dto.AbsoluteUtc is { } value ? new DateSource.Absolute(value) : null;

            case "shift":
                return dto.ShiftTicks is { } ticks
                    && Enum.TryParse(dto.ShiftBasis, out ShiftBasis basis)
                        ? new DateSource.Shift(TimeSpan.FromTicks(ticks), basis)
                        : null;

            case "copyFrom":
                {
                    if (dto.SourceFields is null || !Enum.TryParse(dto.Aggregate, out Aggregate how))
                    {
                        return null;
                    }

                    var fields = new List<DateField>();

                    foreach (string name in dto.SourceFields)
                    {
                        if (!Enum.TryParse(name, out DateField field))
                        {
                            return null;
                        }

                        fields.Add(field);
                    }

                    return fields.Count > 0 ? new DateSource.CopyFrom(how, fields) : null;
                }

            case "fromFileName":
                return new DateSource.FromFileName(dto.PatternId ?? string.Empty);

            case "zoneChange":
                try
                {
                    return dto.FromZone is null || dto.ToZone is null
                        ? null
                        : new DateSource.ZoneChange(
                            TimeZoneInfo.FindSystemTimeZoneById(dto.FromZone),
                            TimeZoneInfo.FindSystemTimeZoneById(dto.ToZone));
                }
                catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
                {
                    // A zone this machine does not have. Refusing the template is right:
                    // guessing a nearby zone would silently shift every date by an hour.
                    return null;
                }

            default:
                return null;
        }
    }
}
