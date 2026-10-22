using System.Globalization;

namespace DotaUtility.Memory;

public sealed class BytePattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _wildcards;

    private BytePattern(byte[] bytes, bool[] wildcards)
    {
        _bytes = bytes;
        _wildcards = wildcards;
    }

    public int Length => _bytes.Length;

    public static BytePattern Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new FormatException("Pattern cannot be empty.");
        }

        var bytes = new byte[tokens.Length];
        var wildcards = new bool[tokens.Length];

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token is "?" or "??")
            {
                wildcards[index] = true;
                continue;
            }

            if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
            {
                throw new FormatException($"Invalid byte token '{token}' at index {index}.");
            }
        }

        return new BytePattern(bytes, wildcards);
    }

    public int FindFirst(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < Length)
        {
            return -1;
        }

        var lastStart = buffer.Length - Length;
        for (var start = 0; start <= lastStart; start++)
        {
            var matches = true;
            for (var index = 0; index < Length; index++)
            {
                if (!_wildcards[index] && buffer[start + index] != _bytes[index])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return start;
            }
        }

        return -1;
    }
}
