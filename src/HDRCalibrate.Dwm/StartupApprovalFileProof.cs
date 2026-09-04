using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace HDRCalibrate.Dwm
{
    // Binds the elevated operation to the exact enabled policy validated by the app.
    // A revocation or replacement while UAC is open must abort before any patch write.
    [Serializable]
    public sealed class StartupApprovalFileProof
    {
        public string PolicyPath { get; set; }
        public string Sha256 { get; set; }

        public static StartupApprovalFileProof Capture(string path)
        {
            return new StartupApprovalFileProof { PolicyPath = Path.GetFullPath(path), Sha256 = ReadHash(path) };
        }

        public void RequireUnchanged()
        {
            if (string.IsNullOrWhiteSpace(PolicyPath) || !Path.IsPathRooted(PolicyPath)
                || Sha256 == null || Sha256.Length != 64
                || !Sha256.All(c => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                throw new InvalidDataException("Der dauerhafte Startfreigabenachweis fehlt oder ist ungültig.");
            if (!string.Equals(Sha256, ReadHash(PolicyPath), StringComparison.Ordinal))
                throw new InvalidDataException("Die Startfreigabe wurde während der Aktivierung geändert oder widerrufen.");
        }

        private static string ReadHash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > 64 * 1024)
                    throw new InvalidDataException("Die Startfreigabedatei besitzt eine ungültige Größe.");
                using (var sha = SHA256.Create())
                    return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }
    }
}
