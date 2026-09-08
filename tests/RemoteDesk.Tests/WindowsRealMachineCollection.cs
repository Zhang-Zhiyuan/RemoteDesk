using Xunit;

namespace RemoteDesk.Tests;

public static class WindowsRealMachineCollection
{
    public const string Name =
        "Windows real-machine tests";
}

[CollectionDefinition(
    WindowsRealMachineCollection.Name,
    DisableParallelization = true)]
public sealed class WindowsRealMachineCollectionDefinition;
