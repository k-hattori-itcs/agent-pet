using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AgentCompanion.Services;

internal static class ProxyProtocol
{
    public static bool IsStreamingBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("stream", out var stream) &&
                   stream.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                   stream.GetBoolean();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static byte[] Dechunk(ReadOnlySpan<byte> data, int maxOutputBytes)
    {
        using var output = new MemoryStream();
        var position = 0;
        while (position < data.Length)
        {
            var lineEndOffset = data[position..].IndexOf("\r\n"u8);
            if (lineEndOffset < 0 || lineEndOffset > 128)
                throw new InvalidDataException("Invalid chunk-size line.");
            var sizeText = Encoding.ASCII.GetString(data.Slice(position, lineEndOffset));
            var extension = sizeText.IndexOf(';');
            if (extension >= 0)
                sizeText = sizeText[..extension];
            if (!int.TryParse(sizeText.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("Invalid chunk size.");
            position += lineEndOffset + 2;

            if (size == 0)
            {
                var trailer = data[position..];
                if (trailer.Length == 0 || trailer.SequenceEqual("\r\n"u8) || trailer.IndexOf("\r\n\r\n"u8) >= 0)
                    return output.ToArray();
                throw new InvalidDataException("Incomplete chunk trailer.");
            }

            if (output.Length + size > maxOutputBytes)
                throw new InvalidDataException("Dechunked response exceeded the limit.");
            if (position + size + 2 > data.Length || data[position + size] != '\r' || data[position + size + 1] != '\n')
                throw new InvalidDataException("Incomplete chunk data.");
            output.Write(data.Slice(position, size));
            position += size + 2;
        }
        throw new InvalidDataException("Missing final chunk.");
    }
}
