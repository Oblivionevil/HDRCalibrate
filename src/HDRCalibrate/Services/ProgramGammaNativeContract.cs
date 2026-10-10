using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace HDRCalibrate.Services
{
    public sealed class ProgramGammaTarget
    {
        public string ExecutablePath { get; }
        public int ProcessId { get; }
        public long CreationFileTime { get; }
        public long WindowHandle { get; }
        public string WindowTitle { get; }

        public ProgramGammaTarget(string path, int processId, long creationFileTime, long windowHandle, string title)
        {
            ExecutablePath = ProgramProfileRules.NormalizePath(path);
            if (processId <= 0 || creationFileTime <= 0 || windowHandle <= 0)
                throw new ArgumentException("Die laufende Programmidentität ist unvollständig.");
            ProcessId = processId;
            CreationFileTime = creationFileTime;
            WindowHandle = windowHandle;
            WindowTitle = title ?? string.Empty;
        }

        public string IdentityKey => ExecutablePath.ToUpperInvariant() + "|" + ProcessId.ToString(CultureInfo.InvariantCulture)
            + "|" + CreationFileTime.ToString(CultureInfo.InvariantCulture) + "|" + WindowHandle.ToString(CultureInfo.InvariantCulture);
    }

    [DataContract]
    public sealed class ProgramGammaNativeEvent
    {
        [DataMember(Name = "event")] public string Event { get; set; }
        [DataMember(Name = "phase")] public string Phase { get; set; }
        [DataMember(Name = "started")] public bool? Started { get; set; }
        [DataMember(Name = "armed")] public bool? Armed { get; set; }
        [DataMember(Name = "target_identity_retained")] public bool? TargetRetained { get; set; }
        [DataMember(Name = "dwm_identity_retained")] public bool? DwmRetained { get; set; }
        [DataMember(Name = "stop_completed")] public bool? StopCompleted { get; set; }
        [DataMember(Name = "stop_status")] public int? StopStatus { get; set; }
        [DataMember(Name = "vetoed_candidates")] public long? VetoedCandidates { get; set; }
        [DataMember(Name = "vetoed")] public long? Vetoed { get; set; }
        [DataMember(Name = "read_faults")] public long? ReadFaults { get; set; }
        [DataMember(Name = "active_callbacks")] public long? ActiveCallbacks { get; set; }
        [DataMember(Name = "no_post_expiry_veto")] public bool? NoPostExpiryVeto { get; set; }
        [DataMember(Name = "sample_valid")] public bool? SampleValid { get; set; }
        [DataMember(Name = "qpc")] public long? Qpc { get; set; }
        [DataMember(Name = "sample_qpc")] public long? SampleQpc { get; set; }
        [DataMember(Name = "point")] public int? Point { get; set; }
        [DataMember(Name = "decision")] public int? Decision { get; set; }
        [DataMember(Name = "colorspace")] public long? ColorSpace { get; set; }
        [DataMember(Name = "format")] public int? PixelFormat { get; set; }
        [DataMember(Name = "owner_pid")] public int? OwnerPid { get; set; }
        [DataMember(Name = "hwnd")] public long? WindowHandle { get; set; }

        public static ProgramGammaNativeEvent Parse(string line)
        {
            if (string.IsNullOrEmpty(line) || line.Length > 8192)
                throw new InvalidDataException("Ungültige oder zu große Antwort des Gamma-Helfers.");
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(line)))
            {
                var result = (ProgramGammaNativeEvent)new DataContractJsonSerializer(typeof(ProgramGammaNativeEvent)).ReadObject(stream);
                if (result == null || string.IsNullOrEmpty(result.Event)) throw new InvalidDataException("Helferereignis fehlt.");
                return result;
            }
        }

        public bool ConfirmsActiveSelection
        {
            get
            {
                var now = Stopwatch.GetTimestamp();
                return Event == "telemetry" && Phase == "veto" && Vetoed > 0 && ReadFaults == 0
                    && SampleValid == true && Point == 1 && Decision == 1 && ColorSpace == 0
                    && (PixelFormat == 28 || PixelFormat == 87 || PixelFormat == 88)
                    && Qpc > 0 && SampleQpc > 0 && Qpc >= SampleQpc && now >= Qpc
                    && now - SampleQpc < Stopwatch.Frequency;
            }
        }

        public bool ConfirmsActiveSelectionFor(ProgramGammaTarget target) => ConfirmsActiveSelection
            && target != null && OwnerPid == target.ProcessId && WindowHandle == target.WindowHandle;

        public bool ConfirmsWithdrawal => Event == "complete" && StopCompleted == true && StopStatus == 0
            && ReadFaults == 0 && ActiveCallbacks == 0 && NoPostExpiryVeto == true;

        public bool ConfirmsSafeCompletion(int exitCode) => exitCode == 0 && Event == "complete"
            && Started == true && Armed == true && TargetRetained == true && DwmRetained == true
            && StopCompleted == true && StopStatus == 0 && VetoedCandidates > 0
            && ReadFaults == 0 && ActiveCallbacks == 0 && NoPostExpiryVeto == true;
    }

    public static class ProgramGammaArguments
    {
        public static string Build(ProgramGammaTarget target, int dwmPid, long dwmCreationFileTime,
            int ownerPid, long ownerCreationFileTime, string dllPath, string pipeName, bool continuous)
        {
            if (target == null || dwmPid <= 0 || dwmCreationFileTime <= 0 || ownerPid <= 0 || ownerCreationFileTime <= 0
                || target.ProcessId == dwmPid || target.ProcessId == ownerPid)
                throw new ArgumentException("Unzulässige Prozessidentität für die Gamma-Auswahl.");
            if (string.IsNullOrEmpty(pipeName) || !System.Text.RegularExpressions.Regex.IsMatch(pipeName,
                @"\AHDRCalibrate\.ProgramGamma\.[0-9a-fA-F]{32}\z")) throw new ArgumentException("Ungültiger Kanalname.");
            if (string.IsNullOrWhiteSpace(dllPath) || !Path.IsPathRooted(dllPath) || dllPath.IndexOf('"') >= 0
                || dllPath.IndexOf('\r') >= 0 || dllPath.IndexOf('\n') >= 0
                || !string.Equals(Path.GetFileName(dllPath), "HDRCalibrate.SelectiveDwmHook.dll", StringComparison.Ordinal))
                throw new ArgumentException("Ungültiger nativer Helferpfad.");
            return string.Format(CultureInfo.InvariantCulture,
                "--veto --duration-ms 5000 --dll \"{0}\" --target-pid {1} --target-hwnd {2} --target-exe \"{3}\" "
                + "--target-creation-filetime {4} --expected-dwm-pid {5} --expected-dwm-creation-filetime {6} "
                + "--owner-pid {7} --owner-creation-filetime {8} --pipe {9}{10}",
                dllPath, target.ProcessId, target.WindowHandle, target.ExecutablePath, target.CreationFileTime,
                dwmPid, dwmCreationFileTime, ownerPid, ownerCreationFileTime, pipeName, continuous ? " --continuous" : "");
        }
    }
}
