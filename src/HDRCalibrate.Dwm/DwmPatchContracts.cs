using HDRCalibrate.Core;
using System;
using System.Collections.Generic;

namespace HDRCalibrate.Dwm
{
    [Serializable]
    public enum DwmPatchCommand
    {
        Unknown = 0,
        Apply = 1,
        Reset = 2,
        Preflight = 3,
        SafetyReset = 4,
        TimedTest = 5,
        TimedTestWorker = 6,
        TimedTestWatchdog = 7,
        SupervisedApply = 8,
        SupervisedApplyWorker = 9,
        SupervisedApplyWatchdog = 10
    }

    [Serializable]
    public sealed class DwmPatchRequest
    {
        public DwmPatchCommand Command { get; set; }
        public CalibrationSettings Settings { get; set; } = new CalibrationSettings();
        public string CorrelationToken { get; set; }
        public string StartGatePath { get; set; }
        public string ReadyPath { get; set; }
        public string WorkerIdentityPath { get; set; }
        public string WorkerResponsePath { get; set; }
        public string ActivationPath { get; set; }
        public string RecoveryResponsePath { get; set; }
        public string CommitPath { get; set; }
        public string CommitAcceptedPath { get; set; }
        public WindowsEnvironmentIdentity ExpectedWindowsEnvironment { get; set; }
        public StartupApprovalFileProof StartupApproval { get; set; }
    }

    [Serializable]
    public sealed class DwmPatchResponse
    {
        public bool Success { get; set; }
        public bool LivePatchBlocked { get; set; }
        public bool TimedTestAvailable { get; set; }
        public string Message { get; set; }
        public int DwmProcessId { get; set; }
        public string DwmCoreVersion { get; set; }
        public long DwmStartTimeUtcTicks { get; set; }
        public int PatchedShaderCount { get; set; }
        public int PatchedConstantVectorCount { get; set; }
        public int ChangedByteCount { get; set; }
        public int ModifiedPageCount { get; set; }
        public long PatchActivatedUtcTicks { get; set; }
        public long WatchdogResetUtcTicks { get; set; }
        public bool WatchdogArmed { get; set; }
        public bool CommitAccepted { get; set; }
        public bool RecoveryPerformed { get; set; }
        public int TestedDwmProcessId { get; set; }
        public int TestDurationMilliseconds { get; set; }
        public string CorrelationToken { get; set; }
        public string PatchPlanFingerprint { get; set; }
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    [Serializable]
    public sealed class TimedTestProcessIdentity
    {
        public int ProcessId { get; set; }
        public long StartTimeUtcTicks { get; set; }
    }

    public sealed class DwmCompatibilityInfo
    {
        public bool IsCompatible { get; set; }
        public string DwmCorePath { get; set; }
        public string DwmCoreVersion { get; set; }
        public int TargetShaderCount { get; set; }
        public int ValidatedSdrShaderCount { get; set; }
        public int ValidatedParentLibraryCount { get; set; }
        public string DwmCoreSha256 { get; set; }
        public string Reason { get; set; }
    }

    public sealed class DwmRewriteReport
    {
        public int PatchedShaderCount { get; set; }
        public int PatchedConstantVectorCount { get; set; }
        public int ParentLibraryCount { get; set; }
        public int ModifiedPageCount { get; set; }
        public int ChangedByteCount { get; set; }
        public List<int> ModifiedPageOffsets { get; } = new List<int>();
        public List<DwmWriteRange> ModifiedRanges { get; } = new List<DwmWriteRange>();
    }

    public sealed class DwmWriteRange
    {
        public int Offset { get; set; }
        public int Length { get; set; }
    }
}
