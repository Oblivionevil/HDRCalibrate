using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>
    /// Replaces files below one root with an on-volume backup, so a failed update
    /// restores the previous files exactly. Existing files are renamed (allowed for
    /// running images), never deleted before <see cref="Commit"/>.
    /// </summary>
    public sealed class FileTransaction
    {
        private readonly string _root;
        private readonly string _backupDirectory;
        private readonly ISetupLog _log;
        private readonly List<Tuple<string, string>> _replaced = new List<Tuple<string, string>>();
        private readonly List<string> _placed = new List<string>();
        private readonly List<string> _createdDirectories = new List<string>();
        private bool _completed;

        public FileTransaction(string root, ISetupLog log)
        {
            _root = Path.GetFullPath(root);
            _backupDirectory = Path.Combine(_root, ProductInfo.BackupDirectoryPrefix + Guid.NewGuid().ToString("N"));
            _log = log ?? new NullSetupLog();
        }

        /// <summary>
        /// Present while a transaction may still need its backup: from the first
        /// moved file until Commit or a complete Rollback. A backup that keeps it
        /// (failed rollback, interrupted setup) holds the only previous copies.
        /// </summary>
        public const string PendingMarkerFileName = "transaction-pending.txt";

        public void Put(string sourceFile, string relativePath)
        {
            if (_completed) throw new InvalidOperationException("Die Dateitransaktion ist bereits abgeschlossen.");
            var target = SetupPaths.Combine(_root, relativePath);
            // Before any directory is created: a junction on the way (for example
            // Native) would otherwise redirect the write outside the root.
            var reparsePoint = FindReparsePoint(target);
            if (reparsePoint != null)
                throw new IOException("Der Zielpfad enthält einen Reparse-Punkt: " + reparsePoint);
            EnsureDirectory(Path.GetDirectoryName(target));
            if (File.Exists(target)) MoveToBackup(target);
            try
            {
                File.Copy(sourceFile, target, false);
            }
            catch
            {
                // Remove a partial copy so the backup can be restored by Rollback.
                if (File.Exists(target)) _placed.Add(target);
                throw;
            }
            _placed.Add(target);
            _log.Write("Datei installiert: " + relativePath);
        }

        /// <summary>
        /// Moves a file that is no longer part of the package into the backup, so a
        /// rollback restores it and Commit deletes it.
        /// </summary>
        /// <returns>False when the file stays in place (locked or a reparse point).</returns>
        public bool TryRemove(string relativePath)
        {
            if (_completed) throw new InvalidOperationException("Die Dateitransaktion ist bereits abgeschlossen.");
            var target = SetupPaths.Combine(_root, relativePath);
            if (FindReparsePoint(target) != null) return false;
            if (!File.Exists(target)) return true;
            try
            {
                MoveToBackup(target);
                _log.Write("Veraltete Datei entfernt: " + relativePath);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _log.Write("Veraltete Datei bleibt erhalten: " + relativePath + " (" + ex.Message + ")");
                return false;
            }
        }

        private void MoveToBackup(string target)
        {
            if (!Directory.Exists(_backupDirectory))
            {
                Directory.CreateDirectory(_backupDirectory);
                File.SetAttributes(_backupDirectory, FileAttributes.Directory | FileAttributes.Hidden);
                // Written before the first move, so even an interrupted setup keeps it.
                File.WriteAllText(Path.Combine(_backupDirectory, PendingMarkerFileName),
                    "HDR Calibrate Setup: Sicherung einer nicht abgeschlossenen Dateitransaktion. " +
                    "Sie wird erst nach einer späteren erfolgreichen Installation entfernt.\r\n");
            }
            var backup = Path.Combine(_backupDirectory, _replaced.Count.ToString("D4") + "-" + Path.GetFileName(target));
            File.Move(target, backup);
            _replaced.Add(Tuple.Create(target, backup));
        }

        public void Commit()
        {
            _completed = true;
            TryDeletePendingMarker();
            TryDeleteBackupDirectory();
        }

        /// <returns>True when every previous file was restored.</returns>
        public bool Rollback()
        {
            if (_completed) return true;
            _completed = true;
            var complete = true;
            foreach (var placed in Enumerable.Reverse(_placed))
            {
                try { File.Delete(placed); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    complete = false;
                    _log.Write("Rücknahme: neue Datei nicht entfernbar: " + placed + " (" + ex.Message + ")");
                }
            }
            foreach (var replaced in Enumerable.Reverse(_replaced))
            {
                try { File.Move(replaced.Item2, replaced.Item1); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    complete = false;
                    _log.Write("Rücknahme: Sicherung nicht wiederherstellbar: " + replaced.Item1 + " (" + ex.Message + ")");
                }
            }
            foreach (var directory in Enumerable.Reverse(_createdDirectories))
            {
                try { Directory.Delete(directory, false); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            // Keep backups that could not be restored; they are the only copy, and
            // the pending marker protects them from the next run's cleanup.
            if (complete)
            {
                TryDeletePendingMarker();
                TryDeleteBackupDirectory();
            }
            else
            {
                _log.Write("Unvollständige Rücknahme; Sicherung bleibt erhalten: " + _backupDirectory);
            }
            return complete;
        }

        private void TryDeletePendingMarker()
        {
            try { File.Delete(Path.Combine(_backupDirectory, PendingMarkerFileName)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <returns>The first reparse point from the root down to the target, or null.</returns>
        private string FindReparsePoint(string target)
        {
            var path = new Stack<string>();
            for (var current = target; current != null && current.Length >= _root.Length;
                 current = Path.GetDirectoryName(current))
                path.Push(current);
            return path.FirstOrDefault(SetupPaths.IsReparsePoint);
        }

        private void EnsureDirectory(string directory)
        {
            var missing = new Stack<string>();
            for (var current = directory; !Directory.Exists(current); current = Path.GetDirectoryName(current))
            {
                if (current == null || current.Length < _root.Length)
                    throw new IOException("Der Zielordner fehlt: " + _root);
                missing.Push(current);
            }
            while (missing.Count > 0)
            {
                var created = missing.Pop();
                Directory.CreateDirectory(created);
                _createdDirectories.Add(created);
            }
            if (SetupPaths.IsReparsePoint(directory))
                throw new IOException("Der Zielordner ist ein Reparse-Punkt: " + directory);
        }

        private void TryDeleteBackupDirectory()
        {
            if (!Directory.Exists(_backupDirectory)) return;
            foreach (var file in Directory.GetFiles(_backupDirectory))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    // A replaced image may still be mapped; the next setup run removes it.
                    _log.Write("Sicherung bleibt bis zum nächsten Setup-Lauf: " + file);
                }
            }
            try { Directory.Delete(_backupDirectory, false); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
        }

        /// <summary>Removes backups left by an earlier run whose files could not be deleted then.</summary>
        /// <param name="includePending">
        /// Also removes backups of unfinished transactions. Only valid once a later
        /// transaction committed (or the application was removed): their previous
        /// files are superseded then, while before that they are the only copy.
        /// </param>
        public static void DeleteStaleBackups(string root, ISetupLog log, bool includePending = false)
        {
            if (!Directory.Exists(root)) return;
            foreach (var directory in Directory.GetDirectories(root, ProductInfo.BackupDirectoryPrefix + "*"))
            {
                var name = Path.GetFileName(directory);
                if (name.Length != ProductInfo.BackupDirectoryPrefix.Length + 32 || SetupPaths.IsReparsePoint(directory)
                    || Directory.GetDirectories(directory).Length != 0)
                    continue;
                if (!includePending && File.Exists(Path.Combine(directory, PendingMarkerFileName)))
                {
                    log?.Write("Sicherung einer unvollständigen Rücknahme bleibt erhalten: " + directory);
                    continue;
                }
                foreach (var file in Directory.GetFiles(directory))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        log?.Write("Alte Sicherung noch gesperrt: " + file);
                    }
                }
                try { Directory.Delete(directory, false); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }
    }
}
