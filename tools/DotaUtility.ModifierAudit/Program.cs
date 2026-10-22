using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotaUtility.Dota2;
using DotaUtility.Memory;
using DotaUtility.ModifierAudit;

internal static class Program
{
    internal const string ClientHash = "88fa24c60a8c68f84d25cc2285019889c05db0c55b2bed1165d6f98fa7c18215";
    private static readonly (ulong Rva, string Bytes)[] NativeChecks =
    [
        (0x14FB5D0, "488b4130c3"), (0x14FA830, "8b5170"),
        (0x14FA878, "395010"), (0x14D9650, "8b5174"),
        (0x14D9693, "395010"), (0x14FF180, "8b4148c3"),
        (0x14FFB6E, "6685ae7c030000"), (0x14FFB8F, "48634610"),
        (0x14FFBB0, "488b4618"), (0x14FFBBD, "f6819e00000004"),
        (0x14FFBD1, "ff9000030000"),
        (0x14D7BBC, "8b4e68"), (0x14D7C58, "8b4e74"),
        (0x14D7CB2, "898694000000"), (0x16418C6, "8b9114050000"),
        (0x1641990, "0fb683e7030000"),
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> Main(string[] args)
    {
        var samples = 20;
        string? output = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!seen.Add(key) || i + 1 >= args.Length) return Usage();
            if (key == "--samples" && int.TryParse(args[++i], out var count) && count is >= 1 and <= 300)
                samples = count;
            else if (key == "--output" && !string.IsNullOrWhiteSpace(args[i + 1])) output = args[++i];
            else return Usage();
        }
        try
        {
            using var memory = new Win32ProcessMemoryAccessor();
            var available = memory.TryAttach("dota2") && memory.TryGetModule("client.dll", out _);
            ProcessModuleSnapshot? client = null;
            if (available)
            {
                if (!memory.TryGetModule("client.dll", out client)) throw new InvalidDataException("Client module changed.");
                using var stream = File.OpenRead(client.FilePath);
                if (Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() != ClientHash)
                    throw new InvalidDataException("Unsupported client build; re-audit modifier layout before use.");
                foreach (var (rva, bytes) in NativeChecks)
                {
                    var expected = Convert.FromHexString(bytes);
                    if (!memory.TryReadBytes(client.BaseAddress + rva, expected.Length, out var actual)
                        || !actual.AsSpan().SequenceEqual(expected))
                        throw new InvalidDataException($"Loaded native contract mismatch at RVA {rva:X}.");
                }
            }
            using var file = output is null ? null : new StreamWriter(
                new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            var writer = (TextWriter?)file ?? Console.Out;
            void Emit(object value) { writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions)); writer.Flush(); }
            if (!available || client is null)
            {
                Emit(new { Kind = "inactive", Utc = DateTimeOffset.UtcNow, Reason = "Dota client unavailable.", CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false });
                return 0;
            }
            var addresses = new DotaAddressResolver(memory);
            var contextReader = new LocalPlayerContextReader(memory, DotaOffsets.Current);
            var modifiers = new OwnerModifierReader(memory);
            var casterReader = new ModifierCasterReader(memory);
            using var cancel = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                Emit(new
                {
                    Kind = "start",
                    Utc = DateTimeOffset.UtcNow,
                    Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                    memory.ProcessId,
                    ClientSha256 = ClientHash,
                    SamplesRequested = samples,
                    IntervalMilliseconds = 200,
                    ReadOnly = true,
                    CallsGameCode = false,
                    AttachesDebugger = false,
                    CurrentHeroVisibility = "Unknown",
                    CanDriveCurrentOverlay = false,
                });
                LocalPlayerContext? previous = null;
                var segment = 0;
                for (var i = 0; i < samples && !cancel.IsCancellationRequested; i++)
                {
                    var reason = "No validated active local controller/hero.";
                    if (!addresses.TryResolve(out var resolved, out reason)
                        || !memory.TryRead<ulong>(resolved.EntitySystemGlobalAddress, out var system)
                        || !contextReader.TryRead(system, resolved.LocalControllerArrayAddress, out var local, out reason)
                        || !memory.TryRead<ulong>(resolved.NetworkGameClientGlobalAddress, out var network)
                        || network == 0
                        || !memory.TryRead<int>(network + (ulong)DotaOffsets.Current.NetworkGameClientLocalPlayer, out var slot)
                        || slot is < 0 or > 63)
                    {
                        previous = null;
                        Emit(new { Kind = "inactive", Utc = DateTimeOffset.UtcNow, Reason = reason, CurrentHeroVisibility = "Unknown", CanDriveCurrentOverlay = false });
                    }
                    else
                    {
                        if (previous != local) { segment++; previous = local; }
                        bool Stable() => memory.TryRead<ulong>(resolved.EntitySystemGlobalAddress, out var nextSystem)
                            && nextSystem == system
                            && memory.TryRead<ulong>(resolved.NetworkGameClientGlobalAddress, out var nextNetwork)
                            && nextNetwork == network
                            && memory.TryRead<int>(network + (ulong)DotaOffsets.Current.NetworkGameClientLocalPlayer, out var nextSlot)
                            && nextSlot == slot
                            && contextReader.TryRead(system, resolved.LocalControllerArrayAddress, out var next, out _)
                            && next == local;
                        var ok = modifiers.TryRead(local.HeroAddress, local.HeroHandle, client, Stable, out var snapshot, out reason);
                        IReadOnlyList<ModifierCasterObservation>? casters = null;
                        if (ok)
                        {
                            ok = casterReader.TryRead(system, snapshot!.Modifiers, client, Stable, out casters, out reason);
                            // Keep modifier and source observations tied to the same owner list.
                            // This is a bounded consistency check, not atomic frame isolation.
                            if (ok)
                            {
                                ok = modifiers.TryRead(local.HeroAddress, local.HeroHandle, client, Stable, out var after, out reason)
                                    && OwnerModifierSnapshotComparer.Equivalent(snapshot, after);
                                if (!ok) reason = "Owner modifiers changed around source acquisition; sample discarded.";
                            }
                        }
                        Emit(new
                        {
                            Kind = ok ? "modifier_sample" : "discarded",
                            Utc = DateTimeOffset.UtcNow,
                            Segment = segment,
                            PlayerId = local.DotaPlayerId,
                            Team = local.LocalTeam,
                            NetworkSlot = slot,
                            ControllerIndex = local.ControllerEntityIndex,
                            HeroHandle = local.HeroHandle.ToString("X8"),
                            Reason = reason,
                            Snapshot = ok ? snapshot : null,
                            CasterObservations = ok ? casters : null,
                            CurrentHeroVisibility = "Unknown",
                            CanDriveCurrentOverlay = false,
                        });
                    }
                    if (i + 1 < samples) await Task.Delay(200, cancel.Token);
                }
                Emit(new { Kind = "end", Utc = DateTimeOffset.UtcNow, Cancelled = cancel.IsCancellationRequested });
            }
            catch (OperationCanceledException) { Emit(new { Kind = "end", Utc = DateTimeOffset.UtcNow, Cancelled = true }); }
            finally { Console.CancelKeyPress -= handler; }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"AUDIT_REJECTED: {error.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Usage: DotaUtility.ModifierAudit [--samples 1..300] [--output new-file.jsonl]");
        return 2;
    }
}
