using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class UpdatePreferencesTests
{
    [Fact]
    public void InstallerChoiceSurvivesAppChangesAndUnknownValuesDoNotEnableRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), "adtrim-update-options-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            Assert.True(UpdatePreferences.Load(directory));
            var path = Path.Combine(directory, "automatic-updates.txt");
            File.WriteAllText(path, "0");
            Assert.False(UpdatePreferences.Load(directory));
            UpdatePreferences.Save(directory, true);
            Assert.Equal("1", File.ReadAllText(path));
            UpdatePreferences.Save(directory, false);
            Assert.False(UpdatePreferences.Load(directory));
            File.WriteAllText(path, "invalid");
            Assert.False(UpdatePreferences.Load(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
