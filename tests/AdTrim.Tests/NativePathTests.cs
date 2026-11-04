using AdTrim.Services;
using Xunit;

namespace AdTrim.Tests;

public sealed class NativePathTests
{
    private static string Deep(string root) => root + string.Join(@"\", Enumerable.Repeat("A folder name long enough", 12)) + @"\file.mp4";

    [Fact]
    public void OrdinaryPathsReachNativeCodeUnchanged()
    {
        // Most recordings are well under the limit; their paths must stay exactly as they were.
        Assert.Equal(@"C:\Recorded TV\Show\episode.mp4", NativePath.ForNative(@"C:\Recorded TV\Show\episode.mp4"));
        Assert.Equal(@"\\nas\Media\episode.ts", NativePath.ForNative(@"\\nas\Media\episode.ts"));
    }

    [Fact]
    public void LongLocalAndNetworkPathsUseTheExtendedForm()
    {
        // libmpv opens files through the classic Win32 limit, so long paths need the extended form.
        string local = Deep(@"C:\");
        Assert.True(local.Length >= 260);
        Assert.Equal(@"\\?\" + local, NativePath.ForNative(local));
        string share = Deep(@"\\nas\Media\");
        Assert.Equal(@"\\?\UNC\nas\Media\" + share[@"\\nas\Media\".Length..], NativePath.ForNative(share));
    }

    [Fact]
    public void ExtendedFormIsNormalizedBecauseWindowsWillNotNormalizeIt()
    {
        string mixed = Deep(@"C:/").Replace('\\', '/');
        Assert.Equal(@"\\?\" + Deep(@"C:\"), NativePath.ForNative(mixed));
        string already = @"\\?\" + Deep(@"C:\");
        Assert.Equal(already, NativePath.ForNative(already));
        string relative = string.Join(@"\", Enumerable.Repeat("relative folder name long", 12));
        Assert.Equal(relative, NativePath.ForNative(relative));
    }
}
