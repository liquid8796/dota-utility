using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotaUtility.Memory;

// Research-only CLI. Reads the current process; never invokes or modifies game code.
if (args.Length != 1 || (args[0] != "client.dll" && args[0] != "server.dll"))
{
    Console.Error.WriteLine("Usage: DotaUtility.RegistryAudit <client.dll|server.dll>");
    return 2;
}

try
{
    using var memory = new Win32ProcessMemoryAccessor();
    if (!memory.TryAttach("dota2") || !memory.TryGetModule(args[0], out var module))
        throw new InvalidDataException("Dota or the requested local module is unavailable.");

    var expected = args[0] == "client.dll"
        ? "33f6c667b628c9a63106b6d9148f35eaa3a4ec665c43dfbc6b8440d320dfaccf"
        : "78e5533be686544508346ae69b80e812eaeff3f1cbc08c69ebdf02765739e189";
    using var file = File.OpenRead(module.FilePath);
    var digest = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
    if (digest != expected)
        throw new InvalidDataException($"Unsupported {args[0]} build {digest}; re-audit before reading pinned layouts.");

    var roots = args[0] == "client.dll"
        ? new ulong[] { 0x61DA928, 0x61DF358, 0x627D230, 0x61CECC8 }
        : new ulong[] { 0x513EAE8, 0x5147D90, 0x501C498 };
    var reader = new RegistryReader(memory, module);
    foreach (var rva in roots)
        reader.Visit(checked(module.BaseAddress + rva));

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        utc = DateTimeOffset.UtcNow,
        pid = memory.ProcessId,
        module = args[0],
        sha256 = digest,
        method = "Bounded Read/Write registered field descriptors; not per-packet transmission proof",
        scope = "Local installed module only; no access to another person's host",
        nodes = reader.Nodes,
    }, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error) when (error is IOException or InvalidOperationException or OverflowException or ArgumentException)
{
    Console.Error.WriteLine($"AUDIT_REJECTED: {error.Message}");
    return 2;
}

internal sealed class RegistryReader(IProcessMemoryAccessor memory, ProcessModuleSnapshot module)
{
    private readonly HashSet<ulong> _visited = [];
    private readonly HashSet<ulong> _active = [];
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    internal List<RegistryNode> Nodes { get; } = [];

    internal void Visit(ulong address, int depth = 0)
    {
        if (_active.Contains(address))
            throw new InvalidDataException("Cycle in class-parent graph.");
        if (_visited.Contains(address)) return;
        if (depth > 16 || _visited.Count >= 64)
            throw new InvalidDataException("Registry traversal bound exceeded.");
        _visited.Add(address);
        _active.Add(address);

        var name = Text(Read<ulong>(At(address, 8)), required: true);
        if (!_names.Add(name)) throw new InvalidDataException("Duplicate class name.");
        var count = Read<int>(At(address, 0x10));
        var fields = Read<ulong>(At(address, 0x18));
        if (count is < 0 or > 512) throw new InvalidDataException("Invalid field count.");
        var rows = new List<RegistryField>();
        var fieldNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var entry = Read<ulong>(At(fields, (ulong)i * 8));
            if (!memory.TryReadBytes(At(entry, 0), 0x158, out var bytes))
                throw new InvalidDataException("Unreadable field descriptor.");
            var fieldName = Text(BitConverter.ToUInt64(bytes, 8), required: true);
            if (!fieldNames.Add(fieldName)) throw new InvalidDataException("Duplicate direct field name.");
            var owner = Text(BitConverter.ToUInt64(bytes, 0x30), required: true);
            if (owner != name) throw new InvalidDataException("Field declaring-class mismatch.");
            rows.Add(new RegistryField(
                i, fieldName,
                Text(BitConverter.ToUInt64(bytes, 0x10)),
                Text(BitConverter.ToUInt64(bytes, 0x18)),
                Text(BitConverter.ToUInt64(bytes, 0x20)), owner,
                BitConverter.ToUInt32(bytes, 0x38),
                BitConverter.ToUInt32(bytes, 0x3C).ToString("X"),
                Text(BitConverter.ToUInt64(bytes, 0x70)), bytes[0x98]));
        }

        var parentCount = Read<int>(At(address, 0xB0));
        var parentArray = Read<ulong>(At(address, 0xB8));
        if (parentCount is < 0 or > 16) throw new InvalidDataException("Invalid parent count.");
        var parents = new List<(ulong Address, string Name)>();
        for (var i = 0; i < parentCount; i++)
        {
            var parent = Read<ulong>(At(parentArray, (ulong)i * 8));
            parents.Add((parent, Text(Read<ulong>(At(parent, 8)), required: true)));
        }

        if (Read<int>(At(address, 0x10)) != count || Read<ulong>(At(address, 0x18)) != fields
            || Read<int>(At(address, 0xB0)) != parentCount || Read<ulong>(At(address, 0xB8)) != parentArray)
            throw new InvalidDataException("Registry changed during acquisition.");

        var rva = address >= module.BaseAddress && address < checked(module.BaseAddress + (ulong)module.Size)
            ? (address - module.BaseAddress).ToString("X") : null;
        Nodes.Add(new RegistryNode(name, rva, count, parents.Select(p => p.Name).ToArray(), rows));
        foreach (var parent in parents) Visit(parent.Address, depth + 1);
        _active.Remove(address);
    }

    private T Read<T>(ulong address) where T : unmanaged =>
        memory.TryRead<T>(At(address, 0), out var value)
            ? value : throw new InvalidDataException("Required registry read failed.");

    private string Text(ulong pointer, bool required = false)
    {
        if (pointer == 0 && !required) return string.Empty;
        if (pointer < 0x10000 || pointer > 0x0000_7FFF_FFFF_FDFF
            || !memory.TryReadBytes(pointer, 512, out var bytes))
        {
            if (!required) return string.Empty;
            throw new InvalidDataException("Required registry string is unreadable.");
        }
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 1 || bytes.Take(end).Any(b => b is < 32 or > 126))
        {
            if (!required) return string.Empty;
            throw new InvalidDataException("Invalid registry string.");
        }
        return Encoding.ASCII.GetString(bytes, 0, end);
    }

    private static ulong At(ulong address, ulong offset)
    {
        var result = checked(address + offset);
        if (address < 0x10000 || result > 0x0000_7FFF_FFFF_FFFF)
            throw new InvalidDataException("Invalid user-space registry pointer.");
        return result;
    }
}

// Lower-case names keep the evidence JSON stable and independent of serializer options.
internal sealed record RegistryNode(string name, string? rva, int fieldCount,
    string[] parents, List<RegistryField> fields);
internal sealed record RegistryField(int index, string name, string cppType,
    string normalizedType, string baseType, string declaringClass, uint size,
    string offset, string encoder, byte rawFlags);
