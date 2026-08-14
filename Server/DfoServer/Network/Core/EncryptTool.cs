using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace DfoServer.Network
{
    internal class EncryptTool
    {
        public static byte[] DecryptData(byte[] data, string key)
        {
            
            var keyBytes = GetKeyBytes(key);
            
            var decompressedData = data;
            if (data[0] == 0x78)
                decompressedData = ZlibDecompress(data);
            
            var decryptedData = Aes128DecryptECB(decompressedData, keyBytes);
            return decryptedData;
        }

        public static byte[] EncryptData(byte[] data, string key, bool compress = true)
        {
            
            var keyBytes = GetKeyBytes(key);
            
            data = GeneratePadding(data, 16);
            
            var encryptedData = Aes128EncryptECB(data, keyBytes);

            if (!compress)
                return encryptedData;

            
            var compressedData = ZlibCompress(encryptedData);
            return compressedData;
        }

        public static byte[] GetKeyBytes(string key)
        {
            var keyBytes = new byte[16];
            var keySourceBytes = Encoding.UTF8.GetBytes(key);
            Array.Copy(keySourceBytes, keyBytes, Math.Min(keySourceBytes.Length, keyBytes.Length));
            return keyBytes;
        }

        public static byte[] GeneratePadding(byte[] data, int align)
        {
            int paddingLength = (align - (data.Length % align)) % align;
            if (paddingLength == 0)
                return data;
            byte[] paddedData = new byte[data.Length + paddingLength];
            Array.Copy(data, paddedData, data.Length);
            for (int i = data.Length; i < paddedData.Length; i++)
            {
                paddedData[i] = 0x00;
            }
            return paddedData;
        }

        public static byte[] ZlibCompress(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (data.Length == 0)
                return new byte[0];

            using (MemoryStream resultStream = new MemoryStream())
            {
                resultStream.WriteByte(0x78);
                resultStream.WriteByte(0x9C);

                byte[] compressedData;
                using (MemoryStream tempStream = new MemoryStream())
                {
                    using (System.IO.Compression.DeflateStream compressionStream = new System.IO.Compression.DeflateStream(tempStream, System.IO.Compression.CompressionMode.Compress, true))
                    {
                        compressionStream.Write(data, 0, data.Length);
                    }
                    compressedData = tempStream.ToArray();
                }

                resultStream.Write(compressedData, 0, compressedData.Length);

                const uint BASE = 65521;
                uint a = 1, b = 0;
                foreach (byte byteValue in data)
                {
                    a = (a + byteValue) % BASE;
                    b = (b + a) % BASE;
                }
                uint checksum = (b << 16) | a;

                resultStream.WriteByte((byte)(checksum >> 24));
                resultStream.WriteByte((byte)(checksum >> 16));
                resultStream.WriteByte((byte)(checksum >> 8));
                resultStream.WriteByte((byte)checksum);

                return resultStream.ToArray();
            }
        }

        public static byte[] ZlibDecompress(byte[] compressedData)
        {
            if (compressedData.Length < 2)
                throw new ArgumentException("压缩数据太短");

            if (compressedData[0] != 0x78)
                throw new ArgumentException("无效的zlib头");

            if (compressedData.Length < 6)
                throw new ArgumentException("压缩数据太短，无法包含校验和");

            int compressedDataLength = compressedData.Length - 6;
            if (compressedDataLength < 0)
                throw new ArgumentException("压缩数据长度不足");

            uint storedChecksum = (uint)((compressedData[compressedData.Length - 4] << 24) |
                                         (compressedData[compressedData.Length - 3] << 16) |
                                         (compressedData[compressedData.Length - 2] << 8) |
                                         compressedData[compressedData.Length - 1]);

            byte[] decompressedData;
            using (MemoryStream compressedStream = new MemoryStream(compressedData, 2, compressedDataLength))
            using (System.IO.Compression.DeflateStream decompressionStream = new System.IO.Compression.DeflateStream(compressedStream, System.IO.Compression.CompressionMode.Decompress))
            using (MemoryStream resultStream = new MemoryStream())
            {
                decompressionStream.CopyTo(resultStream);
                decompressedData = resultStream.ToArray();
            }

            const uint BASE = 65521;
            uint a = 1, b = 0;
            foreach (byte byteValue in decompressedData)
            {
                a = (a + byteValue) % BASE;
                b = (b + a) % BASE;
            }
            uint calculatedChecksum = (b << 16) | a;

            if (storedChecksum != calculatedChecksum)
                throw new System.IO.InvalidDataException($"ADLER32 校验和验证失败。期望: 0x{storedChecksum:X8}, 实际: 0x{calculatedChecksum:X8}");

            return decompressedData;
        }

        public static byte[] Aes128EncryptECB(byte[] data, byte[] key)
        {
            if (key.Length != 16)
                throw new ArgumentException("AES-128需要16字节密钥");

            if (data.Length % 16 != 0)
                throw new ArgumentException("数据长度必须是16的倍数");

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;

                using (ICryptoTransform encryptor = aes.CreateEncryptor())
                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                {
                    cs.Write(data, 0, data.Length);
                    cs.FlushFinalBlock();
                    return ms.ToArray();
                }
            }
        }

        public static byte[] Aes128DecryptECB(byte[] data, byte[] key)
        {
            if (key.Length != 16)
                throw new ArgumentException("AES-128需要16字节密钥");

            if (data.Length % 16 != 0)
                throw new ArgumentException("数据长度必须是16的倍数");

            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                using (MemoryStream ms = new MemoryStream(data))
                using (CryptoStream cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                using (MemoryStream result = new MemoryStream())
                {
                    cs.CopyTo(result);
                    return result.ToArray();
                }
            }
        }
    }
}
