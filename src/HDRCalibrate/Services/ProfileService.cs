using HDRCalibrate.Core;
using HDRCalibrate.Native;
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HDRCalibrate.Services
{
    public sealed class ProfileApplyResult
    {
        public string ProfileName { get; set; }
        public string ProfilePath { get; set; }
        public bool UsedExistingBaseProfile { get; set; }
    }

    public sealed class ProfileService
    {
        private const string ProfilePrefix = "HDRCalibrate_";
        private readonly ProfileStateStore _stateStore;

        public ProfileService(ProfileStateStore stateStore = null)
        {
            _stateStore = stateStore ?? new ProfileStateStore();
        }

        public PersistedDisplayState GetState(DisplayInfo display)
        {
            return display == null ? null : _stateStore.Get(display.Key);
        }

        public ProfileApplyResult Apply(DisplayInfo display, CalibrationSettings settings)
        {
            if (display == null) throw new ArgumentNullException(nameof(display));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            EnsureDisplayCanApply(display);

            var state = _stateStore.Get(display.Key);
            if (state == null && IsManagedProfile(display.ActiveProfileName))
            {
                throw new InvalidOperationException(
                    "Das aktive Profil stammt von HDR Calibrate, aber der Originalprofil-Merker fehlt. " +
                    "Aus Sicherheitsgründen wird kein weiteres Profil darübergelegt.");
            }

            if (state == null)
            {
                state = new PersistedDisplayState
                {
                    DisplayKey = display.Key,
                    OriginalProfileName = display.ActiveProfileName,
                    OriginalProfileScope = (int)display.ProfileScope,
                    BaseMetadata = display.ColorMetadata?.Clone() ?? new DisplayColorMetadata()
                };
            }
            else if (!string.IsNullOrWhiteSpace(display.ActiveProfileName)
                     && !IsManagedProfile(display.ActiveProfileName)
                     && !string.Equals(display.ActiveProfileName, state.OriginalProfileName, StringComparison.OrdinalIgnoreCase))
            {
                // The user intentionally selected another HDR profile outside this tool.
                state.OriginalProfileName = display.ActiveProfileName;
                state.OriginalProfileScope = (int)display.ProfileScope;
                state.BaseMetadata = display.ColorMetadata?.Clone() ?? state.BaseMetadata;
            }

            // Persist the rollback target before changing the active display association.
            _stateStore.Upsert(state);

            var basePath = ResolveInstalledProfilePath(state.OriginalProfileName);
            var hasBaseProfile = !string.IsNullOrWhiteSpace(basePath) && File.Exists(basePath);
            var description = BuildDescription(display, settings);
            var lutEntries = ChooseLutEntryCount(display.Mhc2LutEntries);
            var metadata = state.BaseMetadata != null && state.BaseMetadata.IsUsable()
                ? state.BaseMetadata
                : (display.ColorMetadata?.Clone() ?? new DisplayColorMetadata());

            byte[] profileBytes;
            if (hasBaseProfile)
            {
                profileBytes = IccProfileFactory.CreateAdjustedProfile(
                    File.ReadAllBytes(basePath),
                    description,
                    settings,
                    lutEntries,
                    display.SdrWhiteNits,
                    metadata);
            }
            else
            {
                profileBytes = IccProfileFactory.CreateFallbackProfile(
                    description,
                    metadata,
                    settings,
                    lutEntries,
                    display.SdrWhiteNits);
            }

            Directory.CreateDirectory(_stateStore.ProfilesDirectory);
            var digest = ComputeDigest(profileBytes).Substring(0, 12);
            var safeDisplayKey = display.AdapterId + "_" + display.SourceId.ToString(CultureInfo.InvariantCulture);
            var profileName = ProfilePrefix + safeDisplayKey + "_" + digest + ".icm";
            var localPath = Path.Combine(_stateStore.ProfilesDirectory, profileName);
            WriteFileAtomically(localPath, profileBytes);

            if (!DisplayService.ValidateProfileFile(localPath, out var validationError))
                throw new InvalidDataException("Windows hat das erzeugte Profil abgelehnt: " + validationError);

            var installedByThisCall = InstallProfile(localPath, profileName, profileBytes);
            try
            {
                var hr = NativeMethods.ColorProfileAddDisplayAssociation(
                    WcsProfileManagementScope.CurrentUser,
                    profileName,
                    display.AdapterId,
                    display.SourceId,
                    true,
                    true);
                if (hr < 0)
                {
                    // It may already be associated. A direct default switch is safe to try.
                    hr = NativeMethods.ColorProfileSetDisplayDefaultAssociation(
                        WcsProfileManagementScope.CurrentUser,
                        profileName,
                        ColorProfileType.Icc,
                        ColorProfileSubType.ExtendedDisplayColorMode,
                        display.AdapterId,
                        display.SourceId);
                }
                ThrowIfFailed(hr, "Das neue HDR-Profil konnte nicht als Standard gesetzt werden.");

                var verified = ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser);
                if (!string.Equals(verified, profileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Windows meldet nach dem Anwenden nicht das erwartete HDR-Profil.");
                }
            }
            catch (Exception applyError)
            {
                Exception rollbackError = null;
                try
                {
                    var activeAfterFailure = ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser);
                    if (string.Equals(activeAfterFailure, profileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrWhiteSpace(state.OriginalProfileName))
                        {
                            RestoreProfile(display, state.OriginalProfileName);
                        }
                        else
                        {
                            var removeResult = NativeMethods.ColorProfileRemoveDisplayAssociation(
                                WcsProfileManagementScope.CurrentUser,
                                profileName,
                                display.AdapterId,
                                display.SourceId,
                                true);
                            ThrowIfFailed(removeResult, "Das fehlgeschlagene Profil konnte nicht zurückgenommen werden.");
                        }
                    }
                }
                catch (Exception ex)
                {
                    rollbackError = ex;
                }

                var stillActive = string.Equals(
                    ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser),
                    profileName,
                    StringComparison.OrdinalIgnoreCase);
                if (!stillActive)
                {
                    if (installedByThisCall) TryUninstall(profileName);
                    TryDeleteLocalProfile(profileName);
                }

                if (rollbackError != null)
                {
                    throw new AggregateException(
                        "Das Anwenden schlug fehl und Windows konnte den vorherigen Profilzustand nicht vollständig wiederherstellen.",
                        applyError,
                        rollbackError);
                }
                throw;
            }

            var oldManagedProfile = state.LastAppliedProfileName;
            state.LastAppliedProfileName = profileName;
            state.Settings = settings.Clone();
            _stateStore.Upsert(state);

            if (!string.IsNullOrWhiteSpace(oldManagedProfile)
                && !string.Equals(oldManagedProfile, profileName, StringComparison.OrdinalIgnoreCase)
                && IsManagedProfile(oldManagedProfile))
            {
                RemoveManagedAssociation(display, oldManagedProfile);
            }

            return new ProfileApplyResult
            {
                ProfileName = profileName,
                ProfilePath = Path.Combine(DisplayService.GetColorDirectory(), profileName),
                UsedExistingBaseProfile = hasBaseProfile
            };
        }

        public void Reset(DisplayInfo display)
        {
            if (display == null) throw new ArgumentNullException(nameof(display));
            var state = _stateStore.Get(display.Key);
            if (state == null)
                throw new InvalidOperationException("Für diesen Monitor ist kein von HDR Calibrate gespeicherter Wiederherstellungspunkt vorhanden.");

            if (!string.IsNullOrWhiteSpace(state.OriginalProfileName))
            {
                RestoreProfile(display, state.OriginalProfileName);
                var verified = ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser);
                if (!string.Equals(verified, state.OriginalProfileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows hat das ursprüngliche HDR-Profil nicht als Standard bestätigt.");
            }
            else if (!string.IsNullOrWhiteSpace(state.LastAppliedProfileName))
            {
                var wasManagedProfileDefault = string.Equals(
                    display.ActiveProfileName,
                    state.LastAppliedProfileName,
                    StringComparison.OrdinalIgnoreCase);
                var hr = NativeMethods.ColorProfileRemoveDisplayAssociation(
                    WcsProfileManagementScope.CurrentUser,
                    state.LastAppliedProfileName,
                    display.AdapterId,
                    display.SourceId,
                    true);
                var remaining = ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser);
                if (string.Equals(remaining, state.LastAppliedProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (hr < 0)
                        ThrowIfFailed(hr, "Das von HDR Calibrate erzeugte Profil konnte nicht entfernt werden.");
                    throw new InvalidOperationException("Windows führt das zu entfernende HDR-Profil weiterhin als Standard.");
                }

                // Windows returns an error when the persisted v1.0 association is already gone.
                // That is a successful idempotent rollback if display detection also reported a
                // different/default-less profile. Only fail when the pre-removal state said that
                // the managed profile was active and Windows did not give us a verifiable result.
                if (hr < 0 && wasManagedProfileDefault && remaining == null)
                    ThrowIfFailed(hr, "Das aktive, von HDR Calibrate erzeugte Profil konnte nicht sicher entfernt werden.");
            }

            if (IsManagedProfile(state.LastAppliedProfileName))
                RemoveManagedAssociation(display, state.LastAppliedProfileName);
            _stateStore.Remove(display.Key);
        }

        private static void EnsureDisplayCanApply(DisplayInfo display)
        {
            if (!display.HdrActive)
                throw new InvalidOperationException("HDR ist auf dem ausgewählten Monitor nicht aktiv.");
            if (display.AdvancedColorLimitedByPolicy)
                throw new InvalidOperationException("Windows begrenzt Advanced Color derzeit durch eine Richtlinie.");
            if (!display.Mhc2Supported)
                throw new NotSupportedException("Der Treiber meldet keine Unterstützung für die Windows-MHC2-Farbpipeline.");
        }

        private static int ChooseLutEntryCount(uint hardwareMaximum)
        {
            if (hardwareMaximum < 2) return 1024;
            return checked((int)Math.Min(4096u, Math.Max(256u, hardwareMaximum)));
        }

        private static string BuildDescription(DisplayInfo display, CalibrationSettings settings)
        {
            var gammaLabel = settings.ApplySdrGammaCorrection
                ? settings.Gamma.ToString("0.00", CultureInfo.InvariantCulture)
                : "sRGB";
            return string.Format(
                CultureInfo.InvariantCulture,
                "HDR Calibrate - {0} - G {1} B {2:+0;-0;0}% C {3:0.00} S {4:0}%",
                display.FriendlyName,
                gammaLabel,
                settings.Brightness * 100.0,
                settings.Contrast,
                settings.Saturation * 100.0);
        }

        private static string ResolveInstalledProfilePath(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return null;
            return Path.IsPathRooted(profileName)
                ? profileName
                : Path.Combine(DisplayService.GetColorDirectory(), profileName);
        }

        private static bool InstallProfile(string localPath, string profileName, byte[] expectedBytes)
        {
            if (NativeMethods.InstallColorProfile(null, localPath)) return true;
            var error = Marshal.GetLastWin32Error();
            var installedPath = Path.Combine(DisplayService.GetColorDirectory(), profileName);
            if ((error == 80 || error == 183) && File.Exists(installedPath)
                && string.Equals(ComputeDigest(File.ReadAllBytes(installedPath)), ComputeDigest(expectedBytes), StringComparison.Ordinal))
                return false;
            throw new Win32Exception(error, "Das erzeugte ICC-Profil konnte nicht im Windows-Farbprofilordner installiert werden.");
        }

        private static void RestoreProfile(DisplayInfo display, string originalProfileName)
        {
            if (string.IsNullOrWhiteSpace(originalProfileName)) return;
            var hr = NativeMethods.ColorProfileAddDisplayAssociation(
                WcsProfileManagementScope.CurrentUser,
                originalProfileName,
                display.AdapterId,
                display.SourceId,
                true,
                true);
            if (hr < 0)
            {
                hr = NativeMethods.ColorProfileSetDisplayDefaultAssociation(
                    WcsProfileManagementScope.CurrentUser,
                    originalProfileName,
                    ColorProfileType.Icc,
                    ColorProfileSubType.ExtendedDisplayColorMode,
                    display.AdapterId,
                    display.SourceId);
            }
            ThrowIfFailed(hr, "Das ursprüngliche HDR-Profil konnte nicht wiederhergestellt werden.");
        }

        private static string ReadDefaultProfile(DisplayInfo display, WcsProfileManagementScope scope)
        {
            var hr = NativeMethods.ColorProfileGetDisplayDefault(
                scope,
                display.AdapterId,
                display.SourceId,
                ColorProfileType.Icc,
                ColorProfileSubType.ExtendedDisplayColorMode,
                out var pointer);
            if (hr < 0 || pointer == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                NativeMethods.LocalFree(pointer);
            }
        }

        private void RemoveManagedAssociation(DisplayInfo display, string profileName)
        {
            if (!IsManagedProfile(profileName)) return;
            NativeMethods.ColorProfileRemoveDisplayAssociation(
                WcsProfileManagementScope.CurrentUser,
                profileName,
                display.AdapterId,
                display.SourceId,
                true);
            TryUninstall(profileName);
            TryDeleteLocalProfile(profileName);
        }

        private void TryDeleteLocalProfile(string profileName)
        {
            if (!IsManagedProfile(profileName)) return;
            try
            {
                var fileName = Path.GetFileName(profileName);
                var profilesRoot = Path.GetFullPath(_stateStore.ProfilesDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var candidate = Path.GetFullPath(Path.Combine(profilesRoot, fileName));
                if (candidate.StartsWith(profilesRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch
            {
                // A stale local copy is harmless; never turn successful profile rollback into a failure.
            }
        }

        private static void TryUninstall(string profileName)
        {
            if (!IsManagedProfile(profileName)) return;
            NativeMethods.UninstallColorProfile(null, profileName, true);
        }

        private static bool IsManagedProfile(string profileName)
        {
            if (string.IsNullOrWhiteSpace(profileName)) return false;
            return Path.GetFileName(profileName).StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase);
        }

        private static void WriteFileAtomically(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }

        private static string ComputeDigest(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty);
            }
        }

        private static void ThrowIfFailed(int hresult, string message)
        {
            if (hresult < 0) throw new COMException(message, hresult);
        }
    }
}
