using HDRCalibrate.Setup.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace HDRCalibrate.Tests
{
    /// <summary>
    /// Offline tests of the WPF setup engine. Every file, registry value, shortcut
    /// and stop request goes to an isolated temp sandbox or in-memory fake.
    /// </summary>
    internal static class SetupTests
    {
        private const string Version = "1.7.3";

        internal sealed class MemoryRegistry : ISetupRegistry
        {
            public readonly Dictionary<string, Dictionary<string, object>> Keys =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

            public string GetString(string keyPath, string valueName) =>
                Keys.TryGetValue(keyPath, out var values) && values.TryGetValue(valueName, out var value) ? value as string : null;
            public bool KeyExists(string keyPath) => Keys.ContainsKey(keyPath);
            public void SetString(string keyPath, string valueName, string value) => Key(keyPath)[valueName] = value;
            public void SetDword(string keyPath, string valueName, int value) => Key(keyPath)[valueName] = value;
            public void DeleteValue(string keyPath, string valueName)
            {
                if (Keys.TryGetValue(keyPath, out var values)) values.Remove(valueName);
            }
            public void DeleteKey(string keyPath) => Keys.Remove(keyPath);

            private Dictionary<string, object> Key(string keyPath)
            {
                if (!Keys.TryGetValue(keyPath, out var values))
                    Keys[keyPath] = values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                return values;
            }
        }

        internal sealed class FileShortcutWriter : IShortcutWriter
        {
            public void Create(string linkPath, string targetPath, string workingDirectory, string iconPath)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(linkPath));
                File.WriteAllText(linkPath, targetPath + "|" + workingDirectory + "|" + iconPath);
            }
        }

        internal sealed class RecordingStopper : IAppStopper
        {
            public int Result;
            public readonly List<Tuple<string, string>> Calls = new List<Tuple<string, string>>();

            public int Stop(string commandExecutable, string targetExecutable)
            {
                Calls.Add(Tuple.Create(commandExecutable, targetExecutable));
                Check(File.Exists(commandExecutable), "The stop command does not exist.");
                return Result;
            }
        }

        internal sealed class Sandbox : IDisposable
        {
            public Sandbox()
            {
                Root = Path.Combine(Path.GetTempPath(), "HDRCalibrate_SetupTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Root);
                Locations = new SetupLocations(
                    Path.Combine(Root, "LocalAppData", "Programs", "HDR Calibrate"),
                    Path.Combine(Root, "StartMenu", "Programs", "HDR Calibrate"),
                    Path.Combine(Root, "Desktop"),
                    Path.Combine(Root, "Temp"));
                Directory.CreateDirectory(Locations.TempDirectory);
                Directory.CreateDirectory(Locations.DesktopDirectory);
                SetupExecutable = Path.Combine(Root, "Downloads", "HDRCalibrate-Setup-v" + Version + "-x64.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(SetupExecutable));
                File.WriteAllText(SetupExecutable, "setup " + Version);
                Context = new SetupContext(Locations, Registry, new FileShortcutWriter(), Stopper, new SetupText(true), null,
                    () => new DateTime(2026, 10, 10, 12, 0, 0));
            }

            public string Root { get; }
            public SetupLocations Locations { get; }
            public MemoryRegistry Registry { get; } = new MemoryRegistry();
            public RecordingStopper Stopper { get; } = new RecordingStopper();
            public SetupContext Context { get; }
            public string SetupExecutable { get; }
            public string Install => Locations.DefaultInstallDirectory;

            public void Dispose()
            {
                foreach (var file in Directory.GetFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, true);
            }
        }

        internal static Dictionary<string, byte[]> PackageFiles(string marker = "new")
        {
            return new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["HDRCalibrate.exe"] = Encoding.UTF8.GetBytes("app " + marker),
                ["HDRCalibrate.exe.config"] = Encoding.UTF8.GetBytes("config " + marker),
                ["HDRCalibrate.Dwm.dll"] = Encoding.UTF8.GetBytes("dwm " + marker),
                ["README.md"] = Encoding.UTF8.GetBytes("readme " + marker),
                ["Native/" + Version + "/HDRCalibrate.SelectiveController.exe"] = Encoding.UTF8.GetBytes("controller " + marker),
                ["Native/" + Version + "/HDRCalibrate.SelectiveDwmHook.dll"] = Encoding.UTF8.GetBytes("hook " + Version),
                ["Native/" + Version + "/MinHook-LICENSE.txt"] = Encoding.UTF8.GetBytes("license")
            };
        }

        internal static string ManifestXml(IDictionary<string, byte[]> files, string version = Version)
        {
            var builder = new StringBuilder();
            using (var writer = XmlWriter.Create(builder, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                writer.WriteStartElement("SetupPayload");
                writer.WriteAttributeString("Version", version);
                foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    writer.WriteStartElement("File");
                    writer.WriteAttributeString("Path", file.Key);
                    writer.WriteAttributeString("Size", file.Value.Length.ToString());
                    writer.WriteAttributeString("Sha256", Sha256(file.Value));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }
            return builder.ToString();
        }

        internal static byte[] Zip(IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            using (var memory = new MemoryStream())
            {
                using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, true))
                {
                    foreach (var entry in entries)
                        using (var stream = archive.CreateEntry(entry.Key).Open())
                            stream.Write(entry.Value, 0, entry.Value.Length);
                }
                return memory.ToArray();
            }
        }

        internal static PayloadPackage Package(IDictionary<string, byte[]> manifestFiles, IEnumerable<KeyValuePair<string, byte[]>> zipEntries = null)
        {
            var manifest = PayloadManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(ManifestXml(manifestFiles))));
            var zip = Zip(zipEntries ?? manifestFiles);
            return new PayloadPackage(manifest, () => new MemoryStream(zip));
        }

        public static void ManifestRejectsUnsafeEntries()
        {
            var valid = PayloadManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(ManifestXml(PackageFiles()))));
            Check(valid.Version == Version && valid.Entries.Count == 7, "A valid manifest was not accepted.");
            Check(valid.Entries.Single(entry => entry.IsNativeHook).RelativePath == @"Native\" + Version + @"\HDRCalibrate.SelectiveDwmHook.dll",
                "Native paths were not mapped to Windows paths.");
            foreach (var badPath in new[]
            {
                "../HDRCalibrate.Setup.exe", "C:/Windows/evil.dll", "/rooted.exe", "sub/app.exe", "Native/hook.dll",
                "Native/9.9.9/hook.dll", "Native/" + Version + "/deeper/hook.dll", "app.exe.", "app.exe ", "app:stream.exe",
                "Native/../app.exe", ".hidden", "Native/./" + Version
            })
            {
                var files = PackageFiles();
                files[badPath] = new byte[] { 1 };
                ExpectInvalid(ManifestXml(files), "Unsafe package path accepted: " + badPath);
            }
            var noApp = PackageFiles();
            noApp.Remove("HDRCalibrate.exe");
            ExpectInvalid(ManifestXml(noApp), "A package without the application was accepted.");
            var xml = ManifestXml(PackageFiles());
            ExpectInvalid(xml.Replace("Version=\"" + Version + "\"", "Version=\"1.7\""), "Invalid package version accepted.");
            ExpectInvalid(xml.Replace("<SetupPayload ", "<SetupPayload Extra=\"1\" "), "Unknown root attribute accepted.");
            var hash = Sha256(PackageFiles()["HDRCalibrate.exe"]);
            ExpectInvalid(xml.Replace(hash, hash.ToLowerInvariant()), "Lower-case hash accepted.");
            ExpectInvalid(xml.Replace(hash, hash.Substring(1)), "Short hash accepted.");
            ExpectInvalid(xml.Replace("Size=\"" + PackageFiles()["HDRCalibrate.exe"].Length + "\" Sha256=\"" + hash,
                "Size=\"0\" Sha256=\"" + hash), "Empty file accepted.");
            var first = xml.IndexOf("<File ", StringComparison.Ordinal);
            var end = xml.IndexOf("/>", first, StringComparison.Ordinal) + 2;
            ExpectInvalid(xml.Insert(end, xml.Substring(first, end - first)), "Duplicate entry accepted.");
            Expect<XmlException>(() => PayloadManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/x'>]><SetupPayload Version=\"" + Version + "\">&e;</SetupPayload>"))),
                "A DTD was processed.");
        }

        public static void PackageExtractionVerifiesEveryFile()
        {
            using (var sandbox = new Sandbox())
            {
                var files = PackageFiles();
                var target = Path.Combine(sandbox.Root, "ok");
                Package(files).ExtractTo(target);
                foreach (var file in files)
                    Check(File.ReadAllBytes(Path.Combine(target, file.Key.Replace('/', '\\'))).SequenceEqual(file.Value),
                        "Extracted bytes differ: " + file.Key);
                Expect<IOException>(() => Package(files).ExtractTo(target), "Extraction into a non-empty directory was allowed.");

                var tampered = files.ToDictionary(item => item.Key, item => item.Value);
                tampered["README.md"] = Encoding.UTF8.GetBytes("readme xxx");
                Expect<InvalidDataException>(() => Package(files, tampered).ExtractTo(Path.Combine(sandbox.Root, "tampered")),
                    "A tampered file passed its manifest hash.");
                var extra = files.Concat(new[] { new KeyValuePair<string, byte[]>("evil.dll", new byte[] { 1 }) });
                Expect<InvalidDataException>(() => Package(files, extra).ExtractTo(Path.Combine(sandbox.Root, "extra")),
                    "An unlisted archive entry was extracted.");
                var missing = files.Where(item => item.Key != "README.md");
                Expect<InvalidDataException>(() => Package(files, missing).ExtractTo(Path.Combine(sandbox.Root, "missing")),
                    "An incomplete archive was accepted.");
                var duplicate = files.Concat(new[] { files.First() });
                Expect<InvalidDataException>(() => Package(files, duplicate).ExtractTo(Path.Combine(sandbox.Root, "duplicate")),
                    "A duplicate archive entry was accepted.");
                var traversal = files.Concat(new[] { new KeyValuePair<string, byte[]>("../outside.exe", new byte[] { 1 }) });
                Expect<InvalidDataException>(() => Package(files, traversal).ExtractTo(Path.Combine(sandbox.Root, "traversal")),
                    "A traversal archive entry was accepted.");
                Check(!File.Exists(Path.Combine(sandbox.Root, "outside.exe")), "An archive entry escaped the target directory.");
            }
        }

        public static void FreshInstallRegistersOnlyOwnState()
        {
            using (var sandbox = new Sandbox())
            {
                var files = PackageFiles();
                var before = InstallationLocator.Find(sandbox.Context);
                Check(!before.IsInstalled && before.Directory == sandbox.Install && !before.AutostartRegistered,
                    "A clean profile was reported as installed.");
                var result = new InstallEngine(sandbox.Context).Install(Package(files), sandbox.SetupExecutable,
                    new InstallOptions { Autostart = true, DesktopShortcut = false });
                Check(result.InstallDirectory == sandbox.Install && sandbox.Stopper.Calls.Count == 0,
                    "A fresh install used the wrong folder or tried to stop an app.");
                foreach (var file in files)
                    Check(File.ReadAllBytes(Path.Combine(sandbox.Install, file.Key.Replace('/', '\\'))).SequenceEqual(file.Value),
                        "Installed file differs: " + file.Key);
                Check(File.ReadAllText(Path.Combine(sandbox.Install, ProductInfo.SetupExeName)) == "setup " + Version,
                    "The uninstaller copy is missing.");
                var record = InstallationRecord.Read(sandbox.Install, null);
                Check(record != null && record.CreatedDirectory && record.Files.Contains("HDRCalibrate.exe")
                    && record.Files.Contains(ProductInfo.SetupExeName) && !record.Files.Any(SetupPaths.IsNative),
                    "The installation record is incomplete or lists native files.");

                var key = ProductInfo.UninstallKey;
                Check(sandbox.Registry.GetString(key, "DisplayName") == "HDR Calibrate"
                    && sandbox.Registry.GetString(key, "DisplayVersion") == Version
                    && sandbox.Registry.GetString(key, "InstallLocation") == sandbox.Install
                    && sandbox.Registry.GetString(key, "UninstallString") == "\"" + Path.Combine(sandbox.Install, ProductInfo.SetupExeName) + "\" --uninstall"
                    && sandbox.Registry.GetString(key, "InstallDate") == "20261010"
                    && (int)sandbox.Registry.Keys[key]["NoModify"] == 1 && (int)sandbox.Registry.Keys[key]["EstimatedSize"] > 0,
                    "The Apps & Features entry is wrong.");
                Check(sandbox.Registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName)
                    == "\"" + Path.Combine(sandbox.Install, "HDRCalibrate.exe") + "\" --tray --startup",
                    "The startup command is not exact.");
                Check(File.Exists(sandbox.Locations.StartMenuShortcut) && !File.Exists(sandbox.Locations.DesktopShortcut),
                    "Shortcut choices were not applied.");
                Check(!Directory.GetDirectories(sandbox.Install, ProductInfo.BackupDirectoryPrefix + "*").Any()
                    && !Directory.GetDirectories(sandbox.Locations.TempDirectory).Any(),
                    "Backup or staging folders were left behind.");
                Check(!sandbox.Registry.KeyExists(ProductInfo.InnoUninstallKey), "An Inno entry was created.");

                var after = InstallationLocator.Find(sandbox.Context);
                Check(after.IsInstalled && after.HasOwnEntry && after.Version == Version && after.AutostartRegistered
                    && !after.DesktopShortcutExists, "The installation is not detected for the next update.");
            }
        }

        public static void UpgradeTakesOverInnoInstallation()
        {
            using (var sandbox = new Sandbox())
            {
                var custom = Path.Combine(sandbox.Root, "Custom", "HDR Calibrate");
                var native = Path.Combine(custom, "Native", Version);
                var olderNative = Path.Combine(custom, "Native", "1.7.0");
                Directory.CreateDirectory(native);
                Directory.CreateDirectory(olderNative);
                File.WriteAllText(Path.Combine(custom, "HDRCalibrate.exe"), "app old");
                File.WriteAllText(Path.Combine(custom, "unins000.exe"), "inno");
                File.WriteAllText(Path.Combine(custom, "unins000.dat"), "inno");
                File.WriteAllText(Path.Combine(custom, "HDRCalibrate.SetupSupport.exe"), "support");
                File.WriteAllText(Path.Combine(custom, "notes.txt"), "user");
                File.WriteAllBytes(Path.Combine(native, ProductInfo.NativeHookFileName), PackageFiles()["Native/" + Version + "/HDRCalibrate.SelectiveDwmHook.dll"]);
                File.WriteAllText(Path.Combine(olderNative, ProductInfo.NativeHookFileName), "hook 1.7.0");
                File.WriteAllText(sandbox.Locations.DesktopShortcut, "inno shortcut");
                sandbox.Registry.SetString(ProductInfo.InnoUninstallKey, "InstallLocation", custom + "\\");
                sandbox.Registry.SetString(ProductInfo.InnoUninstallKey, "DisplayVersion", "1.6.1");
                sandbox.Registry.SetString(ProductInfo.RunKey, ProductInfo.RunValueName, "\"" + Path.Combine(custom, "HDRCalibrate.exe") + "\" --tray");

                var existing = InstallationLocator.Find(sandbox.Context);
                Check(existing.Directory == custom && existing.HasInnoEntry && existing.Version == "1.6.1"
                    && existing.AutostartRegistered && existing.DesktopShortcutExists && existing.AppExists,
                    "The Inno installation was not detected with its current choices.");

                // A same-version hook may be pinned in DWM: without write/delete sharing,
                // any attempt to rename or replace it fails the test.
                using (new FileStream(Path.Combine(native, ProductInfo.NativeHookFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    new InstallEngine(sandbox.Context).Install(Package(PackageFiles()), sandbox.SetupExecutable,
                        new InstallOptions { Autostart = true, DesktopShortcut = true });
                }
                Check(sandbox.Stopper.Calls.Count == 1
                    && sandbox.Stopper.Calls[0].Item2 == Path.Combine(custom, "HDRCalibrate.exe")
                    && sandbox.Stopper.Calls[0].Item1.StartsWith(sandbox.Locations.TempDirectory, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(sandbox.Stopper.Calls[0].Item1) == "HDRCalibrate.exe",
                    "The installed instance was not asked to exit by the new binary before replacement.");
                Check(File.ReadAllText(Path.Combine(custom, "HDRCalibrate.exe")) == "app new", "The application was not updated.");
                Check(!File.Exists(Path.Combine(custom, "unins000.exe")) && !File.Exists(Path.Combine(custom, "unins000.dat"))
                    && !File.Exists(Path.Combine(custom, "HDRCalibrate.SetupSupport.exe")), "Inno uninstaller files remain.");
                Check(File.ReadAllText(Path.Combine(custom, "notes.txt")) == "user", "A foreign file was touched.");
                Check(File.ReadAllText(Path.Combine(olderNative, ProductInfo.NativeHookFileName)) == "hook 1.7.0",
                    "An older native version folder was changed during an update.");
                Check(!sandbox.Registry.KeyExists(ProductInfo.InnoUninstallKey)
                    && sandbox.Registry.GetString(ProductInfo.UninstallKey, "InstallLocation") == custom,
                    "The Inno registration was not taken over.");
                Check(sandbox.Registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName)
                    == "\"" + Path.Combine(custom, "HDRCalibrate.exe") + "\" --tray --startup",
                    "The legacy startup command was not migrated.");
                Check(File.ReadAllText(sandbox.Locations.DesktopShortcut).StartsWith(Path.Combine(custom, "HDRCalibrate.exe"), StringComparison.Ordinal),
                    "The desktop shortcut was not refreshed.");
                var record = InstallationRecord.Read(custom, null);
                Check(record != null && !record.CreatedDirectory, "A pre-existing custom folder was claimed by Setup.");
            }
        }

        public static void RejectedUpdatesChangeNothing()
        {
            using (var sandbox = new Sandbox())
            {
                var native = Path.Combine(sandbox.Install, "Native", Version);
                Directory.CreateDirectory(native);
                File.WriteAllText(Path.Combine(sandbox.Install, "HDRCalibrate.exe"), "app old");
                File.WriteAllText(Path.Combine(native, ProductInfo.NativeHookFileName), "different hook");
                var mismatch = Expect<SetupException>(() => new InstallEngine(sandbox.Context).Install(
                    Package(PackageFiles()), sandbox.SetupExecutable, new InstallOptions()), "A different same-version hook was accepted.");
                Check(mismatch.Message == sandbox.Context.Text.NativeVersionMismatch && sandbox.Stopper.Calls.Count == 0,
                    "The native mismatch was not reported before stopping the app.");
                Check(File.ReadAllText(Path.Combine(sandbox.Install, "HDRCalibrate.exe")) == "app old"
                    && sandbox.Registry.Keys.Count == 0, "A rejected update changed files or registry.");

                File.WriteAllBytes(Path.Combine(native, ProductInfo.NativeHookFileName), PackageFiles()["Native/" + Version + "/HDRCalibrate.SelectiveDwmHook.dll"]);
                sandbox.Stopper.Result = ProcessAppStopper.AppExitTimeout;
                var timeout = Expect<SetupException>(() => new InstallEngine(sandbox.Context).Install(
                    Package(PackageFiles()), sandbox.SetupExecutable, new InstallOptions()), "A running app was replaced.");
                Check(timeout.Message == sandbox.Context.Text.StopTimedOut && sandbox.Stopper.Calls.Count == 1,
                    "A stop timeout was not reported.");
                Check(File.ReadAllText(Path.Combine(sandbox.Install, "HDRCalibrate.exe")) == "app old"
                    && sandbox.Registry.Keys.Count == 0 && !Directory.GetDirectories(sandbox.Locations.TempDirectory).Any(),
                    "A failed stop changed files, registry or left staging data.");
            }
        }

        public static void FailedCopyRestoresPreviousFiles()
        {
            using (var sandbox = new Sandbox())
            {
                Directory.CreateDirectory(sandbox.Install);
                var old = new Dictionary<string, string>
                {
                    ["HDRCalibrate.exe"] = "app old",
                    ["HDRCalibrate.Dwm.dll"] = "dwm old",
                    ["README.md"] = "readme old"
                };
                foreach (var file in old) File.WriteAllText(Path.Combine(sandbox.Install, file.Key), file.Value);
                SetupException failure;
                // Without delete sharing the README cannot be renamed to the backup.
                using (new FileStream(Path.Combine(sandbox.Install, "README.md"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    failure = Expect<SetupException>(() => new InstallEngine(sandbox.Context).Install(
                        Package(PackageFiles()), sandbox.SetupExecutable, new InstallOptions { Autostart = true }),
                        "A partially failed copy was reported as success.");
                }
                Check(failure.ChangesRolledBack, "The failure did not report a complete rollback.");
                foreach (var file in old)
                    Check(File.ReadAllText(Path.Combine(sandbox.Install, file.Key)) == file.Value, "Rollback lost " + file.Key);
                Check(!File.Exists(Path.Combine(sandbox.Install, "HDRCalibrate.exe.config"))
                    && !File.Exists(Path.Combine(sandbox.Install, ProductInfo.RecordFileName))
                    && !Directory.Exists(Path.Combine(sandbox.Install, "Native"))
                    && !Directory.GetDirectories(sandbox.Install, ProductInfo.BackupDirectoryPrefix + "*").Any(),
                    "Rollback left new files, folders or backups behind.");
                Check(sandbox.Registry.Keys.Count == 0, "A failed copy registered the installation or startup.");
            }
        }

        public static void UninstallRemovesOnlyOwnedState()
        {
            using (var sandbox = new Sandbox())
            {
                var package = Package(PackageFiles());
                new InstallEngine(sandbox.Context).Install(package, sandbox.SetupExecutable,
                    new InstallOptions { Autostart = true, DesktopShortcut = true });
                File.WriteAllText(Path.Combine(sandbox.Install, "notes.txt"), "user");
                var result = new UninstallEngine(sandbox.Context).Uninstall(package.Manifest);
                var app = Path.Combine(sandbox.Install, "HDRCalibrate.exe");
                Check(sandbox.Stopper.Calls.Count == 1 && sandbox.Stopper.Calls[0].Item1 == app && sandbox.Stopper.Calls[0].Item2 == app,
                    "Uninstall did not ask the installed instance to exit.");
                Check(Directory.GetFiles(sandbox.Install).Select(Path.GetFileName).SequenceEqual(new[] { "notes.txt" })
                    && !Directory.Exists(Path.Combine(sandbox.Install, "Native")),
                    "Uninstall left owned files or removed a foreign file.");
                Check(result.RemainingDirectory == sandbox.Install && result.RetainedNativeDirectories.Count == 0,
                    "The folder with a foreign file was not reported as kept.");
                Check(sandbox.Registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName) == null
                    && !sandbox.Registry.KeyExists(ProductInfo.UninstallKey), "Own registry state remains.");
                Check(!File.Exists(sandbox.Locations.StartMenuShortcut) && !Directory.Exists(sandbox.Locations.StartMenuDirectory)
                    && !File.Exists(sandbox.Locations.DesktopShortcut), "Shortcuts remain.");
                Expect<SetupException>(() => new UninstallEngine(sandbox.Context).Uninstall(package.Manifest),
                    "A second uninstall did not report the missing installation.");
            }

            using (var sandbox = new Sandbox())
            {
                var package = Package(PackageFiles());
                new InstallEngine(sandbox.Context).Install(package, sandbox.SetupExecutable, new InstallOptions());
                const string foreignCommand = "\"C:\\Other\\HDRCalibrate.exe\" --tray --startup";
                sandbox.Registry.SetString(ProductInfo.RunKey, ProductInfo.RunValueName, foreignCommand);
                var native = Path.Combine(sandbox.Install, "Native", Version);
                UninstallResult result;
                // A hook pinned by DWM cannot be deleted: its folder stays, nothing is deferred.
                using (new FileStream(Path.Combine(native, ProductInfo.NativeHookFileName), FileMode.Open, FileAccess.Read, FileShare.Read))
                    result = new UninstallEngine(sandbox.Context).Uninstall(package.Manifest);
                Check(sandbox.Registry.GetString(ProductInfo.RunKey, ProductInfo.RunValueName) == foreignCommand,
                    "A startup command of another installation was removed.");
                Check(result.RetainedNativeDirectories.SequenceEqual(new[] { native })
                    && File.Exists(Path.Combine(native, ProductInfo.NativeHookFileName))
                    && File.Exists(Path.Combine(native, ProductInfo.NativeLicenseFileName)),
                    "A locked native hook was not retained together with its license.");
                Check(result.RemainingDirectory == sandbox.Install && !File.Exists(Path.Combine(sandbox.Install, "HDRCalibrate.exe")),
                    "Uninstall did not remove the application next to a retained native folder.");
                Check(NativeFiles.RemoveAll(sandbox.Install, new NullSetupLog()).Count == 0
                    && !Directory.Exists(Path.Combine(sandbox.Install, "Native")), "The released native folder was not removable.");
            }

            using (var sandbox = new Sandbox())
            {
                var package = Package(PackageFiles());
                new InstallEngine(sandbox.Context).Install(package, sandbox.SetupExecutable, new InstallOptions());
                var result = new UninstallEngine(sandbox.Context).Uninstall(package.Manifest);
                Check(result.RemainingDirectory == null && !Directory.Exists(sandbox.Install),
                    "The setup-created folder was not removed.");
            }
        }

        public static void ArgumentsAndStopCodesAreExact()
        {
            Check(SetupCommandLine.TryParse(new string[0], out var mode) && mode == SetupMode.Install, "No arguments must install.");
            Check(SetupCommandLine.TryParse(new[] { "--UNINSTALL" }, out mode) && mode == SetupMode.Uninstall, "--uninstall was not parsed.");
            foreach (var invalid in new[]
            {
                new[] { "--uninstall", "--quiet" }, new[] { "/S" }, new[] { "--uninstall=1" }, new[] { "uninstall" }, new[] { "" }
            })
                Check(!SetupCommandLine.TryParse(invalid, out _), "Unexpected setup arguments accepted: " + string.Join(" ", invalid));

            var text = new SetupText(false);
            Check(InstallationLocator.StopMessage(text, ProcessAppStopper.AppExitTimeout) == text.StopTimedOut
                && InstallationLocator.StopMessage(text, ProcessAppStopper.ErrorTimeout) == text.StopTimedOut
                && InstallationLocator.StopMessage(text, ProcessAppStopper.ErrorBusy) == text.StopBusy
                && InstallationLocator.StopMessage(text, ProcessAppStopper.ErrorInvalidParameter) == text.StopStartFailed
                && InstallationLocator.StopMessage(text, 23) == text.StopFailed(23), "Stop codes map to the wrong messages.");
            Check(new SetupText(true).InstallButton == "Installieren" && text.InstallButton == "Install", "Localization is wrong.");
            Check(InstallationLocator.Normalize(@"\\server\share\HDR") == null && InstallationLocator.Normalize("relative") == null
                && InstallationLocator.Normalize(@"C:\Apps\HDR Calibrate\") == @"C:\Apps\HDR Calibrate",
                "Recorded installation folders are not validated.");
        }

        private static void ExpectInvalid(string xml, string message) =>
            Expect<InvalidDataException>(() => PayloadManifest.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))), message);

        private static T Expect<T>(Action action, string message) where T : Exception
        {
            try { action(); }
            catch (T expected) { return expected; }
            throw new InvalidOperationException(message);
        }

        private static string Sha256(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
