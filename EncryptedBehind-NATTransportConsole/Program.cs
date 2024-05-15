using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EncryptedBehind_NATTransportConsole
{
   internal class Program
   {
      static void Main(string[] args)
      {
         RSA rSA = RSA.Create();
         //byte[] InputBytes = File.ReadAllBytes("C:\\Users\\topol\\Desktop\\4_5870692120028778632.mp4");
         string InputText = "THIS IS A TEST TEXT FOR AES CRYPTO STREAM";
         byte[] InputBytes = Encoding.ASCII.GetBytes(InputText);
         Console.WriteLine($"File len: {InputBytes.Length}");
         TripleDES DES = TripleDES.Create();
         DES.GenerateKey();
         DES.GenerateIV();
         byte[] AESKEY = DES.Key;
         byte[] AESIV = DES.IV;
         Console.WriteLine(Convert.ToHexString(AESKEY));
         Console.WriteLine(Convert.ToHexString(AESIV));
         //ICryptoTransform AESEncTr = aes.CreateEncryptor(AESKEY, AESIV);
         Stopwatch ST = Stopwatch.StartNew();

         byte[] EncBytes;
         using (MemoryStream MS = new MemoryStream())
         {
            using (CryptoStream CS = new CryptoStream(MS, DES.CreateEncryptor(), CryptoStreamMode.Write))
            {
               CS.Write(InputBytes, 0, InputBytes.Length);
               //using (StreamWriter SW = new StreamWriter(CS))
               //{
               //   SW.WriteLine(InputText);
               //   SW.Flush();
               //}
            }
            EncBytes = MS.ToArray();
         }
         Console.WriteLine($"Encoded len: {EncBytes.Length}");
         //Compress
         byte[] CompressedEncodedBytes;
         using (MemoryStream output = new MemoryStream())
         {
            using (DeflateStream dstream = new DeflateStream(output, CompressionLevel.SmallestSize))
            {
               dstream.Write(EncBytes, 0, EncBytes.Length);
               dstream.Flush();
            }
            CompressedEncodedBytes = output.ToArray();
         }
         Console.WriteLine($"Compressed, Encoded len: {CompressedEncodedBytes.Length}");

         byte[] DecompressedBytes;

         using (MemoryStream input = new MemoryStream(CompressedEncodedBytes))
         {
            using (MemoryStream output = new MemoryStream())
            {
               using (DeflateStream dstream = new DeflateStream(input, CompressionMode.Decompress))
               {
                  dstream.CopyTo(output);
               }
               DecompressedBytes = output.ToArray();
            }
         }
         Console.WriteLine($"Decompressed len: {DecompressedBytes.Length}");

         TripleDES Decryptoraes = TripleDES.Create();
         Decryptoraes.Key = AESKEY;
         Decryptoraes.IV = AESIV;
         using (MemoryStream MS = new MemoryStream(DecompressedBytes))
         {
            using (MemoryStream Output = new MemoryStream())
            {

               using (CryptoStream CS = new CryptoStream(MS, Decryptoraes.CreateDecryptor(), CryptoStreamMode.Read))
               {
                  CS.CopyTo(Output);
                  //using (StreamReader SR = new StreamReader(CS))
                  //{
                  //   Console.WriteLine(SR.ReadLine());
                  //}
               }
               ST.Stop();
               Console.WriteLine($"The entire encoding/decoding took {ST.ElapsedTicks} Ticks or {ST.ElapsedMilliseconds} ms");
               ST.Restart();
               //File.WriteAllBytes("C:\\Users\\topol\\Desktop\\DecodedFile.mp4", Output.ToArray());
               ST.Stop();
               Console.WriteLine(Encoding.ASCII.GetString(Output.ToArray()));
               //Console.WriteLine($"Writing to file took {ST.ElapsedTicks} Ticks or {ST.ElapsedMilliseconds} ms");
            }
         }

      }
   }
}
