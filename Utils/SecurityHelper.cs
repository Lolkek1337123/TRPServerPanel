using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TRPServerPanel.Utils
{
    public static class SecurityHelper
    {
        private static readonly byte[] Salt = Encoding.ASCII.GetBytes("TRP_SECURITY_SALT_2026");

        private static string GetMachineGuid()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid")?.ToString() ?? Environment.MachineName;
            }
            catch
            {
                return Environment.MachineName;
            }
        }

        private static Aes CreateAes()
        {
            var aes = Aes.Create();
            var password = GetMachineGuid();
            
            // Fix for SYSLIB0060: Using the recommended Pbkdf2 static method
            aes.Key = Rfc2898DeriveBytes.Pbkdf2(password, Salt, 1000, HashAlgorithmName.SHA256, 32);
            aes.IV = Rfc2898DeriveBytes.Pbkdf2(password, Salt, 1000, HashAlgorithmName.SHA256, 16);
            
            return aes;
        }

        public static string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return plainText;

            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] encryptedBytes = ProtectedData.Protect(plainBytes, Salt, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(encryptedBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SECURITY] Encrypt failed, falling back to AES: {ex.Message}");
                try
                {
                    using var aes = CreateAes();
                    using var ms = new MemoryStream();
                    using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                    using (var sw = new StreamWriter(cs))
                    {
                        sw.Write(plainText);
                    }
                    return Convert.ToBase64String(ms.ToArray());
                }
                catch
                {
                    return plainText;
                }
            }
        }

        public static string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return cipherText;
            if (!IsBase64String(cipherText)) return cipherText; // Return as-is if not encrypted (backwards compatibility)

            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherText);
                byte[] decryptedBytes = ProtectedData.Unprotect(cipherBytes, Salt, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch
            {
                try
                {
                    using var aes = CreateAes();
                    using var ms = new MemoryStream(Convert.FromBase64String(cipherText));
                    using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    using (var sr = new StreamReader(cs))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch
                {
                    return cipherText; // Fallback to raw if decryption fails
                }
            }
        }

        private static bool IsBase64String(string base64)
        {
            if (string.IsNullOrEmpty(base64) || base64.Length % 4 != 0) return false;
            try
            {
                Convert.FromBase64String(base64);
                return true;
            }
            catch { return false; }
        }
    }
}
