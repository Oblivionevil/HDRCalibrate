using HDRCalibrate.Native;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace HDRCalibrate.Services
{
    /// <summary>
    /// Removes the MHC2 profile associations HDR Calibrate 1.0 created. Nothing
    /// here creates or applies a profile any more.
    /// </summary>
    public sealed class ProfileService
    {
        private const string ProfilePrefix = "HDRCalibrate_";
        private readonly ProfileStateStore _stateStore;

        public ProfileService(ProfileStateStore stateStore = null)
        {
            _stateStore = stateStore ?? new ProfileStateStore();
        }

        /// <summary>
        /// The restore point for this display. Its key contains the adapter LUID,
        /// which Windows assigns anew at every start, so after a reboot the point
        /// is found by the display's active own profile instead.
        /// </summary>
        public PersistedDisplayState GetState(DisplayInfo display)
        {
            if (display == null) return null;
            var states = _stateStore.Load().Displays;
            return states.FirstOrDefault(item => string.Equals(item.DisplayKey, display.Key, StringComparison.Ordinal))
                ?? (IsManagedProfile(display.ActiveProfileName)
                    ? states.FirstOrDefault(item => IsManagedProfile(item.LastAppliedProfileName)
                        && string.Equals(Path.GetFileName(item.LastAppliedProfileName),
                            Path.GetFileName(display.ActiveProfileName), StringComparison.OrdinalIgnoreCase))
                    : null);
        }

        /// <summary>
        /// True when there is something to clean up: a restore point, or an own
        /// profile that is still active without one (reported, never guessed).
        /// </summary>
        public bool HasLegacyProfile(DisplayInfo display)
        {
            return display != null && (IsManagedProfile(display.ActiveProfileName) || GetState(display) != null);
        }

        public void Reset(DisplayInfo display)
        {
            if (display == null) throw new ArgumentNullException(nameof(display));
            var state = GetState(display);
            if (state == null)
                throw new InvalidOperationException(IsManagedProfile(display.ActiveProfileName)
                    ? "Ein altes HDR-Calibrate-Profil ist aktiv, aber ohne Wiederherstellungspunkt; das ursprüngliche Profil ist unbekannt und wird nicht geraten."
                    : "Für diesen Monitor ist kein von HDR Calibrate gespeicherter Wiederherstellungspunkt vorhanden.");

            // Only HDR Calibrate's own association is undone. A profile chosen
            // since then is a later user decision; restoring the old original
            // would overwrite it.
            var laterUserChoice = KeepsLaterUserChoice(
                ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser), state.OriginalProfileName);
            if (!laterUserChoice && !string.IsNullOrWhiteSpace(state.OriginalProfileName))
            {
                RestoreProfile(display, state.OriginalProfileName);
                var verified = ReadDefaultProfile(display, WcsProfileManagementScope.CurrentUser);
                if (!string.Equals(verified, state.OriginalProfileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Windows hat das ursprüngliche HDR-Profil nicht als Standard bestätigt.");
            }
            else if (!laterUserChoice && !string.IsNullOrWhiteSpace(state.LastAppliedProfileName))
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

            // Also covers a later user choice: the own association is no longer the default.
            if (IsManagedProfile(state.LastAppliedProfileName))
                RemoveManagedAssociation(display, state.LastAppliedProfileName);
            _stateStore.Remove(state.DisplayKey);
        }

        /// <summary>
        /// True when the current default is a foreign profile other than the saved
        /// original: the user chose it after HDR Calibrate applied its own.
        /// </summary>
        internal static bool KeepsLaterUserChoice(string currentDefault, string originalProfileName)
        {
            return !string.IsNullOrWhiteSpace(currentDefault)
                && !IsManagedProfile(currentDefault)
                && !string.Equals(currentDefault, originalProfileName, StringComparison.OrdinalIgnoreCase);
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

        private static void ThrowIfFailed(int hresult, string message)
        {
            if (hresult < 0) throw new COMException(message, hresult);
        }
    }
}
