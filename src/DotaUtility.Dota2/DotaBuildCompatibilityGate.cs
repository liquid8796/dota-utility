using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DotaUtility.Memory;

namespace DotaUtility.Dota2;

/// <summary>
/// Prevents layout-dependent production reads from running against a client
/// binary whose object layout has not been explicitly audited.
/// </summary>
public sealed class DotaBuildCompatibilityGate : IDotaBuildCompatibilityGate
{
    private const string ClientModuleName = "client.dll";
    private const long MinSchemaValidationFileLength = 0x100_000;
    private const long MaxSchemaValidationFileLength = 256L * 1024 * 1024;

    // Client builds for which the production local-controller and
    // replicated team-data layout were independently audited together.
    private static readonly IReadOnlyDictionary<string, string> SupportedClientBuilds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dfec84e4e3b89518dcd78c308e27db35d04c7e286d67a01166eecd5a39ceefd6"] =
                "2026-10-19 local-controller/replication profile",
            ["25ea6c6774974f4da2188db89080074eeeb5ebd66d0675bcf1d029d44fd8de80"] =
                "2026-10-20 local-controller/replication profile",
        };

    private readonly IProcessMemoryAccessor _memory;
    private readonly object _gate = new();
    private CacheEntry? _cache;

    public DotaBuildCompatibilityGate(IProcessMemoryAccessor memory)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
    }

    public bool TryValidate(out string detail)
    {
        detail = string.Empty;
        if (!_memory.IsAttached || _memory.ProcessId is not int processId)
        {
            Invalidate();
            detail = "Dota process is not attached; build compatibility cannot be validated.";
            return false;
        }

        var attachmentGeneration = (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration;
        if (!_memory.TryGetModule(ClientModuleName, out var clientModule)
            || string.IsNullOrWhiteSpace(clientModule.FilePath))
        {
            detail = "client.dll is not loaded yet; build compatibility cannot be validated.";
            return false;
        }

        if (!Path.IsPathFullyQualified(clientModule.FilePath)
            || !string.Equals(
                Path.GetFileName(clientModule.FilePath),
                ClientModuleName,
                StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Unexpected client.dll module path: {clientModule.FilePath}";
            return false;
        }

        long length;
        long writeTimeTicks;
        try
        {
            var info = new FileInfo(clientModule.FilePath);
            if (!info.Exists)
            {
                detail = $"client.dll backing file is unavailable: {clientModule.FilePath}";
                return false;
            }

            length = info.Length;
            writeTimeTicks = info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException exception)
        {
            detail = $"Could not inspect client.dll build identity: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            detail = $"Could not inspect client.dll build identity: {exception.Message}";
            return false;
        }

        lock (_gate)
        {
            if (attachmentGeneration is long generation
                && _cache is { } cached
                && cached.AttachmentGeneration == generation
                && cached.ProcessId == processId
                && string.Equals(cached.FilePath, clientModule.FilePath, StringComparison.OrdinalIgnoreCase)
                && cached.ModuleBaseAddress == clientModule.BaseAddress
                && cached.ModuleSize == clientModule.Size
                && cached.FileLength == length
                && cached.LastWriteTimeUtcTicks == writeTimeTicks
                && (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration == generation)
            {
                detail = cached.Detail;
                return cached.IsSupported;
            }
        }

        if (!TryReadMappedPeIdentity(clientModule, out var mappedIdentity, out detail))
        {
            return false;
        }

        PeIdentity fileIdentity;
        string digest;
        var supported = false;
        string? profile = null;
        try
        {
            using var stream = new FileStream(
                clientModule.FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (!TryReadFilePeIdentity(stream, out fileIdentity, out detail))
            {
                return false;
            }

            digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            supported = SupportedClientBuilds.TryGetValue(digest, out profile)
                || TryValidateSchemaLayout(stream, DotaOffsets.Current, out profile);
        }
        catch (IOException exception)
        {
            detail = $"Could not hash client.dll for compatibility validation: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            detail = $"Could not hash client.dll for compatibility validation: {exception.Message}";
            return false;
        }

        // Steam may replace or modify the backing file while the digest is
        // being computed. Never cache a digest tied to stale file metadata.
        try
        {
            var afterHash = new FileInfo(clientModule.FilePath);
            if (!afterHash.Exists
                || afterHash.Length != length
                || afterHash.LastWriteTimeUtc.Ticks != writeTimeTicks)
            {
                Invalidate();
                detail = "client.dll backing file changed during build validation; retry after the update finishes.";
                return false;
            }
        }
        catch (IOException exception)
        {
            Invalidate();
            detail = $"Could not recheck client.dll build identity: {exception.Message}";
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            Invalidate();
            detail = $"Could not recheck client.dll build identity: {exception.Message}";
            return false;
        }

        if (mappedIdentity.TimeDateStamp != fileIdentity.TimeDateStamp
            || mappedIdentity.SizeOfImage != fileIdentity.SizeOfImage
            || clientModule.Size != checked((int)fileIdentity.SizeOfImage))
        {
            detail =
                "Loaded client.dll does not match its backing file. " +
                $"mapped timestamp=0x{mappedIdentity.TimeDateStamp:X8}, " +
                $"file timestamp=0x{fileIdentity.TimeDateStamp:X8}, " +
                $"mapped image size=0x{mappedIdentity.SizeOfImage:X}, " +
                $"file image size=0x{fileIdentity.SizeOfImage:X}, " +
                $"module size=0x{clientModule.Size:X}. " +
                "Layout-dependent memory reads are disabled until Dota is restarted on a consistent build.";
            return false;
        }

        if (!_memory.IsAttached
            || _memory.ProcessId != processId
            || (_memory as IProcessAttachmentGeneration)?.AttachmentGeneration != attachmentGeneration)
        {
            Invalidate();
            detail = "Process attachment changed during build validation; retry on the current process.";
            return false;
        }

        detail = supported
            ? $"Validated client.dll build {digest[..12]} ({profile})."
            : $"Unsupported client.dll build {digest}. Layout-dependent memory reads are disabled until this build is audited.";

        lock (_gate)
        {
            // A custom accessor without generation identity must never reuse
            // process-bound cached build validation across a PID reuse.
            _cache = attachmentGeneration is long generation
                ? new CacheEntry(
                    processId,
                    generation,
                    clientModule.FilePath,
                    clientModule.BaseAddress,
                    clientModule.Size,
                    length,
                    writeTimeTicks,
                    supported,
                    detail)
                : null;
        }

        return supported;
    }

    private void Invalidate()
    {
        lock (_gate)
        {
            _cache = null;
        }
    }

    private static bool TryValidateSchemaLayout(
        FileStream stream,
        DotaOffsets offsets,
        out string? profile)
    {
        profile = null;
        if (stream.Length is < MinSchemaValidationFileLength or > MaxSchemaValidationFileLength)
        {
            return false;
        }

        var data = new byte[checked((int)stream.Length)];
        stream.Position = 0;
        if (!ReadExactly(stream, data))
        {
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x3C, 4));
        if (peOffset is < 0x40 or > 0x10000
            || peOffset + 24 + 64 > data.Length)
        {
            return false;
        }

        var numberOfSections = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 6, 2));
        var sizeOfOptionalHeader = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(peOffset + 20, 2));
        var optionalHeader = peOffset + 24;
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(optionalHeader + 24, 8));
        var sectionTableOffset = optionalHeader + sizeOfOptionalHeader;
        if (numberOfSections is 0 or > 64
            || sectionTableOffset + (numberOfSections * 40) > data.Length)
        {
            return false;
        }

        var sections = new PeSection[numberOfSections];
        for (var i = 0; i < numberOfSections; i++)
        {
            var entry = data.AsSpan(sectionTableOffset + (i * 40), 40);
            sections[i] = new PeSection(
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(20, 4)));
        }

        ReadOnlySpan<(string FieldName, int ExpectedOffset)> requiredFields =
        [
            ("m_bIsLocalPlayerController", offsets.PlayerControllerIsLocalPlayer),
            ("m_bIsHLTV", offsets.PlayerControllerIsHltv),
            ("m_nPlayerID", offsets.PlayerControllerPlayerId),
            ("m_hAssignedHero", offsets.PlayerControllerAssignedHero),
            ("m_iTeamNum", offsets.BaseEntityTeamNumber),
            ("m_pGameSceneNode", offsets.BaseEntityGameSceneNode),
            ("m_iTeamVisibilityBitmask", offsets.BaseModelEntityTeamVisibilityBitmask),
            ("m_vecAbsOrigin", offsets.GameSceneNodeAbsOrigin),
            ("m_bNPCVisibleState", offsets.DataNonSpectatorNpcVisibleState),
        ];

        ReadOnlySpan<byte> span = data;
        Span<byte> vaBytes = stackalloc byte[sizeof(ulong)];
        foreach (var (fieldName, expectedOffset) in requiredFields)
        {
            var needle = Encoding.ASCII.GetBytes($"\0{fieldName}\0");
            var searchStart = 0;
            var fieldMatched = false;
            while (searchStart < span.Length)
            {
                var relativeIndex = span[searchStart..].IndexOf(needle);
                if (relativeIndex < 0)
                {
                    break;
                }

                var stringFileOffset = searchStart + relativeIndex + 1;
                if (TryFileOffsetToVa(sections, imageBase, (uint)stringFileOffset, out var stringVa))
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(vaBytes, stringVa);
                    var refSearchStart = 0;
                    while (refSearchStart < span.Length)
                    {
                        var refRelative = span[refSearchStart..].IndexOf(vaBytes);
                        if (refRelative < 0)
                        {
                            break;
                        }

                        var refOffset = refSearchStart + refRelative;
                        if ((refOffset & 7) == 0 && refOffset + 0x18 <= span.Length)
                        {
                            var candidateOffset = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(refOffset + 0x10, 4));
                            var metadataCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(refOffset + 0x14, 4));
                            if (candidateOffset == expectedOffset && metadataCount is >= 0 and <= 64)
                            {
                                fieldMatched = true;
                                break;
                            }
                        }

                        refSearchStart = refOffset + 1;
                    }
                }

                if (fieldMatched)
                {
                    break;
                }

                searchStart += relativeIndex + 1;
            }

            if (!fieldMatched)
            {
                return false;
            }
        }

        profile = "schema-verified Source 2 layout profile";
        return true;
    }

    private static bool TryFileOffsetToVa(
        ReadOnlySpan<PeSection> sections,
        ulong imageBase,
        uint fileOffset,
        out ulong virtualAddress)
    {
        foreach (var section in sections)
        {
            if (fileOffset >= section.PointerToRawData
                && fileOffset < section.PointerToRawData + section.SizeOfRawData)
            {
                virtualAddress = imageBase + section.VirtualAddress + (fileOffset - section.PointerToRawData);
                return true;
            }
        }

        virtualAddress = 0;
        return false;
    }

    private bool TryReadMappedPeIdentity(
        ProcessModuleSnapshot module,
        out PeIdentity identity,
        out string detail)
    {
        identity = default;
        detail = string.Empty;
        if (!_memory.TryReadBytes(module.BaseAddress, 0x100, out var dosHeader)
            || dosHeader.Length < 0x40
            || dosHeader[0] != (byte)'M'
            || dosHeader[1] != (byte)'Z')
        {
            detail = "Could not read a valid mapped client.dll DOS header.";
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.AsSpan(0x3C, 4));
        if (peOffset is < 0x40 or > 0x10000)
        {
            detail = $"Mapped client.dll has an invalid PE header offset 0x{peOffset:X}.";
            return false;
        }

        var headerLength = checked(peOffset + 0x100);
        if (!_memory.TryReadBytes(module.BaseAddress, headerLength, out var headers)
            || !TryParsePeIdentity(headers, peOffset, out identity))
        {
            detail = "Could not parse the mapped client.dll PE identity.";
            return false;
        }

        return true;
    }

    private static bool TryReadFilePeIdentity(
        FileStream stream,
        out PeIdentity identity,
        out string detail)
    {
        identity = default;
        detail = string.Empty;
        Span<byte> dosHeader = stackalloc byte[0x100];
        stream.Position = 0;
        if (!ReadExactly(stream, dosHeader)
            || dosHeader[0] != (byte)'M'
            || dosHeader[1] != (byte)'Z')
        {
            detail = "client.dll backing file does not contain a valid DOS header.";
            return false;
        }

        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.Slice(0x3C, 4));
        if (peOffset is < 0x40 or > 0x10000)
        {
            detail = $"client.dll backing file has an invalid PE header offset 0x{peOffset:X}.";
            return false;
        }

        var headerLength = checked(peOffset + 0x100);
        var headers = new byte[headerLength];
        stream.Position = 0;
        if (!ReadExactly(stream, headers)
            || !TryParsePeIdentity(headers, peOffset, out identity))
        {
            detail = "Could not parse the client.dll backing-file PE identity.";
            return false;
        }

        stream.Position = 0;
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }

    private static bool TryParsePeIdentity(
        ReadOnlySpan<byte> headers,
        int peOffset,
        out PeIdentity identity)
    {
        identity = default;
        const ushort Amd64Machine = 0x8664;
        const ushort Pe32PlusMagic = 0x020B;
        const int OptionalHeaderOffsetFromPe = 24;
        const int SizeOfImageOffsetFromOptional = 56;

        if (peOffset < 0
            || peOffset + OptionalHeaderOffsetFromPe + SizeOfImageOffsetFromOptional + sizeof(uint) > headers.Length
            || headers[peOffset] != (byte)'P'
            || headers[peOffset + 1] != (byte)'E'
            || headers[peOffset + 2] != 0
            || headers[peOffset + 3] != 0)
        {
            return false;
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(peOffset + 4, 2));
        var timeDateStamp = BinaryPrimitives.ReadUInt32LittleEndian(headers.Slice(peOffset + 8, 4));
        var optionalHeader = peOffset + OptionalHeaderOffsetFromPe;
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(optionalHeader, 2));
        var sizeOfImage = BinaryPrimitives.ReadUInt32LittleEndian(
            headers.Slice(optionalHeader + SizeOfImageOffsetFromOptional, 4));

        if (machine != Amd64Machine
            || magic != Pe32PlusMagic
            || sizeOfImage == 0
            || sizeOfImage > int.MaxValue)
        {
            return false;
        }

        identity = new PeIdentity(timeDateStamp, sizeOfImage);
        return true;
    }

    private sealed record CacheEntry(
        int ProcessId,
        long AttachmentGeneration,
        string FilePath,
        ulong ModuleBaseAddress,
        int ModuleSize,
        long FileLength,
        long LastWriteTimeUtcTicks,
        bool IsSupported,
        string Detail);

    private readonly record struct PeIdentity(
        uint TimeDateStamp,
        uint SizeOfImage);

    private readonly record struct PeSection(
        uint VirtualAddress,
        uint SizeOfRawData,
        uint PointerToRawData);
}
