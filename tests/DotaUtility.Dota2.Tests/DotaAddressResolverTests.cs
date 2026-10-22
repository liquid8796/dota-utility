using DotaUtility.Memory;

namespace DotaUtility.Dota2.Tests;

public sealed class DotaAddressResolverTests
{
    private const ulong ClientBase = 0x10000;
    private const ulong EngineBase = 0x20000;
    private const string LocalPattern = "33 C0 83 F9 FF 0F 44 C8 48 63 C1 48 8D 0D ?? ?? ?? ?? 48 8B 04 C1 C3";

    [Theory]
    [InlineData(0x8000UL)]
    [InlineData(0x34000UL)]
    public void TryResolve_DecodesNativeControllerArrayIncludingNegativeRipDisplacement(ulong target)
    {
        var f = Seed(target);
        Assert.True(f.Resolver.TryResolve(out var addresses, out _));
        Assert.Equal(target, addresses.LocalControllerArrayAddress);
        Assert.Equal(0x31000UL, addresses.EntitySystemGlobalAddress);
        Assert.Equal(0x32000UL, addresses.NetworkGameClientGlobalAddress);
        Assert.Equal(0x33000UL, addresses.TeamDataHandleArrayAddress);
    }

    [Fact]
    public void TryResolve_DoesNotAcceptOnlyTheLegacyAddressSignatures()
    {
        var f = Seed(0x34000);
        f.Memory.WriteBytes(ClientBase + 0xA0, new byte[23]);
        Assert.False(f.Resolver.TryResolve(out _, out var detail));
        Assert.Contains("native local-controller", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolve_CachesPerProcessAndReacquiresAfterProcessChange()
    {
        var f = Seed(0x34000);
        Assert.True(f.Resolver.TryResolve(out var first, out _));
        f.Memory.WriteBytes(ClientBase + 0xA0, Instruction(LocalPattern, ClientBase + 0xA0, 14, 18, 0x35000));
        Assert.True(f.Resolver.TryResolve(out var cached, out _));
        Assert.Equal(first.LocalControllerArrayAddress, cached.LocalControllerArrayAddress);
        f.Memory.TryAttach(1338);
        Assert.True(f.Resolver.TryResolve(out var changed, out _));
        Assert.Equal(0x35000UL, changed.LocalControllerArrayAddress);
    }

    private static Fixture Seed(ulong localTarget)
    {
        var m = new FakeProcessMemoryAccessor();
        m.TryAttach(1337);
        var client = Enumerable.Repeat((byte)0xCC, 256).ToArray();
        var engine = Enumerable.Repeat((byte)0xCC, 128).ToArray();
        Instruction("48 89 0D ?? ?? ?? ?? E9 ?? ?? ?? ?? CC", ClientBase + 0x10, 3, 7, 0x31000).CopyTo(client, 0x10);
        Instruction(
            "41 56 48 83 EC 40 48 8B 02 4C 8B F1 48 8B 50 08 8B 02 39 05 ?? ?? ?? ?? 0F 85 ?? ?? ?? ?? " +
            "48 8B 42 10 48 89 5C 24 50 BB 01 00 00 00 48 89 6C 24 60 48 89 74 24 38 48 8D 35 ?? ?? ?? ?? 4C 89 64 24 28 45 33 E4",
            ClientBase + 0x40, 57, 61, 0x33000).CopyTo(client, 0x40);
        Instruction(LocalPattern, ClientBase + 0xA0, 14, 18, localTarget).CopyTo(client, 0xA0);
        Instruction("48 89 3D ?? ?? ?? ?? FF 87", EngineBase + 0x10, 3, 7, 0x32000).CopyTo(engine, 0x10);
        m.AddModule(new ProcessModuleSnapshot("client.dll", "client.dll", ClientBase, client.Length));
        m.AddModule(new ProcessModuleSnapshot("engine2.dll", "engine2.dll", EngineBase, engine.Length));
        m.WriteBytes(ClientBase, client);
        m.WriteBytes(EngineBase, engine);
        return new Fixture(m, new DotaAddressResolver(m));
    }

    private static byte[] Instruction(string pattern, ulong source, int displacement, int end, ulong target)
    {
        var bytes = pattern.Split(' ').Select(token => token == "??" ? (byte)0 : Convert.ToByte(token, 16)).ToArray();
        BitConverter.GetBytes(checked((int)((long)target - ((long)source + end)))).CopyTo(bytes, displacement);
        return bytes;
    }

    private sealed record Fixture(FakeProcessMemoryAccessor Memory, DotaAddressResolver Resolver);
}
