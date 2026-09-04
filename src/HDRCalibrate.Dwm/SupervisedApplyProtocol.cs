using System;
using System.IO;
using System.Linq;

namespace HDRCalibrate.Dwm
{
    /// <summary>
    /// Pure, side-effect-free validation for the persistent apply handshake shared by
    /// the unelevated app, elevated supervisor, patch worker and independent watchdog.
    /// </summary>
    public static class SupervisedApplyProtocol
    {
        public static DwmPatchResponse CreateCommit(DwmPatchResponse activation, string token)
        {
            ValidateResponse(activation, token, false);
            return new DwmPatchResponse
            {
                Success = true,
                WatchdogArmed = true,
                CorrelationToken = token,
                DwmProcessId = activation.DwmProcessId,
                DwmStartTimeUtcTicks = activation.DwmStartTimeUtcTicks,
                DwmCoreVersion = activation.DwmCoreVersion,
                PatchActivatedUtcTicks = activation.PatchActivatedUtcTicks,
                PatchPlanFingerprint = activation.PatchPlanFingerprint,
                Message = "Der aktive DWM-Zustand wurde atomar gespeichert."
            };
        }

        public static bool IsValidCommit(
            string token,
            DwmPatchResponse activation,
            DwmPatchResponse worker,
            DwmPatchResponse commit)
        {
            try
            {
                ValidateResponse(activation, token, false);
                ValidateResponse(worker, token, false);
                if (commit == null || !commit.Success || !commit.WatchdogArmed)
                    return false;
                if (!TimedDwmTestRunner.IsValidCorrelationToken(token)
                    || !string.Equals(commit.CorrelationToken, token, StringComparison.Ordinal))
                    return false;
                return MatchesActivation(activation, worker)
                    && MatchesActivation(activation, commit);
            }
            catch
            {
                return false;
            }
        }

        public static void ValidateCommittedApply(
            string token,
            DwmPatchResponse applied,
            DwmPatchResponse committed)
        {
            if (!IsValidCommit(token, applied, applied, committed)
                || !committed.CommitAccepted)
                throw new InvalidDataException("Die Commit-Bestätigung passt nicht zur DWM-Aktivierung.");
        }

        public static void ValidateResponse(
            DwmPatchResponse response,
            string token,
            bool requireCommit)
        {
            if (response == null || !response.Success)
                throw new InvalidDataException("Der überwachte Apply-Pfad meldete keinen Erfolg.");
            if (!response.WatchdogArmed)
                throw new InvalidDataException("Der unabhängige Apply-Watchdog war nicht bestätigt.");
            if (requireCommit && !response.CommitAccepted)
                throw new InvalidDataException("Der unabhängige Apply-Watchdog bestätigte den Zustands-Commit nicht.");
            if (!TimedDwmTestRunner.IsValidCorrelationToken(token)
                || !string.Equals(response.CorrelationToken, token, StringComparison.Ordinal))
                throw new InvalidDataException("Der überwachte Apply-Token stimmt nicht überein.");
            if (response.DwmProcessId <= 0 || response.DwmStartTimeUtcTicks <= DateTime.MinValue.Ticks)
                throw new InvalidDataException("Die exakte DWM-Prozessidentität fehlt.");
            if (response.PatchActivatedUtcTicks <= DateTime.MinValue.Ticks)
                throw new InvalidDataException("Der Aktivierungszeitpunkt des DWM-Patches fehlt.");
            if (string.IsNullOrWhiteSpace(response.DwmCoreVersion))
                throw new InvalidDataException("Die DWM-Modulversion fehlt.");
            if (!IsUppercaseSha256(response.PatchPlanFingerprint))
                throw new InvalidDataException("Der Fingerabdruck des DWM-Schreibplans ist ungültig.");
        }

        private static bool MatchesActivation(DwmPatchResponse expected, DwmPatchResponse actual)
        {
            return expected.DwmProcessId == actual.DwmProcessId
                && expected.DwmStartTimeUtcTicks == actual.DwmStartTimeUtcTicks
                && expected.PatchActivatedUtcTicks == actual.PatchActivatedUtcTicks
                && string.Equals(expected.DwmCoreVersion, actual.DwmCoreVersion, StringComparison.Ordinal)
                && string.Equals(
                    expected.PatchPlanFingerprint,
                    actual.PatchPlanFingerprint,
                    StringComparison.Ordinal);
        }

        private static bool IsUppercaseSha256(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.Length == 64
                && value.All(character =>
                    (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'F'));
        }
    }
}
