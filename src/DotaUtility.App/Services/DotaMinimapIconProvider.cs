using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaUtility.App.Services;

public sealed class DotaMinimapIconProvider
{
    private const byte VtexFormatBgra8888 = 28;
    private static readonly byte[] IconsDirectorySignature =
        Encoding.ASCII.GetBytes("panorama/images/heroes/icons\0");

    private static readonly string[] CandidatePakDirectories =
    [
        @"D:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\dota",
        @"C:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\dota",
        @"E:\Program Files (x86)\Steam\steamapps\common\dota 2 beta\game\dota",
        @"D:\SteamLibrary\steamapps\common\dota 2 beta\game\dota",
        @"C:\SteamLibrary\steamapps\common\dota 2 beta\game\dota",
        @"E:\SteamLibrary\steamapps\common\dota 2 beta\game\dota",
    ];

    private readonly object _gate = new();
    private readonly Dictionary<string, VpkIconEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private string? _dotaPakDirectory;
    private bool _indexLoaded;

    public DotaMinimapIconProvider(string? dotaPakDirectory = null)
    {
        _dotaPakDirectory = dotaPakDirectory;
    }

    public int LoadedEntryCount
    {
        get
        {
            lock (_gate)
            {
                EnsureIndexLoaded(processId: null);
                return _entries.Count;
            }
        }
    }

    public bool TryGetHeroIcon(string? heroName, int? processId, out ImageSource? icon)
    {
        icon = null;
        if (string.IsNullOrWhiteSpace(heroName))
        {
            return false;
        }

        var normalized = NormalizeHeroKey(heroName);
        lock (_gate)
        {
            EnsureIndexLoaded(processId);
            if (_iconCache.TryGetValue(normalized, out icon))
            {
                return icon is not null;
            }

            if (!_entries.TryGetValue(normalized, out var entry)
                && !_entries.TryGetValue($"npc_dota_hero_{normalized}", out entry))
            {
                _iconCache[normalized] = null;
                return false;
            }

            icon = LoadBitmapFromArchive(entry);
            _iconCache[normalized] = icon;
            return icon is not null;
        }
    }

    private void EnsureIndexLoaded(int? processId)
    {
        if (_indexLoaded)
        {
            return;
        }

        var pakDir = ResolveDotaPakDirectory(processId);
        if (string.IsNullOrWhiteSpace(pakDir))
        {
            return;
        }

        var dirVpkPath = Path.Combine(pakDir, "pak01_dir.vpk");
        if (!File.Exists(dirVpkPath))
        {
            return;
        }

        try
        {
            var bytes = File.ReadAllBytes(dirVpkPath);
            var dirOffset = bytes.AsSpan().IndexOf(IconsDirectorySignature);
            if (dirOffset < 0)
            {
                return;
            }

            var pos = dirOffset + IconsDirectorySignature.Length;
            while (pos < bytes.Length)
            {
                var nameStart = pos;
                while (pos < bytes.Length && bytes[pos] != 0)
                {
                    pos++;
                }

                if (pos >= bytes.Length)
                {
                    break;
                }

                var nameLen = pos - nameStart;
                pos++; // skip '\0'
                if (nameLen == 0)
                {
                    break;
                }

                if (pos + 18 > bytes.Length)
                {
                    break;
                }

                var fileName = Encoding.ASCII.GetString(bytes, nameStart, nameLen);
                var preloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 4, 2));
                var archiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos + 6, 2));
                var entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 8, 4));
                var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 12, 4));
                pos += 18 + preloadBytes;

                var heroKey = fileName.EndsWith("_png", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^4]
                    : fileName;

                var entry = new VpkIconEntry(archiveIndex, entryOffset, entryLength);
                _entries[heroKey] = entry;

                const string Prefix = "npc_dota_hero_";
                if (heroKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                    && heroKey.Length > Prefix.Length)
                {
                    var shortKey = heroKey[Prefix.Length..];
                    _entries.TryAdd(shortKey, entry);
                }
            }

            _dotaPakDirectory = pakDir;
            _indexLoaded = _entries.Count > 0;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private ImageSource? LoadBitmapFromArchive(VpkIconEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_dotaPakDirectory) || entry.Length < 64 || entry.Length > 262_144)
        {
            return null;
        }

        var archivePath = Path.Combine(_dotaPakDirectory, $"pak01_{entry.ArchiveIndex:D3}.vpk");
        if (!File.Exists(archivePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (entry.Offset + entry.Length > stream.Length)
            {
                return null;
            }

            stream.Position = entry.Offset;
            var vtex = new byte[entry.Length];
            var totalRead = 0;
            while (totalRead < vtex.Length)
            {
                var read = stream.Read(vtex, totalRead, vtex.Length - totalRead);
                if (read <= 0)
                {
                    return null;
                }

                totalRead += read;
            }

            var nonStreamingSize = BinaryPrimitives.ReadInt32LittleEndian(vtex.AsSpan(0, 4));
            var dataRelOffset = BinaryPrimitives.ReadInt32LittleEndian(vtex.AsSpan(0x20, 4));
            var dataOffset = 0x20 + dataRelOffset;
            if (nonStreamingSize <= 0
                || nonStreamingSize >= vtex.Length
                || dataOffset < 0
                || dataOffset + 0x1C > vtex.Length)
            {
                return null;
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(vtex.AsSpan(dataOffset + 0x14, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(vtex.AsSpan(dataOffset + 0x16, 2));
            var format = vtex[dataOffset + 0x1A];
            var stride = width * 4;
            var pixelByteCount = stride * height;

            if (width is 0 or > 256
                || height is 0 or > 256
                || format != VtexFormatBgra8888
                || nonStreamingSize + pixelByteCount > vtex.Length)
            {
                return null;
            }

            var pixelBytes = new byte[pixelByteCount];
            Buffer.BlockCopy(vtex, nonStreamingSize, pixelBytes, 0, pixelByteCount);

            var bitmap = BitmapSource.Create(
                width,
                height,
                96d,
                96d,
                PixelFormats.Bgra32,
                palette: null,
                pixelBytes,
                stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveDotaPakDirectory(int? processId)
    {
        if (!string.IsNullOrWhiteSpace(_dotaPakDirectory)
            && File.Exists(Path.Combine(_dotaPakDirectory, "pak01_dir.vpk")))
        {
            return _dotaPakDirectory;
        }

        if (processId is int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                var exePath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    var gameDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exePath)!, "..", ".."));
                    var candidate = Path.Combine(gameDir, "dota");
                    if (File.Exists(Path.Combine(candidate, "pak01_dir.vpk")))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }
        }

        foreach (var candidate in CandidatePakDirectories)
        {
            if (File.Exists(Path.Combine(candidate, "pak01_dir.vpk")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string NormalizeHeroKey(string heroName)
    {
        var trimmed = heroName.Trim();
        if (trimmed.EndsWith("_png", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }

    private readonly record struct VpkIconEntry(ushort ArchiveIndex, uint Offset, uint Length);
}
