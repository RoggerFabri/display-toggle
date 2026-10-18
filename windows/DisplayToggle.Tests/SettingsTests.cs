using Xunit;

namespace DisplayToggle.Tests;

// Settings persistence: an unreadable or corrupt config must never cost the InstanceId / secret.
public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dt-settings-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public SettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void RoundTrip_KeepsValues_AndLeavesNoTempFile()
    {
        var s = Settings.Load(ConfigPath);
        s.InstanceId = "abc";
        s.PeerSecret = "s3cret";
        s.Save();

        var back = Settings.Load(ConfigPath);
        Assert.Equal("abc", back.InstanceId);
        Assert.Equal("s3cret", back.PeerSecret);
        Assert.False(File.Exists(ConfigPath + ".tmp"));
    }

    [Fact]
    public void LockedFile_LoadsReadOnly_AndSaveDoesNotOverwrite()
    {
        File.WriteAllText(ConfigPath, """{"InstanceId":"keep-me"}""");
        Settings s;
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            s = Settings.Load(ConfigPath);

        Assert.True(s.ReadOnly);
        s.InstanceId = "clobber";
        s.Save();
        Assert.Equal("keep-me", Settings.Load(ConfigPath).InstanceId);
    }

    [Fact]
    public void ReadOnly_KeepsChangesInMemory()
    {
        // The tray relies on this to show the "update available" balloon once per session even when
        // the config can't be written.
        File.WriteAllText(ConfigPath, "{}");
        Settings s;
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None))
            s = Settings.Load(ConfigPath);

        Assert.True(s.ReadOnly);
        s.LastNotifiedVersion = "1.2.3";
        s.Save();
        Assert.Equal("1.2.3", s.LastNotifiedVersion);
        Assert.Equal("", Settings.Load(ConfigPath).LastNotifiedVersion);
    }

    [Fact]
    public void CorruptFile_IsBackedUp_ThenDefaults()
    {
        File.WriteAllText(ConfigPath, """{"InstanceId":"tru""");
        var s = Settings.Load(ConfigPath);

        Assert.False(s.ReadOnly);
        Assert.Equal("", s.InstanceId);
        var backup = Assert.Single(Directory.GetFiles(_dir, "config.json.bad-*"));
        Assert.Equal("""{"InstanceId":"tru""", File.ReadAllText(backup));
    }
}
