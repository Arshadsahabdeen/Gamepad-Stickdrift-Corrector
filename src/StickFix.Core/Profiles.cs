using System.Text.Json;
using System.Text.Json.Nodes;

namespace StickFix.Core;

public sealed record Profile
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;
    public string Name { get; init; } = "Default";
    public string ControllerId { get; init; } = "";
    public StickSettings Left { get; init; } = new();
    public StickSettings Right { get; init; } = new();
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public Confidence Confidence { get; init; } = Confidence.Low;
    public StickFinding LeftFindings { get; init; }
    public StickFinding RightFindings { get; init; }
}

public sealed class ProfileException(string message, Exception? inner = null) : Exception(message, inner);

public static class ProfileStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickFix", "profiles");

    public static string PathFor(string directory, string controllerId, string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string($"{controllerId}-{name}".Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(directory, safe + ".json");
    }

    /// <summary>Atomic: a crash mid-save leaves the previous file intact.</summary>
    public static void Save(Profile profile, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Also the import path, so everything read here is treated as untrusted.</summary>
    public static Profile Load(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path))
                ?? throw new ProfileException($"Profile '{path}' is empty.");
            var version = node["Version"]?.GetValue<int>()
                ?? throw new ProfileException($"Profile '{path}' has no version.");
            if (version > Profile.CurrentVersion)
                throw new ProfileException($"Profile '{path}' is version {version}, newer than this app supports ({Profile.CurrentVersion}).");
            // shortcut: no older schema exists yet, add a vN -> vN+1 step here on the first format change.

            var profile = node.Deserialize<Profile>(Options)
                ?? throw new ProfileException($"Profile '{path}' is empty.");
            var problem = profile.Left.Validate() ?? profile.Right.Validate();
            return problem is null ? profile : throw new ProfileException($"Profile '{path}' is invalid: {problem}.");
        }
        catch (JsonException e)
        {
            throw new ProfileException($"Profile '{path}' is not valid JSON.", e);
        }
        catch (InvalidOperationException e)
        {
            throw new ProfileException($"Profile '{path}' has a malformed field.", e);
        }
    }
}
