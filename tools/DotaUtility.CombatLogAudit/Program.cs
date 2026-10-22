using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotaUtility.CombatLogAudit;
using DotaUtility.Dota2;
using DotaUtility.Memory;

internal static class Program
{
    private const string AuditedClientHash = "88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215";
    private const ulong HudPointerSlotRva = 0x642C498;
    private const ulong RecordVtableRva = 0x3D4F110;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly (ulong Rva, string Bytes)[] NativeChecks =
    [
        (0x30F1380, "488b0511b13303c3"),
        (0xCD2336, "418b7710"),
        (0xCD2353, "418b4f30"),
        (0xCD25C7, "0fbae60c"),
        (0xCD25E0, "410fb64754"),
        (0xCD25F4, "0fbae60d"),
        (0xCD260D, "410fb64755"),
        (0xCD26E6, "0fbae60b"),
        (0xCD26FF, "f3410f104750"),
    ];

    private static async Task<int> Main(string[] args)
    {
        var sampleCount = 8;
        string? output = null;
        for (var i = 0; i < args.Length; i++)
        {
            var option = args[i];
            if (option == "--samples" && i + 1 < args.Length
                && int.TryParse(args[++i], out var count) && count is >= 1 and <= 120)
            {
                sampleCount = count;
            }
            else if (option == "--output" && i + 1 < args.Length)
            {
                output = args[++i];
            }
            else
            {
                Console.Error.WriteLine("Usage: DotaUtility.CombatLogAudit [--samples 1..120] [--output new-file.jsonl]");
                return 2;
            }
        }

        try
        {
            using var file = output is null ? null : new StreamWriter(
                new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false));
            var writer = (TextWriter?)file ?? Console.Out;
            void Emit(object row) { writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions)); writer.Flush(); }
            using var memory = new Win32ProcessMemoryAccessor();
            if (!memory.TryAttach("dota2") || !memory.TryGetModule("client.dll", out var client))
            {
                Emit(new { Kind = "inactive", Utc = DateTimeOffset.UtcNow, Reason = "Dota client is not available.", CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false });
                return 0;
            }
            using (var stream = File.OpenRead(client.FilePath))
            {
                var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                if (digest != AuditedClientHash)
                {
                    throw new InvalidDataException("Unsupported client build; repeat the offline combat-log audit first.");
                }
            }
            if (!CheckNativeBytes(memory, client.BaseAddress))
            {
                throw new InvalidDataException("Loaded client code does not match the audited file profile.");
            }

            var addresses = new DotaAddressResolver(memory);
            var local = new LocalPlayerContextReader(memory, DotaOffsets.Current);
            var reader = new CombatLogCacheReader(memory);
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                Emit(new
                {
                    Kind = "start",
                    Utc = DateTimeOffset.UtcNow,
                    Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    memory.ProcessId,
                    ClientSha256 = AuditedClientHash,
                    Method = "Read/Write bounded HUD cache observation, not packet capture or enemy-view truth.",
                    SamplesRequested = sampleCount,
                    IntervalMilliseconds = 500,
                    AttachesDebugger = false,
                    CallsGameCode = false,
                    CurrentHeroVisibility = "Unknown",
                    CanDriveCurrentOverlay = false,
                });
                for (var index = 0; index < sampleCount; index++)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    Emit(Capture(memory, client, addresses, local, reader));
                    if (index + 1 < sampleCount)
                    {
                        await Task.Delay(500, cancellation.Token);
                    }
                }
                Emit(new { Kind = "end", Utc = DateTimeOffset.UtcNow });
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or OverflowException)
        {
            Console.Error.WriteLine($"AUDIT_REJECTED: {exception.Message}");
            return 2;
        }
    }

    private static object Capture(
        IProcessMemoryAccessor memory,
        ProcessModuleSnapshot client,
        DotaAddressResolver resolver,
        LocalPlayerContextReader localReader,
        CombatLogCacheReader cacheReader)
    {
        var utc = DateTimeOffset.UtcNow;
        if (!resolver.TryResolve(out var addresses, out var reason))
        {
            return new { Kind = "unavailable", Utc = utc, Reason = reason, CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false };
        }
        var system = Read<ulong>(memory, addresses.EntitySystemGlobalAddress);
        var network = Read<ulong>(memory, addresses.NetworkGameClientGlobalAddress);
        var networkSlot = network is > 0 ? Read<int>(memory, network.Value + (ulong)DotaOffsets.Current.NetworkGameClientLocalPlayer) : null;
        if (system is null or 0 || networkSlot is null
            || !localReader.TryRead(system.Value, addresses.LocalControllerArrayAddress, out var before, out _))
        {
            return new { Kind = "inactive", Utc = utc, Reason = "No validated active native controller/assigned hero; cache not sampled.", CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false };
        }
        var hud = Read<ulong>(memory, client.BaseAddress + HudPointerSlotRva);
        if (hud is null or 0)
        {
            return new { Kind = "unavailable", Utc = utc, Reason = "Normal combat-log HUD cache is not initialized.", before.DotaPlayerId, CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false };
        }

        var success = cacheReader.TryRead(hud.Value, client.BaseAddress + RecordVtableRva, out var snapshot, out var detail);
        var className = ReadClassName(memory, client, before.HeroAddress);
        var stableIdentity = Read<ulong>(memory, addresses.EntitySystemGlobalAddress) == system
            && Read<ulong>(memory, addresses.NetworkGameClientGlobalAddress) == network
            && Read<int>(memory, network!.Value + (ulong)DotaOffsets.Current.NetworkGameClientLocalPlayer) == networkSlot
            && Read<ulong>(memory, client.BaseAddress + HudPointerSlotRva) == hud
            && localReader.TryRead(system.Value, addresses.LocalControllerArrayAddress, out var after, out _)
            && before == after;
        if (!stableIdentity)
        {
            success = false;
            detail = "Local identity, network context or HUD changed during capture.";
        }
        return new
        {
            Kind = success ? "cache_sample" : "discarded",
            Utc = utc,
            before.DotaPlayerId,
            before.LocalTeam,
            NetworkSlot = networkSlot,
            HeroHandle = before.HeroHandle.ToString("X8"),
            HeroClass = className,
            Cache = success ? snapshot : null,
            Rejection = success ? null : detail,
            Scope = "Historical event cache only; empty is not Hidden; flags may concern another participant.",
            CurrentHeroVisibility = "Unknown",
            CanDriveCurrentOverlay = false,
        };
    }

    private static bool CheckNativeBytes(IProcessMemoryAccessor memory, ulong moduleBase)
    {
        foreach (var check in NativeChecks)
        {
            var expected = Convert.FromHexString(check.Bytes);
            if (!memory.TryReadBytes(moduleBase + check.Rva, expected.Length, out var actual)
                || !actual.AsSpan().SequenceEqual(expected))
            {
                return false;
            }
        }
        return true;
    }

    private static T? Read<T>(IProcessMemoryAccessor memory, ulong pointer) where T : unmanaged =>
        pointer != 0 && memory.TryRead<T>(pointer, out var value) ? value : null;

    private static string? ReadClassName(IProcessMemoryAccessor memory, ProcessModuleSnapshot client, ulong hero)
    {
        var start = client.BaseAddress;
        var end = checked(start + (ulong)client.Size);
        var vtable = Read<ulong>(memory, hero);
        if (vtable is not ulong vt || vt < start + 8 || vt >= end)
        {
            return null;
        }
        var locator = Read<ulong>(memory, vt - 8);
        if (locator is not ulong col || col < start || col >= end - 24 || Read<uint>(memory, col) != 1)
        {
            return null;
        }
        var descriptor = Read<uint>(memory, col + 12);
        if (descriptor is not uint rva || rva >= client.Size - 192
            || !memory.TryReadBytes(start + rva + 16, 160, out var text))
        {
            return null;
        }
        var length = Array.IndexOf(text, (byte)0);
        return length < 0 ? null : Encoding.ASCII.GetString(text, 0, length);
    }
}
