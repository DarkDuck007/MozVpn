using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace DirtySocksASP
{
   class EncryptionProvider : IDisposable
   {
      private byte[] key;
      private byte[] iv;
      private Aes aes;
      private ICryptoTransform enc;
      private ICryptoTransform dec;
      //static MemoryStream DecryptedStreamToEnc = new MemoryStream();
      //static MemoryStream EncryptedStreamToDec = new MemoryStream();
      //static CryptoStream EncStream;
      //static CryptoStream DecStream;
      public EncryptionProvider()
      {
         //key = Encoding.ASCII.GetBytes("A Super safe passwordwith32bytes");
         //iv = Encoding.ASCII.GetBytes("random iv 123456");
         //aes.Key = key;
         //aes.IV = iv;
         aes = Aes.Create();
         aes.Key = Encoding.ASCII.GetBytes("A Super safe passwordwith32bytes");
         aes.IV = Encoding.ASCII.GetBytes("random iv 123456");
         aes.BlockSize = 128;
         aes.Mode = CipherMode.CFB;
         aes.Padding = PaddingMode.Zeros;
         enc = aes.CreateEncryptor();
         dec = aes.CreateDecryptor();
         //EncStream = new CryptoStream(DecryptedStreamToEnc, enc, CryptoStreamMode.Write);
         //DecStream = new CryptoStream(EncryptedStreamToDec, dec, CryptoStreamMode.Read);
      }
      public byte[] Encrypt(byte[] data)
      {
         return PerformCryptography(data, enc);
      }
      public byte[] Encrypt(byte[] data, int offset, int count)
      {
         return PerformCryptography(data, offset, count, enc);
      }
      public byte[] Decrypt(byte[] data)
      {
         return PerformCryptography(data, dec);
      }
      public byte[] Decrypt(byte[] data, int offset, int count)
      {
         return PerformCryptography(data, offset, count, dec);
      }
      private byte[] PerformCryptography(byte[] data, ICryptoTransform cryptoTransform)
      {
         using (var ms = new MemoryStream())
         using (var cryptoStream = new CryptoStream(ms, cryptoTransform, CryptoStreamMode.Write))
         {
            cryptoStream.Write(data, 0, data.Length);
            cryptoStream.FlushFinalBlock();

            return ms.ToArray();
         }
      }
      private byte[] PerformCryptography(byte[] data, int offset, int count, ICryptoTransform cryptoTransform)
      {
         using (var ms = new MemoryStream())
         using (var cryptoStream = new CryptoStream(ms, cryptoTransform, CryptoStreamMode.Write))
         {
            cryptoStream.Write(data, offset, count);
            cryptoStream.FlushFinalBlock();

            return ms.ToArray();
         }
      }
      public async Task<byte[]> EncryptAsync(byte[] data)
      {
         return await PerformCryptographyAsync(data, enc);
      }
      public async Task<byte[]> EncryptAsync(byte[] data, int offset, int count)
      {
         return await PerformCryptographyAsync(data, offset, count, enc);
      }
      public async Task<byte[]> DecryptAsync(byte[] data)
      {
         return await PerformCryptographyAsync(data, dec);
      }
      public async Task<byte[]> DecryptAsync(byte[] data, int offset, int count)
      {
         return await PerformCryptographyAsync(data, offset, count, dec);
      }
      private async Task<byte[]> PerformCryptographyAsync(byte[] data, ICryptoTransform cryptoTransform)
      {
         using (var ms = new MemoryStream())
         using (var cryptoStream = new CryptoStream(ms, cryptoTransform, CryptoStreamMode.Write))
         {
            await cryptoStream.WriteAsync(data, 0, data.Length);
            cryptoStream.FlushFinalBlock();

            return ms.ToArray();
         }
      }
      private async Task<byte[]> PerformCryptographyAsync(byte[] data, int offset, int count, ICryptoTransform cryptoTransform)
      {
         using (var ms = new MemoryStream())
         using (var cryptoStream = new CryptoStream(ms, cryptoTransform, CryptoStreamMode.Write))
         {
            await cryptoStream.WriteAsync(data, offset, count);
            cryptoStream.FlushFinalBlock();

            return ms.ToArray();
         }
      }
      public void Dispose()
      {
         //DecryptedStreamToEnc.Dispose();
         //EncryptedStreamToDec.Dispose();
         //EncStream.Dispose();
         //DecStream.Dispose();
         enc.Dispose();
         dec.Dispose();
         aes.Dispose();
      }
   }
}
