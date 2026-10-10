using HDRCalibrate.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Tests
{
    internal static class ProgramProfileTests
    {
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Reject(Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is IOException || ex is InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid operation was accepted.");
        }

        private static ProgramLaunchProfile Profile(ProgramColorMode mode = ProgramColorMode.Srgb) => new ProgramLaunchProfile
        {
            ExecutablePath = @"C:\Program Files\Example Browser\brave.exe",
            ColorMode = mode
        };

        public static void ArgumentsAndPaths()
        {
            Check(ProgramProfileRules.Arguments(ProgramColorMode.Srgb) == "--force-color-profile=srgb", "Wrong SDR argument.");
            Check(ProgramProfileRules.Arguments(ProgramColorMode.ScrgbLinear) == "--force-color-profile=scrgb-linear", "Wrong scRGB argument.");
            Check(ProgramProfileRules.Arguments(ProgramColorMode.Default) == string.Empty, "Default must not force a color profile.");
            Reject(() => ProgramProfileRules.Arguments((ProgramColorMode)999));
            foreach (var path in new[] { "brave.exe", @"C:brave.exe", @"\\server\brave.exe", "https://example.org/brave.exe", @"C:\a.cmd", @"C:\a.exe:stream.exe", "C:\\a\".exe", "C:\\a\n.exe" })
                Reject(() => ProgramProfileRules.NormalizePath(path));
            var normalized = ProgramProfileRules.NormalizePath(@"C:\Apps\one\..\brave.exe");
            Check(normalized == @"C:\Apps\brave.exe", "Path was not normalized.");
            var copy = Profile();
            Check(!copy.ForceSdrGamma, "Gamma forcing must default to disabled.");
            copy.ForceSdrGamma = true;
            var explicitCopy = copy.Copy();
            Check(explicitCopy.ForceSdrGamma && explicitCopy.ColorMode == copy.ColorMode
                && explicitCopy.ExecutablePath == copy.ExecutablePath, "Copy lost the independent gamma preference.");
            explicitCopy.ForceSdrGamma = false;
            Check(copy.ForceSdrGamma, "Copy shares mutable gamma preference state.");
            var validated = ProgramProfileRules.Validate(new[] { copy });
            Check(validated[0].ForceSdrGamma, "Validation dropped the gamma preference.");
            validated[0].ForceSdrGamma = false;
            validated[0].ColorMode = ProgramColorMode.Default;
            Check(copy.ColorMode == ProgramColorMode.Srgb && copy.ForceSdrGamma,
                "Validation did not snapshot the caller's profile.");
            var arbitraryApp = new ProgramLaunchProfile
            {
                ExecutablePath = @"C:\Games\Any Application\application.exe",
                ForceSdrGamma = true
            };
            var arbitraryValidated = ProgramProfileRules.Validate(new[] { arbitraryApp }).Single();
            Check(arbitraryValidated.ColorMode == ProgramColorMode.Default && arbitraryValidated.ForceSdrGamma,
                "A non-browser gamma profile must retain the default color mode.");
        }

        public static void BrowserRecognition()
        {
            Check(ProgramExecutableInspector.Recognize("brave.exe", "Brave Browser", "brave.exe") == "Brave", "Brave not recognized.");
            Check(ProgramExecutableInspector.Recognize("chrome.exe", "Google Chrome", "chrome.exe") == "Google Chrome", "Chrome not recognized.");
            Check(ProgramExecutableInspector.Recognize("msedge.exe", "Microsoft Edge", "msedge.exe") == "Microsoft Edge", "Edge not recognized.");
            Check(ProgramExecutableInspector.Recognize("chromium.exe", "Chromium", "chrome.exe") == "Chromium", "Chromium not recognized.");
            Check(ProgramExecutableInspector.Recognize("brave.exe", "Some Other Product", "other.exe") == null, "Filename alone must not grant compatibility.");
            Check(ProgramExecutableInspector.Recognize("firefox.exe", "Firefox", "firefox.exe") == null, "Unsupported app was accepted.");
            Check(!ProgramExecutableInspector.Inspect(@"C:\HDRCalibrate-Missing-" + Guid.NewGuid().ToString("N") + @"\brave.exe").IsSupported, "Missing file was accepted.");
        }

        public static void Persistence()
        {
            var directory = Path.Combine(Path.GetDirectoryName(typeof(ProgramProfileTests).Assembly.Location),
                "program-profile-tests", Guid.NewGuid().ToString("N"));
            var store = new ProgramProfileStore(directory);
            Check(store.Load().Count == 0 && !Directory.Exists(directory), "Reading absent profiles created files.");
            store.Save(new[] { Profile() });
            Check(store.Load().Single().ColorMode == ProgramColorMode.Srgb && !store.Load().Single().ForceSdrGamma,
                "SDR mode or the safe default gamma preference was not persisted.");
            var gammaProfile = Profile();
            gammaProfile.ForceSdrGamma = true;
            store.Save(new[] { gammaProfile });
            Check(new ProgramProfileStore(directory).Load().Single().ForceSdrGamma,
                "Explicit gamma preference did not survive store recreation.");
            store.Save(new[] { Profile(ProgramColorMode.ScrgbLinear) });
            Check(new ProgramProfileStore(directory).Load().Single().ColorMode == ProgramColorMode.ScrgbLinear, "Mode did not survive store recreation.");
            Check(!store.Load().Single().ForceSdrGamma, "Disabling gamma was not persisted.");
            Check(File.Exists(Path.Combine(directory, "program-profiles.xml.bak")), "Atomic save did not retain a backup.");
            var current = File.ReadAllBytes(Path.Combine(directory, "program-profiles.xml"));
            var duplicate = Profile();
            duplicate.ExecutablePath = duplicate.ExecutablePath.ToUpperInvariant();
            Reject(() => store.Save(new[] { Profile(), duplicate }));
            Check(current.SequenceEqual(File.ReadAllBytes(Path.Combine(directory, "program-profiles.xml"))), "Rejected save changed the stored list.");
            Reject(() => store.Save(new[] { Profile((ProgramColorMode)77) }));
            Check(!Directory.GetFiles(directory, "*.tmp").Any(), "Temporary write residue remains.");
            store.Save(new ProgramLaunchProfile[0]);
            Check(store.Load().Count == 0, "Removing all profiles did not persist.");

            var path = Path.Combine(directory, "program-profiles.xml");
            var legacyXml = "<ProgramProfilesDocument><SchemaVersion>1</SchemaVersion><Profiles>"
                + "<ProgramLaunchProfile><ExecutablePath>C:\\Apps\\brave.exe</ExecutablePath>"
                + "<ColorMode>Srgb</ColorMode></ProgramLaunchProfile></Profiles></ProgramProfilesDocument>";
            File.WriteAllText(path, legacyXml);
            var legacy = store.Load().Single();
            Check(legacy.ColorMode == ProgramColorMode.Srgb && !legacy.ForceSdrGamma,
                "Legacy profile loading must preserve the browser mode and default gamma to disabled.");
            Check(File.ReadAllText(path) == legacyXml, "Loading a legacy profile unexpectedly rewrote the file.");
        }

        public static void CorruptStore()
        {
            var directory = Path.Combine(Path.GetDirectoryName(typeof(ProgramProfileTests).Assembly.Location),
                "program-profile-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "program-profiles.xml");
            var store = new ProgramProfileStore(directory);
            foreach (var xml in new[]
            {
                "<broken>",
                "<ProgramProfilesDocument><SchemaVersion>2</SchemaVersion><Profiles /></ProgramProfilesDocument>",
                "<ProgramProfilesDocument><SchemaVersion>1</SchemaVersion><Profiles><ProgramLaunchProfile>"
                    + "<ExecutablePath>C:\\Apps\\application.exe</ExecutablePath><ColorMode>Default</ColorMode>"
                    + "<ForceSdrGamma>not-a-boolean</ForceSdrGamma></ProgramLaunchProfile></Profiles></ProgramProfilesDocument>",
                "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/not-read'>]><ProgramProfilesDocument>&e;</ProgramProfilesDocument>"
            })
            {
                File.WriteAllText(path, xml);
                Reject(() => store.Load());
                Check(File.ReadAllText(path) == xml, "Invalid configuration was rewritten by Load.");
            }
        }

        public static void LaunchPlans()
        {
            ProcessStartInfo started = null;
            var launcher = new ProgramProfileLauncher(_ => new ProgramExecutableInfo { IsSupported = true },
                _ => false, info => started = info, () => false);
            foreach (var mode in new[] { ProgramColorMode.Default, ProgramColorMode.Srgb, ProgramColorMode.ScrgbLinear })
            {
                foreach (var forceGamma in new[] { false, true })
                {
                    var profile = Profile(mode);
                    profile.ForceSdrGamma = forceGamma;
                    launcher.Launch(profile);
                    Check(started.FileName == Profile().ExecutablePath, "The executable path was changed or shell-quoted.");
                    Check(started.Arguments == ProgramProfileRules.Arguments(mode),
                        "Gamma forcing must not add or replace a browser launch argument.");
                    Check(!started.UseShellExecute && started.WorkingDirectory == @"C:\Program Files\Example Browser", "Launch must not use a command shell.");
                }
            }
        }

        public static void LaunchGuards()
        {
            var launchCount = 0;
            Action<ProcessStartInfo> launch = _ => launchCount++;
            Func<string, ProgramExecutableInfo> supported = _ => new ProgramExecutableInfo { IsSupported = true };
            foreach (var elevated in new[] { false, true })
            {
                Reject(() => new ProgramProfileLauncher(supported, _ => true, launch, () => elevated, launch).Launch(Profile()));
                Reject(() => new ProgramProfileLauncher(_ => new ProgramExecutableInfo(), _ => false, launch, () => elevated, launch).Launch(Profile()));
                Reject(() => new ProgramProfileLauncher(supported, _ => false, launch, () => elevated, launch).Launch(Profile((ProgramColorMode)999)));
                var gammaProfile = Profile(ProgramColorMode.Default);
                gammaProfile.ExecutablePath = @"C:\Games\Any Application\application.exe";
                gammaProfile.ForceSdrGamma = true;
                Reject(() => new ProgramProfileLauncher(_ => new ProgramExecutableInfo(), _ => false, launch,
                    () => elevated, launch).Launch(gammaProfile));
            }
            Check(launchCount == 0, "A refused operation reached Process.Start.");
        }

        public static void ElevatedLaunchRouting()
        {
            var directStarts = 0;
            var explorerStarts = 0;
            ProcessStartInfo delegated = null;
            var launcher = new ProgramProfileLauncher(_ => new ProgramExecutableInfo { IsSupported = true },
                _ => false, _ => directStarts++, () => true, info => { explorerStarts++; delegated = info; });
            foreach (var mode in new[] { ProgramColorMode.Default, ProgramColorMode.Srgb, ProgramColorMode.ScrgbLinear })
            {
                launcher.Launch(Profile(mode));
                Check(delegated.FileName == Profile().ExecutablePath, "Elevated routing changed the EXE path.");
                Check(delegated.Arguments == ProgramProfileRules.Arguments(mode), "Elevated routing changed the arguments.");
            }
            Check(directStarts == 0 && explorerStarts == 3, "Elevated launch did not use Explorer exclusively.");
        }

        public static void ExplorerFailureDoesNotRetry()
        {
            var directStarts = 0;
            var explorerStarts = 0;
            var launcher = new ProgramProfileLauncher(_ => new ProgramExecutableInfo { IsSupported = true },
                _ => false, _ => directStarts++, () => true, _ =>
                {
                    explorerStarts++;
                    throw new InvalidOperationException("Simulated Explorer failure.");
                });
            Reject(() => launcher.Launch(Profile()));
            Check(explorerStarts == 1 && directStarts == 0, "Failed delegation must not risk launching a second instance.");
        }
    }
}
