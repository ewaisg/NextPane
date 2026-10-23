using NexPane.Shell.Launch;

namespace NexPane.Shell.Tests.Launch;

public sealed class ShellLaunchErrorsTests
{
    [Theory]
    [InlineData(0, ShellLaunchStatus.Success)]
    [InlineData(2, ShellLaunchStatus.FileNotFound)]
    [InlineData(3, ShellLaunchStatus.PathNotFound)]
    [InlineData(5, ShellLaunchStatus.AccessDenied)]
    [InlineData(8, ShellLaunchStatus.OutOfMemory)]
    [InlineData(14, ShellLaunchStatus.OutOfMemory)]
    [InlineData(32, ShellLaunchStatus.SharingViolation)]
    [InlineData(123, ShellLaunchStatus.InvalidName)]
    [InlineData(193, ShellLaunchStatus.BadExecutableFormat)]
    [InlineData(206, ShellLaunchStatus.PathTooLong)]
    [InlineData(740, ShellLaunchStatus.ElevationRequired)]
    [InlineData(1155, ShellLaunchStatus.NoAssociation)]
    [InlineData(1156, ShellLaunchStatus.DdeFailure)]
    [InlineData(1157, ShellLaunchStatus.DllNotFound)]
    [InlineData(1223, ShellLaunchStatus.CancelledByUser)]
    [InlineData(87, ShellLaunchStatus.Failed)]
    public void Win32_errors_map_to_typed_statuses(int error, ShellLaunchStatus expected)
    {
        ShellLaunchErrors.FromWin32Error(error).Should().Be(expected);
    }

    [Fact]
    public void Win32_result_carries_the_code_and_the_system_message()
    {
        var result = ShellLaunchErrors.FromWin32(1223);

        result.Status.Should().Be(ShellLaunchStatus.CancelledByUser);
        result.NativeError.Should().Be(1223);
        result.Message.Should().NotBeNullOrWhiteSpace();
        result.Succeeded.Should().BeFalse();
        ShellLaunchErrors.FromWin32(0).Should().Be(ShellLaunchResult.Success);
    }

    [Theory]
    [InlineData(2, ShellLaunchStatus.FileNotFound, 2)]
    [InlineData(3, ShellLaunchStatus.PathNotFound, 3)]
    [InlineData(5, ShellLaunchStatus.AccessDenied, 5)]
    [InlineData(8, ShellLaunchStatus.OutOfMemory, 8)]
    [InlineData(26, ShellLaunchStatus.SharingViolation, 32)]
    [InlineData(27, ShellLaunchStatus.IncompleteAssociation, 1155)]
    [InlineData(28, ShellLaunchStatus.DdeFailure, 1156)]
    [InlineData(29, ShellLaunchStatus.DdeFailure, 1156)]
    [InlineData(30, ShellLaunchStatus.DdeFailure, 1156)]
    [InlineData(31, ShellLaunchStatus.NoAssociation, 1155)]
    [InlineData(32, ShellLaunchStatus.DllNotFound, 1157)]
    public void Legacy_SE_ERR_codes_map_to_typed_statuses(int seErr, ShellLaunchStatus expected, int win32)
    {
        ShellLaunchErrors.FromSeErr(seErr).Should().Be((expected, win32));
    }

    [Fact]
    public void HRESULT_from_Win32_uses_the_Win32_table_and_other_HRESULTs_are_Failed()
    {
        ShellLaunchErrors.FromHResult(unchecked((int)0x80070002)).Status.Should().Be(ShellLaunchStatus.FileNotFound);
        ShellLaunchErrors.FromHResult(unchecked((int)0x80070005)).Status.Should().Be(ShellLaunchStatus.AccessDenied);
        ShellLaunchErrors.FromHResult(unchecked((int)0x800704C7)).Status.Should().Be(ShellLaunchStatus.CancelledByUser);
        ShellLaunchErrors.FromHResult(unchecked((int)0x80070483)).Status.Should().Be(ShellLaunchStatus.NoAssociation);

        var other = ShellLaunchErrors.FromHResult(unchecked((int)0x80004005)); // E_FAIL
        other.Status.Should().Be(ShellLaunchStatus.Failed);
        other.NativeError.Should().Be(unchecked((int)0x80004005));
        ShellLaunchErrors.FromHResult(0).Should().Be(ShellLaunchResult.Success);
    }
}
