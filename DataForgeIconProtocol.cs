using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataForge;

internal sealed class DataForgeIconManifestEntry
{
    internal DataForgeIconManifestEntry(
        string logicalName,
        string hash,
        int byteLength,
        int width,
        int height)
    {
        LogicalName = logicalName;
        Hash = hash;
        ByteLength = byteLength;
        Width = width;
        Height = height;
    }

    internal string LogicalName { get; }
    internal string Hash { get; }
    internal int ByteLength { get; }
    internal int Width { get; }
    internal int Height { get; }
    internal long PixelCount => (long)Width * Height;
}

internal sealed class DataForgeIconManifest
{
    internal DataForgeIconManifest(
        string revision,
        List<DataForgeIconManifestEntry> entries,
        int uniqueContentCount,
        long totalBytes,
        long totalPixels)
    {
        Revision = revision;
        Entries = new ReadOnlyCollection<DataForgeIconManifestEntry>(
            entries.ToArray());
        UniqueContentCount = uniqueContentCount;
        TotalBytes = totalBytes;
        TotalPixels = totalPixels;
    }

    internal string Revision { get; }
    internal IReadOnlyList<DataForgeIconManifestEntry> Entries { get; }
    internal int UniqueContentCount { get; }
    internal long TotalBytes { get; }
    internal long TotalPixels { get; }

}

internal static class DataForgeIconProtocol
{
    internal const int ProtocolVersion = 1;
    internal const int MaxIconBytes = 512 * 1024;
    internal const int MaxIconDimension = 1024;
    internal const long MaxIconPixels = 1_048_576;
    internal const int MaxTotalBytes = 2 * 1024 * 1024;
    internal const long MaxTotalPixels = 16_777_216;
    internal const int MaxIconCount = 128;
    internal const int MaxLogicalNameUtf8Bytes = 240;
    internal const int MaxManifestCharacters = 64 * 1024;

    private const int MinimumPngBytes = 33;
    private const string ManifestMagic = "DATAFORGE_ICONS_V1";
    private const string RevisionPrefix = "revision=";
    private static readonly byte[] PngSignature =
    {
        0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryNormalizeLogicalName(
        string? value,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Icon name is empty.";
            return false;
        }

        string trimmed = value!.Trim();
        if (trimmed.Length > MaxLogicalNameUtf8Bytes)
        {
            error = $"Icon name '{value}' is too long.";
            return false;
        }

        string candidate;
        try
        {
            candidate = trimmed.Normalize(NormalizationForm.FormC).Replace('\\', '/');
            if (StrictUtf8.GetByteCount(candidate) > MaxLogicalNameUtf8Bytes)
            {
                error = $"Icon name '{value}' exceeds the {MaxLogicalNameUtf8Bytes}-byte UTF-8 limit.";
                return false;
            }
        }
        catch (ArgumentException)
        {
            error = $"Icon name '{value}' contains invalid Unicode text.";
            return false;
        }
        if (candidate[0] == '/' ||
            (candidate.Length >= 2 && candidate[1] == ':'))
        {
            error = $"Icon name '{value}' must be relative.";
            return false;
        }

        foreach (char character in candidate)
        {
            if (char.IsControl(character))
            {
                error = $"Icon name '{value}' contains a control character.";
                return false;
            }
        }

        string[] segments = candidate.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string segment = segments[index];
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                error = $"Icon name '{value}' contains an empty or dot path segment.";
                return false;
            }

            if (!string.Equals(segment, segment.Trim(), StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal))
            {
                error = $"Icon name '{value}' contains a path segment with unsafe trailing characters.";
                return false;
            }

