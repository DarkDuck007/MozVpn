using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace EncryptedBehind_NATTransportConsole
{
   internal class MozCryptStream
   {
      Stream _BaseStream { get; }
      CryptoStream WriteStream { get; }
      CryptoStream ReadStream { get; }
      public MozCryptStream(Stream baseStream, Aes AesCrypto)
      {
         _BaseStream = baseStream;
         WriteStream = new CryptoStream(_BaseStream, AesCrypto.CreateEncryptor(), CryptoStreamMode.Write);
         ReadStream = new CryptoStream(_BaseStream, AesCrypto.CreateDecryptor(), CryptoStreamMode.Read);
      }
      public MozCryptStream(Stream baseStream, byte[] Key, byte[] IV)
      {
         Aes AesCrypto = Aes.Create();
         AesCrypto.Key = Key;
         AesCrypto.IV = IV;
         _BaseStream = baseStream;
         WriteStream = new CryptoStream(_BaseStream, AesCrypto.CreateEncryptor(), CryptoStreamMode.Write);
         ReadStream = new CryptoStream(_BaseStream, AesCrypto.CreateDecryptor(), CryptoStreamMode.Read);

      }
   }
}
