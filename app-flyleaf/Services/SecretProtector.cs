using System;
using System.Security.Cryptography;
using System.Text;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Encrypts/decrypts small service secrets (e.g. the Plex token) at rest.
    ///
    /// Uses Windows DPAPI (<see cref="ProtectedData"/>) at <see cref="DataProtectionScope.CurrentUser"/>
    /// scope, additionally bound to an app-embedded entropy constant. The ciphertext
    /// is therefore tied to BOTH the current Windows user AND this application — i.e.
    /// "a key the video player itself has." Secrets are stored as base64 of the DPAPI blob.
    ///
    /// Decryption failures (blob copied to another machine/user, or corrupted) return
    /// null rather than throwing, so a bad secret degrades to "not configured".
    /// </summary>
    public static class SecretProtector
    {
        // App-embedded entropy. Not itself a secret in the cryptographic sense — DPAPI
        // provides the actual key material — but it binds the ciphertext to this app so a
        // stray blob from another DPAPI-using program on the same account can't be decrypted.
        private static readonly byte[] AppEntropy =
        {
            0x56, 0x50, 0x6C, 0x61, 0x79, 0x65, 0x72, 0x2E,
            0x53, 0x76, 0x63, 0x53, 0x65, 0x63, 0x72, 0x65,
            0x74, 0x2E, 0x76, 0x31, 0x9A, 0x3F, 0xC1, 0x7E,
        };

        /// <summary>Encrypt a plaintext secret to a base64 DPAPI blob. Empty in → empty out.</summary>
        public static string Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return "";
            try
            {
                var bytes  = Encoding.UTF8.GetBytes(plaintext);
                var cipher = ProtectedData.Protect(bytes, AppEntropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipher);
            }
            catch
            {
                // Should not happen on a supported Windows profile; never surface the secret.
                return "";
            }
        }

        /// <summary>Decrypt a base64 DPAPI blob. Returns null if it can't be decrypted.</summary>
        public static string? Unprotect(string? cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64)) return null;
            try
            {
                var cipher = Convert.FromBase64String(cipherBase64);
                var bytes  = ProtectedData.Unprotect(cipher, AppEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}
