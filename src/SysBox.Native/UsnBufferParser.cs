// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SysBox.Abstractions;

namespace SysBox.Native;

public static class UsnBufferParser
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);
    public static NtfsBatch Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length is < 8 or > 1_048_576) throw Invalid("Invalid batch length.");
        var continuation = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        var records = new List<UsnRecord>(); var position = 8;
        while (position < buffer.Length)
        {
            var remaining = buffer[position..];
            if (remaining.Length < 8) throw Invalid("Truncated record header.");
            var length = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            if (length < 8 || length > remaining.Length || length % 8 != 0) throw Invalid("Invalid record bounds/alignment.");
            var record = remaining[..checked((int)length)];
            var major = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
            var minor = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
            var minimum = major switch { 2 => 60, 3 => 76, _ => throw Invalid("Unsupported USN record version.") };
            if (minor != 0 || length < minimum) throw Invalid("Unsupported or truncated record layout.");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[(minimum - 4)..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[(minimum - 2)..]);
            if (nameOffset < minimum || nameOffset % 2 != 0 || nameLength == 0 || nameLength % 2 != 0 || nameOffset > length || nameLength > length - nameOffset)
                throw Invalid("Invalid filename bounds.");
            string name;
            try { name = StrictUnicode.GetString(record.Slice(nameOffset, nameLength)); }
            catch (DecoderFallbackException) { throw Invalid("Invalid UTF-16 filename."); }
            var idLength = major == 2 ? 8 : 16;
            var usnOffset = 8 + 2 * idLength;
            var attributes = BinaryPrimitives.ReadUInt32LittleEndian(record[(usnOffset + 28)..]);
            var root = name == "." && (attributes & 0x10) != 0 && record.Slice(8, idLength).SequenceEqual(record.Slice(8 + idLength, idLength));
            if (name.IndexOfAny(['\0', '\\', '/']) >= 0 || name == ".." || (name == "." && !root)) throw Invalid("Invalid filename component.");
            var usn = BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
            if (usn < 0) throw Invalid("Negative USN.");
            records.Add(new(Convert.ToHexString(record.Slice(8, idLength)), Convert.ToHexString(record.Slice(8 + idLength, idLength)),
                usn, BinaryPrimitives.ReadUInt32LittleEndian(record[(usnOffset + 16)..]),
                attributes, name, major));
            position = checked(position + (int)length);
        }
        return new(continuation, records.AsReadOnly(), false);
    }
    private static SysBoxOperationException Invalid(string detail) => new(new("USN_INVALID", "USN 数据格式不受支持，需要重新建立索引。", detail));
}
