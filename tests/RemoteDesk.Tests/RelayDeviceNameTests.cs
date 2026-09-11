using Xunit;

namespace RemoteDesk.Tests;

public sealed class RelayDeviceNameTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("  广州主机 🖥  ", "广州主机 🖥")]
    [InlineData("　手机　", "手机")]
    public void NormalizesSharedNameWithoutChangingUnicode(string value, string expected) =>
        Assert.Equal(expected, RelayDeviceName.Normalize(value));

    [Fact]
    public void RejectsUnprintableCharactersAndUsesUtf16Limit()
    {
        Assert.Equal(new string('文', 80), RelayDeviceName.Normalize(new string('文', 80)));
        Assert.Equal(80, RelayDeviceName.Normalize(string.Concat(Enumerable.Repeat("🙂", 40))).Length);
        foreach (string invalid in new[] { new string('文', 81), string.Concat(Enumerable.Repeat("🙂", 41)),
                     "a\nb", "a\0b", "a\u202eb", "a\u2028b", "\ud800", "\udfff" })
            Assert.Throws<ArgumentException>(() => RelayDeviceName.Normalize(invalid));
    }

    [Fact]
    public void ErrorMessagesNeverEchoArbitraryServerContents()
    {
        Assert.DoesNotContain("secret", RelayDeviceName.ErrorMessage("arbitrary-secret"));
        Assert.Contains("磁盘", RelayDeviceName.ErrorMessage("中继握手无效：设备名称保存失败。"));
        Assert.Equal(RelayDeviceName.UnsupportedMessage, RelayDeviceName.ErrorMessage("未知的中继连接类型。"));
    }

    [Fact]
    public void DialogEditsSharedNameRatherThanOriginalAndAllowsEmptyReset()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var device = new RelayOnlineDevice(Guid.NewGuid().ToString(), "共享机", "Windows", null, false, 0)
                    { SharedName = "共享机", OriginalMachineName = "Original-PC", CanRename = true };
                using var dialog = MainForm.CreateRelayNameDialog(device, SystemFonts.MessageBoxFont!, out TextBox input);
                Assert.Equal("共享机", input.Text);
                Assert.Equal(80, input.MaxLength);
                Assert.Equal("保存并同步", ((Button)dialog.AcceptButton!).Text);
                Assert.Equal(DialogResult.Cancel, dialog.CancelButton!.DialogResult);
                Assert.False(input.UseSystemPasswordChar);
                input.Clear();
                Assert.Empty(RelayDeviceName.Normalize(input.Text));
                using var original = MainForm.CreateRelayNameDialog(device with { SharedName = "" }, SystemFonts.MessageBoxFont!, out TextBox empty);
                Assert.Empty(empty.Text);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "Owned naming dialog test timed out");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
