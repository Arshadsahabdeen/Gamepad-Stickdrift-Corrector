using StickFix.Core;
using Xunit;

namespace StickFix.Tests;

public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("stickfix-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid() + ".json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Round_trips_every_field()
    {
        var profile = new Profile
        {
            Name = "FPS",
            ControllerId = "20D6-2001",
            Left = new StickSettings
            {
                X = new AxisCalibration { Center = -0.02, RangePositive = 1.02, RangeNegative = 0.98, Invert = true },
                DeadzoneInner = 0.07, Gamma = 1.5, FilterEnabled = true, SwapXY = true,
            },
            Confidence = Confidence.High,
            LeftFindings = StickFinding.ConsistentOffset | StickFinding.Noisy,
        };
        var path = Path.Combine(_dir, "nested", "p.json");
        ProfileStore.Save(profile, path);

        var loaded = ProfileStore.Load(path);
        Assert.Equal(profile.Left, loaded.Left);
        Assert.Equal(profile.Right, loaded.Right);
        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal(profile.Confidence, loaded.Confidence);
        Assert.Equal(profile.LeftFindings, loaded.LeftFindings);
        Assert.Equal(Profile.CurrentVersion, loaded.Version);
    }

    [Fact]
    public void Newer_version_is_rejected_and_the_file_is_left_alone()
    {
        const string content = """{ "Version": 99 }""";
        var path = Write(content);
        Assert.Throws<ProfileException>(() => ProfileStore.Load(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "Version": "one" }""")]
    public void Corrupt_files_give_a_clear_error(string content)
    {
        var path = Write(content);
        Assert.Throws<ProfileException>(() => ProfileStore.Load(path));
    }

    [Fact]
    public void Imported_profile_with_dangerous_values_is_rejected()
    {
        var path = Write("""{ "Version": 1, "Left": { "DeadzoneInner": 0.9, "DeadzoneOuter": 0.1 } }""");
        var error = Assert.Throws<ProfileException>(() => ProfileStore.Load(path));
        Assert.Contains("deadzones", error.Message);
    }

    [Fact]
    public void Saving_over_an_existing_profile_leaves_no_temp_file()
    {
        var path = Path.Combine(_dir, "p.json");
        ProfileStore.Save(new Profile { Name = "a" }, path);
        ProfileStore.Save(new Profile { Name = "b" }, path);
        Assert.Equal("b", ProfileStore.Load(path).Name);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void File_names_from_controller_ids_are_made_safe()
    {
        var path = ProfileStore.PathFor(_dir, @"bad:id/..\x", "My Profile");
        Assert.Equal(_dir, Path.GetDirectoryName(path));
    }
}
