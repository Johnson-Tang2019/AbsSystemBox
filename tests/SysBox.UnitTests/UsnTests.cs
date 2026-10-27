// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class UsnTests
{
    private static byte[] Batch(ushort version, string name)
    {
        var header = version == 2 ? 60 : 76; var ids = version == 2 ? 8 : 16;
        var text = Encoding.Unicode.GetBytes(name); var length = (header + text.Length + 7) & ~7;
        var buffer = new byte[length + 8]; var record = buffer.AsSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 999);
        BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[4..], version);
        record[8] = 255; record[8 + ids] = 123;
        if (version == 3) record[8 + 15] = 128;
        BinaryPrimitives.WriteInt64LittleEndian(record[(8 + 2 * ids)..], 12345678901234);
        BinaryPrimitives.WriteUInt32LittleEndian(record[(24 + 2 * ids)..], 0x2000);
        BinaryPrimitives.WriteUInt32LittleEndian(record[(36 + 2 * ids)..], 0x20);
        BinaryPrimitives.WriteUInt16LittleEndian(record[(header - 4)..], (ushort)text.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[(header - 2)..], (ushort)header);
        text.CopyTo(record[header..]); return buffer;
    }

    [Theory]
    [InlineData((ushort)2)] [InlineData((ushort)3)]
    public void ParsesVersionedIdsAndUnicodeWithoutPrecisionLoss(ushort version)
    {
        var batch = UsnBufferParser.Parse(Batch(version, "中文 🐈.pt"));
        var record = Assert.Single(batch.Records);
        Assert.Equal(999UL, batch.Continuation); Assert.Equal("中文 🐈.pt", record.Name);
        Assert.Equal(version == 2 ? 16 : 32, record.FileId.Length);
        Assert.Equal(12345678901234, record.Usn); Assert.Equal(0x2000u, record.Reason); Assert.Equal(0x20u, record.Attributes);
    }

    [Fact]
    public void RejectsAllTruncatedBuffersExceptValidEmptyPrefix()
    {
        var bytes = Batch(3, "test");
        for (var size = 0; size < bytes.Length; size++)
            if (size != 8) Assert.Throws<SysBoxOperationException>(() => UsnBufferParser.Parse(bytes.AsSpan(0, size)));
        Assert.Empty(UsnBufferParser.Parse(bytes.AsSpan(0, 8)).Records);
    }

    [Fact]
    public void RejectsUnsupportedVersionOffsetsLengthsAndInvalidUnicode()
    {
        var bytes = Batch(2, "test");
        foreach (var (offset, value) in new[] { (4, (ushort)4), (6, (ushort)1), (8 + 56, (ushort)65535), (8 + 58, (ushort)2), (8 + 58, (ushort)61) })
        {
            var copy = bytes.ToArray(); BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(offset == 4 || offset == 6 ? offset + 8 : offset), value);
            Assert.Throws<SysBoxOperationException>(() => UsnBufferParser.Parse(copy));
        }
        var malformed = Batch(2, "test"); malformed[8 + 60] = 0; malformed[8 + 61] = 0xd8;
        Assert.Throws<SysBoxOperationException>(() => UsnBufferParser.Parse(malformed));
        foreach (var name in new[] { "..", "a/b", "a\\b", "a\0b" }) Assert.Throws<SysBoxOperationException>(() => UsnBufferParser.Parse(Batch(2, name)));
    }

    [Fact]
    public void DetectsJournalReplacementTruncationAndImpossibleCheckpoint()
    {
        NtfsJournalGuard.Validate(new(42, 100, 200, 90), 42, 150);
        foreach (var journal in new[] { new UsnJournal(43, 100, 200, 90), new(42, 151, 200, 90), new(42, 100, 149, 90), new(42, 100, 200, 151) })
            Assert.Equal("REBUILD_REQUIRED", Assert.Throws<SysBoxOperationException>(() => NtfsJournalGuard.Validate(journal, 42, 150)).Error.Code);
    }

    [Fact]
    public void FuzzedRecordsCannotReadOutsideTheirBuffers()
    {
        var random = new Random(1729);
        for (var i = 0; i < 2000; i++)
        {
            var bytes = Batch(i % 2 == 0 ? (ushort)2 : (ushort)3, "sample");
            var offset = random.Next(8, bytes.Length); bytes[offset] ^= (byte)random.Next(1, 256);
            try { UsnBufferParser.Parse(bytes); }
            catch (SysBoxOperationException) { }
        }
    }
}
