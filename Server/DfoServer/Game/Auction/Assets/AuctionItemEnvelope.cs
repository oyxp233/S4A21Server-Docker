using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Auction.Assets
{
    // This is a persistence descriptor, never an ItemCore wire entry. Use the
    // codec to deserialize it so integrity AND instance semantics are checked.
    internal sealed class AuctionItemEnvelope
    {
        internal const int CurrentFormatVersion = 1;
        private readonly byte[] nativeCore;
        private readonly byte[] contentHash;

        private AuctionItemEnvelope(int formatVersion, byte[] nativeCore99,
            string detailJson, string extensionsJson, byte[] expectedHash)
        {
            if (formatVersion != CurrentFormatVersion)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Unsupported auction item format.");
            if (nativeCore99 == null || nativeCore99.Length != ItemCore.Size)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidNativeCore, "Auction item core must be exactly 99 bytes.");
            if (detailJson == null || extensionsJson == null)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Auction item descriptor fields must be explicit.");

            FormatVersion = formatVersion;
            nativeCore = (byte[])nativeCore99.Clone();
            DetailJson = detailJson;
            ExtensionsJson = extensionsJson;
            contentHash = ComputeHash(formatVersion, nativeCore, detailJson, extensionsJson);
            if (expectedHash != null && (expectedHash.Length != contentHash.Length
                || !CryptographicOperations.FixedTimeEquals(contentHash, expectedHash)))
                throw new AuctionItemCodecException(AuctionItemCodecError.IntegrityMismatch, "Auction item content hash mismatch.");
        }

        internal int FormatVersion { get; }
        internal byte[] NativeCore99 => (byte[])nativeCore.Clone();
        internal string DetailJson { get; }
        internal string ExtensionsJson { get; }
        internal byte[] ContentHash => (byte[])contentHash.Clone();
        internal string ContentHashHex => Convert.ToHexString(contentHash);

        internal static AuctionItemEnvelope Create(byte[] nativeCore99, string detailJson, string extensionsJson)
            => new(CurrentFormatVersion, nativeCore99, detailJson, extensionsJson, null);

        internal static AuctionItemEnvelope FromStored(int formatVersion, byte[] nativeCore99,
            string detailJson, string extensionsJson, byte[] expectedHash)
        {
            if (expectedHash == null)
                throw new AuctionItemCodecException(AuctionItemCodecError.InvalidEnvelope, "Missing auction item content hash.");
            return new(formatVersion, nativeCore99, detailJson, extensionsJson, expectedHash);
        }

        internal void VerifyIntegrity()
        {
            var actual = ComputeHash(FormatVersion, nativeCore, DetailJson, ExtensionsJson);
            if (!CryptographicOperations.FixedTimeEquals(actual, contentHash))
                throw new AuctionItemCodecException(AuctionItemCodecError.IntegrityMismatch, "Auction item content hash mismatch.");
        }

        private static byte[] ComputeHash(int version, byte[] core, string detail, string extensions)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes("A21-AUCTION-ITEM\0"));
                writer.Write(version);
                WriteBytes(writer, core);
                WriteBytes(writer, new UTF8Encoding(false, true).GetBytes(detail));
                WriteBytes(writer, new UTF8Encoding(false, true).GetBytes(extensions));
            }
            return SHA256.HashData(stream.ToArray());
        }

        private static void WriteBytes(BinaryWriter writer, byte[] value)
        {
            writer.Write(value.Length);
            writer.Write(value);
        }
    }
}
