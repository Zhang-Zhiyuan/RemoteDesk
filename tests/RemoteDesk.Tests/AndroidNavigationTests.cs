using Xunit;

namespace RemoteDesk.Tests;

public sealed class AndroidNavigationTests
{
    [Theory]
    [InlineData(Keys.Escape)]
    [InlineData(Keys.Home)]
    [InlineData(Keys.F12)]
    public void PhoneNavigationUsesBalancedExistingProtocolKeys(Keys key)
    {
        Assert.True(RemoteViewerWindow.IsAndroidNavigationKey(key));
        Assert.Equal(new[] { RemoteInputCommand.KeyDown((int)key), RemoteInputCommand.KeyUp((int)key) },
            RemoteViewerWindow.CreateAndroidNavigationCommands(key));
    }

    [Theory]
    [InlineData(Keys.Back)]
    [InlineData(Keys.Delete)]
    [InlineData(Keys.LWin)]
    [InlineData(Keys.F11)]
    public void OtherKeysCannotBeSentAsPhoneNavigation(Keys key)
    {
        Assert.False(RemoteViewerWindow.IsAndroidNavigationKey(key));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemoteViewerWindow.CreateAndroidNavigationCommands(key));
    }
}
