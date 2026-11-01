using System;
using System.IO;

namespace HDRCalibrate.Dwm
{
    /// <summary>
    /// The request directory shared by the app and its elevated helper. With
    /// over-the-shoulder elevation (a standard user enters administrator
    /// credentials) the helper runs under another account whose
    /// %LOCALAPPDATA% differs, so the app passes its own directory explicitly.
    /// A supplied directory is accepted only in the exact shape the app creates.
    /// </summary>
    public static class DwmIpcRoot
    {
        public const string ArgumentName = "root";
        private const string DirectoryName = "Requests";
        private const string ParentName = "HDRCalibrate";

        public static string GetDefault()
        {
            return Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    ParentName,
                    DirectoryName))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public static string Validate(string root)
        {
            // Same-account elevation passes this process's own directory, which is
            // valid in any form Windows uses for %LOCALAPPDATA% (also redirected).
            if (IsOwnDefault(root)) return GetDefault();
            if (string.IsNullOrWhiteSpace(root) || root.Length > 240
                || root.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|', '/' }) >= 0)
                throw Rejected();
            // A local drive path only: no UNC, device or relative forms.
            if (root.Length < 4 || !char.IsLetter(root[0]) || root[1] != ':' || root[2] != '\\'
                || root.IndexOf(':', 2) >= 0)
                throw Rejected();
            var trimmed = root.TrimEnd('\\');
            string full;
            try { full = Path.GetFullPath(trimmed).TrimEnd('\\'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                throw Rejected();
            }
            // Rejects "." and ".." segments, which GetFullPath would normalize away.
            if (!string.Equals(full, trimmed, StringComparison.OrdinalIgnoreCase))
                throw Rejected();
            var parent = Path.GetDirectoryName(full);
            if (parent == null
                || !string.Equals(Path.GetFileName(full), DirectoryName, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(parent), ParentName, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(full))
                throw Rejected();
            foreach (var directory in new[] { full, parent })
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Der HDRCalibrate-Requests-Ordner darf kein Reparse Point sein.");
            }
            return full;
        }

        private static bool IsOwnDefault(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(root).TrimEnd('\\'), GetDefault(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private static UnauthorizedAccessException Rejected()
        {
            return new UnauthorizedAccessException(
                "Der IPC-Ordner muss ein vorhandener lokaler Ordner \"HDRCalibrate\\Requests\" sein.");
        }
    }
}
