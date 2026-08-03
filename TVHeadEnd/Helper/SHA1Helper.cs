using System;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace TVHeadEnd.Helper
{
    public static class SHA1Helper
    {
        [SuppressMessage(
            "Security",
            "CA5350:Do Not Use Weak Cryptographic Algorithms",
            Justification = "The HTSP protocol specifies a SHA1 digest over the password and the server challenge. The algorithm is dictated by TVHeadend and cannot be changed client-side.")]
        public static byte[] GenerateSaltedSHA1(string plainTextString, byte[] saltBytes)
        {
            ArgumentNullException.ThrowIfNull(plainTextString);
            ArgumentNullException.ThrowIfNull(saltBytes);

            byte[] plainTextBytes = Encoding.UTF8.GetBytes(plainTextString);

            byte[] plainTextWithSaltBytes = new byte[plainTextBytes.Length + saltBytes.Length];
            plainTextBytes.CopyTo(plainTextWithSaltBytes, 0);
            saltBytes.CopyTo(plainTextWithSaltBytes, plainTextBytes.Length);

            return SHA1.HashData(plainTextWithSaltBytes);
        }
    }
}