            if (segment.IndexOfAny(new[] { '<', '>', ':', '"', '|', '?', '*' }) >= 0)
            {
                error = $"Icon name '{value}' contains a character that is invalid in a file name.";
                return false;
            }
        }

        string fileName = segments[segments.Length - 1];
        int extensionIndex = fileName.LastIndexOf('.');
        if (extensionIndex < 0)
        {
            segments[segments.Length - 1] = fileName + ".png";
        }
        else if (extensionIndex == 0 ||
                 !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Icon name '{value}' must use the .png extension.";
            return false;
        }
        else
        {
            segments[segments.Length - 1] = fileName.Substring(0, fileName.Length - 4) + ".png";
        }

        normalized = string.Join("/", segments);
        if (StrictUtf8.GetByteCount(normalized) > MaxLogicalNameUtf8Bytes)
        {
            normalized = string.Empty;
            error = $"Icon name '{value}' exceeds the {MaxLogicalNameUtf8Bytes}-byte UTF-8 limit after normalization.";
            return false;
        }

        return true;
    }

    internal static string ComputeSha256(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        using SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(bytes);
        StringBuilder result = new(hash.Length * 2);
        foreach (byte value in hash)
        {
            result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        }

        return result.ToString();
    }

    internal static bool IsValidSha256(string? value)
    {
        if (value == null || value.Length != 64)
        {
            return false;
        }

        foreach (char character in value)
        {
            if ((character < '0' || character > '9') &&
                (character < 'a' || character > 'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryReadPngInfo(
        byte[]? bytes,
        out int width,
        out int height,
        out string error)
    {
        width = 0;
        height = 0;
        error = string.Empty;

        if (bytes == null || bytes.Length < MinimumPngBytes)
        {
            error = "PNG data is shorter than its signature and IHDR chunk.";
            return false;
        }

        if (bytes.Length > MaxIconBytes)
        {
            error = $"PNG data exceeds the {MaxIconBytes}-byte per-icon limit.";
            return false;
        }

        for (int index = 0; index < PngSignature.Length; index++)
        {
            if (bytes[index] != PngSignature[index])
            {
                error = "PNG signature is invalid.";
                return false;
            }
        }

        if (ReadUInt32BigEndian(bytes, 8) != 13 ||
            bytes[12] != (byte)'I' ||
            bytes[13] != (byte)'H' ||
            bytes[14] != (byte)'D' ||
            bytes[15] != (byte)'R')
        {
            error = "PNG does not start with a 13-byte IHDR chunk.";
            return false;
        }

        uint unsignedWidth = ReadUInt32BigEndian(bytes, 16);
        uint unsignedHeight = ReadUInt32BigEndian(bytes, 20);
        if (unsignedWidth == 0 || unsignedHeight == 0 ||
            unsignedWidth > MaxIconDimension || unsignedHeight > MaxIconDimension)
        {
            error = $"PNG dimensions must be between 1 and {MaxIconDimension} pixels.";
            return false;
        }

        long pixels = (long)unsignedWidth * unsignedHeight;
        if (pixels > MaxIconPixels)
        {
            error = $"PNG exceeds the {MaxIconPixels}-pixel per-icon limit.";
            return false;
        }

        width = (int)unsignedWidth;
        height = (int)unsignedHeight;
        return true;
    }

    internal static bool TryCreateManifest(
        IEnumerable<DataForgeIconManifestEntry>? entries,
        out DataForgeIconManifest manifest,
        out string error)
    {
        manifest = null!;
        error = string.Empty;
        if (entries == null)
        {
            error = "Icon manifest entries are missing.";
            return false;
        }

        List<DataForgeIconManifestEntry> canonicalEntries = new();
        HashSet<string> logicalNames = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, ContentDescription> uniqueContents = new(StringComparer.Ordinal);
        long totalBytes = 0;
        long totalPixels = 0;

        foreach (DataForgeIconManifestEntry? suppliedEntry in entries)
        {
            if (suppliedEntry == null)
            {
                error = "Icon manifest contains a null entry.";
                return false;
            }

            if (canonicalEntries.Count >= MaxIconCount)
            {
                error = $"Icon manifest exceeds the {MaxIconCount}-icon limit.";
                return false;
            }

            if (!TryNormalizeLogicalName(
                    suppliedEntry.LogicalName,
                    out string logicalName,
                    out error))
            {
                return false;
            }

            if (!logicalNames.Add(logicalName))
            {
                error = $"Icon manifest contains the duplicate logical name '{logicalName}'.";
                return false;
            }

            if (!IsValidSha256(suppliedEntry.Hash))
            {
                error = $"Icon '{logicalName}' has an invalid lowercase SHA-256 hash.";
                return false;
            }

            if (!TryValidateManifestDimensions(
                    logicalName,
                    suppliedEntry.ByteLength,
                    suppliedEntry.Width,
                    suppliedEntry.Height,
                    out error))
            {
                return false;
            }

            ContentDescription description = new(
                suppliedEntry.ByteLength,
                suppliedEntry.Width,
                suppliedEntry.Height);
            if (uniqueContents.TryGetValue(suppliedEntry.Hash, out ContentDescription existing))
            {
                if (!existing.Equals(description))
                {
                    error = $"Icons sharing hash '{suppliedEntry.Hash}' disagree on size or dimensions.";
                    return false;
                }
            }
            else
            {
                uniqueContents.Add(suppliedEntry.Hash, description);
                totalBytes += suppliedEntry.ByteLength;
                totalPixels += suppliedEntry.PixelCount;
                if (totalBytes > MaxTotalBytes)
                {
                    error = $"Icon manifest exceeds the {MaxTotalBytes}-byte aggregate limit.";
                    return false;
                }

                if (totalPixels > MaxTotalPixels)
                {
                    error = $"Icon manifest exceeds the {MaxTotalPixels}-pixel aggregate limit.";
                    return false;
                }
            }

            canonicalEntries.Add(new DataForgeIconManifestEntry(
                logicalName,
                suppliedEntry.Hash,
                suppliedEntry.ByteLength,
                suppliedEntry.Width,
                suppliedEntry.Height));
        }

        canonicalEntries.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left.LogicalName, right.LogicalName));
        string body = BuildCanonicalBody(canonicalEntries);
        string revision = ComputeSha256(StrictUtf8.GetBytes(body));
        manifest = new DataForgeIconManifest(
            revision,
            canonicalEntries,
            uniqueContents.Count,
            totalBytes,
            totalPixels);
        return true;
    }

    internal static string SerializeManifest(IEnumerable<DataForgeIconManifestEntry> entries)
    {
        if (!TryCreateManifest(entries, out DataForgeIconManifest manifest, out string error))
        {
            throw new ArgumentException(error, nameof(entries));
        }

        return SerializeManifest(manifest);
    }

    internal static string SerializeManifest(DataForgeIconManifest manifest)
    {
        if (manifest == null)
        {
            throw new ArgumentNullException(nameof(manifest));
        }

        string body = BuildCanonicalBody(manifest.Entries);
        return ManifestMagic + "\n" + RevisionPrefix + manifest.Revision + "\n" + body;
    }

    internal static bool TryParseManifest(
        string? payload,
        out DataForgeIconManifest manifest,
        out string error)
    {
        manifest = null!;
        error = string.Empty;
        if (payload == null)
        {
            error = "Icon manifest payload is missing.";
            return false;
        }

        if (payload.Length > MaxManifestCharacters)
        {
            error = $"Icon manifest exceeds the {MaxManifestCharacters}-character limit.";
            return false;
        }

        string normalizedPayload = payload.Replace("\r\n", "\n");
        if (normalizedPayload.IndexOf('\r') >= 0)
        {
            error = "Icon manifest contains an invalid line ending.";
            return false;
        }

        int headerEnd = normalizedPayload.IndexOf('\n');
        int revisionEnd = headerEnd < 0
            ? -1
            : normalizedPayload.IndexOf('\n', headerEnd + 1);
        if (headerEnd < 0 || revisionEnd < 0 ||
            !string.Equals(
                normalizedPayload.Substring(0, headerEnd),
                ManifestMagic,
                StringComparison.Ordinal))
        {
            error = "Icon manifest header is invalid.";
            return false;
        }

        string revisionLine = normalizedPayload.Substring(
            headerEnd + 1,
            revisionEnd - headerEnd - 1);
        if (!revisionLine.StartsWith(RevisionPrefix, StringComparison.Ordinal))
        {
            error = "Icon manifest revision line is invalid.";
            return false;
        }

        string suppliedRevision = revisionLine.Substring(RevisionPrefix.Length);
        if (!IsValidSha256(suppliedRevision))
        {
            error = "Icon manifest revision is not a lowercase SHA-256 hash.";
            return false;
        }

        string suppliedBody = normalizedPayload.Substring(revisionEnd + 1);
        if (suppliedBody.EndsWith("\n", StringComparison.Ordinal))
        {
            error = "Icon manifest has a non-canonical trailing line.";
            return false;
        }

        string[] lines = suppliedBody.Length == 0
            ? Array.Empty<string>()
            : suppliedBody.Split('\n');
        if (lines.Length > MaxIconCount)
        {
            error = $"Icon manifest exceeds the {MaxIconCount}-icon limit.";
            return false;
        }

        List<DataForgeIconManifestEntry> entries = new(lines.Length);
        foreach (string line in lines)
        {
            string[] fields = line.Split('|');
            if (fields.Length != 5 || fields[0].Length == 0)
            {
                error = "Icon manifest contains a malformed entry line.";
                return false;
            }

            string logicalName;
            try
            {
                byte[] encodedName = Convert.FromBase64String(fields[0]);
                logicalName = StrictUtf8.GetString(encodedName);
                if (!string.Equals(
                        Convert.ToBase64String(StrictUtf8.GetBytes(logicalName)),
                        fields[0],
                        StringComparison.Ordinal))
                {
                    error = "Icon manifest contains a non-canonical encoded name.";
                    return false;
                }
            }
            catch (FormatException)
            {
                error = "Icon manifest contains an invalid base64 name.";
                return false;
            }
            catch (DecoderFallbackException)
            {
                error = "Icon manifest contains a name that is not valid UTF-8.";
                return false;
            }

            if (!TryParsePositiveInt(fields[2], out int byteLength) ||
                !TryParsePositiveInt(fields[3], out int width) ||
                !TryParsePositiveInt(fields[4], out int height))
            {
                error = $"Icon '{logicalName}' has malformed numeric metadata.";
                return false;
            }

            entries.Add(new DataForgeIconManifestEntry(
                logicalName,
                fields[1],
                byteLength,
                width,
                height));
        }

        if (!TryCreateManifest(entries, out DataForgeIconManifest parsed, out error))
        {
            return false;
        }

        string canonicalBody = BuildCanonicalBody(parsed.Entries);
        if (!string.Equals(suppliedBody, canonicalBody, StringComparison.Ordinal))
        {
            error = "Icon manifest entries are not in canonical form or order.";
            return false;
        }

        if (!string.Equals(suppliedRevision, parsed.Revision, StringComparison.Ordinal))
        {
            error = "Icon manifest revision does not match its canonical body.";
            return false;
        }

        manifest = parsed;
        return true;
    }

    private static bool TryValidateManifestDimensions(
        string logicalName,
        int byteLength,
        int width,
        int height,
        out string error)
    {
        error = string.Empty;
        if (byteLength < MinimumPngBytes || byteLength > MaxIconBytes)
        {
            error = $"Icon '{logicalName}' has a byte length outside the permitted range.";
            return false;
        }

        if (width <= 0 || height <= 0 ||
            width > MaxIconDimension || height > MaxIconDimension)
        {
            error = $"Icon '{logicalName}' has dimensions outside the permitted range.";
            return false;
        }

        if ((long)width * height > MaxIconPixels)
        {
            error = $"Icon '{logicalName}' exceeds the per-icon pixel limit.";
            return false;
        }

        return true;
    }

    private static bool TryParsePositiveInt(string text, out int value)
    {
        return int.TryParse(
                   text,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value > 0 &&
               string.Equals(
                   value.ToString(CultureInfo.InvariantCulture),
                   text,
                   StringComparison.Ordinal);
    }

    private static string BuildCanonicalBody(
        IReadOnlyList<DataForgeIconManifestEntry> entries)
    {
        StringBuilder body = new();
        for (int index = 0; index < entries.Count; index++)
        {
            if (index > 0)
            {
                body.Append('\n');
            }

            DataForgeIconManifestEntry entry = entries[index];
            body.Append(Convert.ToBase64String(StrictUtf8.GetBytes(entry.LogicalName)));
            body.Append('|');
            body.Append(entry.Hash);
            body.Append('|');
            body.Append(entry.ByteLength.ToString(CultureInfo.InvariantCulture));
            body.Append('|');
            body.Append(entry.Width.ToString(CultureInfo.InvariantCulture));
            body.Append('|');
            body.Append(entry.Height.ToString(CultureInfo.InvariantCulture));
        }

        return body.ToString();
    }

    private static uint ReadUInt32BigEndian(byte[] bytes, int offset)
    {
        return ((uint)bytes[offset] << 24) |
               ((uint)bytes[offset + 1] << 16) |
               ((uint)bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }

    private readonly struct ContentDescription : IEquatable<ContentDescription>
    {
        internal ContentDescription(int byteLength, int width, int height)
        {
            ByteLength = byteLength;
            Width = width;
            Height = height;
        }

        private int ByteLength { get; }
        private int Width { get; }
        private int Height { get; }

        public bool Equals(ContentDescription other) =>
            ByteLength == other.ByteLength &&
            Width == other.Width &&
            Height == other.Height;
    }
}
