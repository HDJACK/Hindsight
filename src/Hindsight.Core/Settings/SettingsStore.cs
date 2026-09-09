using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hindsight.Core.Settings;

/// <summary>The settings that were loaded, whether the file had to be reset, and any adjustments worth telling the user about.</summary>
public sealed record LoadResult(AppSettings Settings, bool WasReset, IReadOnlyList<string> Notes);

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, falling back to the defaults when the file is unreadable.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    public AppSettings Current { get; private set; } = AppSettings.Default;
    public event Action<AppSettings>? Changed;

    public SettingsStore(string path) => _path = path;

    public LoadResult Load()
    {
        var notes = new List<string>();
        if (!File.Exists(_path))
        {
            Current = AppSettings.Default;
            try { Save(Current); } catch { /* best effort; the in-memory defaults still apply */ }
            return new LoadResult(Current, false, notes);
        }
        AppSettings? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json);
        }
        catch (JsonException ex)
        {
            notes.Add("Settings file was unreadable and has been reset: " + ex.Message);
            Current = AppSettings.Default;
            return new LoadResult(Current, true, notes);
        }
        if (parsed is null)
        {
            notes.Add("Settings file was empty and has been reset.");
            Current = AppSettings.Default;
            return new LoadResult(Current, true, notes);
        }
        var errors = SettingsValidator.Validate(parsed);
        notes.AddRange(errors.Select(e => "Adjusted: " + e));
        Current = errors.Count == 0 ? parsed : SettingsValidator.Clamp(parsed);
        return new LoadResult(Current, false, notes);
    }

    public void Save(AppSettings settings)
    {
        var errors = SettingsValidator.Validate(settings);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, _path, overwrite: true);
        Current = settings;
        Changed?.Invoke(settings);
    }
}
