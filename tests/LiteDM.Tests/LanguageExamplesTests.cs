using LiteDM.Core;

namespace LiteDM.Tests;

// Executable study examples. Production behavior does not depend on these helpers.
public sealed class LanguageExamplesTests
{
    [Fact]
    public void FieldBackedPropertyValidatesWithoutAnExplicitBackingField()
    {
        var preferences = new StudyPreferences { Connections = 4 };
        Assert.Equal(4, preferences.Connections);
        Assert.Throws<ArgumentOutOfRangeException>(() => preferences.Connections = 0);
        Assert.Equal(4, preferences.Connections);
    }

    [Fact]
    public void NullConditionalAssignmentSkipsTheRightHandSideWhenMissing()
    {
        StudyPreferences? preferences = null;
        var evaluations = 0;
        preferences?.Connections = ++evaluations;
        Assert.Equal(0, evaluations);

        preferences = new StudyPreferences();
        preferences?.Connections = ++evaluations;
        Assert.Equal(1, preferences!.Connections);
    }

    [Fact]
    public void ExtensionPropertyAddsAComputedViewWithoutChangingTheRecord()
    {
        var job = new DownloadSnapshot
        {
            Url = "https://example.com/file.bin",
            FileName = "file.bin",
            DestinationDirectory = ".",
            State = DownloadState.Completed
        };
        Assert.True(job.IsSuccessful);
        Assert.False((job with { State = DownloadState.Failed }).IsSuccessful);
    }
}

public sealed class StudyPreferences
{
    public int Connections
    {
        get;
        set => field = value is >= 1 and <= 16
            ? value : throw new ArgumentOutOfRangeException(nameof(value));
    } = 4;
}

public static class StudyDownloadExtensions
{
    extension(DownloadSnapshot job)
    {
        public bool IsSuccessful => job.State == DownloadState.Completed;
    }
}
