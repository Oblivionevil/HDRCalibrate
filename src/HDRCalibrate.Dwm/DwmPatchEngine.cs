using HDRCalibrate.Core;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;

namespace HDRCalibrate.Dwm
{
    public sealed class DwmPatchEngine
    {
        private const int RestartTimeoutMilliseconds = 15000;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        private sealed class DwmProcessModule
        {
            public int ProcessId { get; set; }
            public long StartTimeUtcTicks { get; set; }
            public IntPtr ModuleBase { get; set; }
            public int ModuleSize { get; set; }
            public string ModulePath { get; set; }
        }

        public DwmPatchResponse Preflight(CalibrationSettings settings)
        {
            var diagnostics = new List<string>();
            try
            {
                settings.Validate();
                var compatibility = DwmCoreInspector.InspectSystem();
                diagnostics.Add("dwmcore.dll: " + compatibility.DwmCoreVersion);
                diagnostics.Add(compatibility.Reason);
                if (!compatibility.IsCompatible)
                    throw new NotSupportedException(compatibility.Reason);

                DwmShaderRewriter.ValidateSupportedSettings(settings);
                diagnostics.Add("ABI-erhaltender Konstantenpatch: Originalshader bleiben vollständig erhalten; nur 16 freigegebene SHDR-Konstanten werden geändert.");
                var diskImage = File.ReadAllBytes(compatibility.DwmCorePath);
                DwmShaderRewriter.Rewrite(diskImage, settings, out var rewriteReport);
                diagnostics.Add(
                    rewriteReport.PatchedShaderCount + " Zielshader in " + rewriteReport.ParentLibraryCount +
                    " Bibliotheken; exakt " + rewriteReport.ChangedByteCount + " geänderte Bytes auf " +
                    rewriteReport.ModifiedPageCount + " Speicherseiten.");

                return new DwmPatchResponse
                {
                    Success = true,
                    LivePatchBlocked = !DwmPatchSafety.LivePatchEnabled,
                    TimedTestAvailable = DwmPatchSafety.TimedTestEnabled,
                    Message = "DWM-SDR-Shaderprüfung erfolgreich.",
                    DwmCoreVersion = compatibility.DwmCoreVersion,
                    PatchedShaderCount = rewriteReport.PatchedShaderCount,
                    PatchedConstantVectorCount = rewriteReport.PatchedConstantVectorCount,
                    ChangedByteCount = rewriteReport.ChangedByteCount,
                    ModifiedPageCount = rewriteReport.ModifiedPageCount,
                    Diagnostics = diagnostics
                };
            }
            catch (Exception ex)
            {
                diagnostics.Add(ex.GetType().Name + ": " + ex.Message);
                return new DwmPatchResponse
                {
                    Success = false,
                    Message = ex.Message,
                    Diagnostics = diagnostics
                };
            }
        }

        public DwmPatchResponse Apply(CalibrationSettings settings)
        {
            DwmPatchSafety.EnsureLivePatchEnabled();
            return ApplyCore(settings, null);
        }

        public DwmPatchResponse ApplySupervisedTest(
            CalibrationSettings settings,
            Action<DwmPatchResponse> activationCallback)
        {
            DwmPatchSafety.EnsureTimedTestEnabled();
            return ApplyCore(settings, activationCallback);
        }

        private DwmPatchResponse ApplyCore(
            CalibrationSettings settings,
            Action<DwmPatchResponse> activationCallback)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            settings.Validate();
            if (settings.IsNeutral()) return Reset();
            EnsureAdministrator();

            var diagnostics = new List<string>();
            var compatibility = DwmCoreInspector.InspectSystem();
            if (!compatibility.IsCompatible) throw new NotSupportedException(compatibility.Reason);

            // Complete every fallible manifest/container/reflection check before DWM is restarted.
            DwmShaderRewriter.ValidateSupportedSettings(settings);
            var diskImage = File.ReadAllBytes(compatibility.DwmCorePath);
            DwmShaderRewriter.Rewrite(diskImage, settings, out var preflightReport);
            diagnostics.Add(
                "Preflight: " + preflightReport.PatchedShaderCount + " SDR-Shader, " +
                preflightReport.PatchedConstantVectorCount + " Konstantenvektoren, " +
                preflightReport.ModifiedPageCount + " Seiten.");

            EnableDebugPrivilege();
            DwmProcessModule module = null;
            try
            {
                module = RestartDwm(diagnostics);
                var runtimeReport = PatchRunningDwm(module, settings, preflightReport, diagnostics);
                var patchActivatedUtcTicks = DateTime.UtcNow.Ticks;
                var activation = new DwmPatchResponse
                {
                    Success = true,
                    Message = "Die SDR-Korrektur wurde in DWM aktiviert.",
                    DwmProcessId = module.ProcessId,
                    DwmStartTimeUtcTicks = module.StartTimeUtcTicks,
                    DwmCoreVersion = compatibility.DwmCoreVersion,
                    PatchedShaderCount = runtimeReport.PatchedShaderCount,
                    PatchedConstantVectorCount = runtimeReport.PatchedConstantVectorCount,
                    ChangedByteCount = runtimeReport.ChangedByteCount,
                    ModifiedPageCount = runtimeReport.ModifiedPageCount,
                    PatchActivatedUtcTicks = patchActivatedUtcTicks,
                    Diagnostics = new List<string>(diagnostics)
                };
                activationCallback?.Invoke(activation);
                Thread.Sleep(500);
                if (!IsCurrentSessionDwmAlive(module.ProcessId, module.StartTimeUtcTicks))
                    throw new InvalidOperationException("DWM wurde nach dem Patchen neu gestartet; die Korrektur ist deshalb nicht aktiv.");

                return new DwmPatchResponse
                {
                    Success = true,
                    Message = "Die SDR-Korrektur ist im aktuellen DWM-Prozess aktiv.",
                    DwmProcessId = module.ProcessId,
                    DwmStartTimeUtcTicks = module.StartTimeUtcTicks,
                    DwmCoreVersion = compatibility.DwmCoreVersion,
                    PatchedShaderCount = runtimeReport.PatchedShaderCount,
                    PatchedConstantVectorCount = runtimeReport.PatchedConstantVectorCount,
                    ChangedByteCount = runtimeReport.ChangedByteCount,
                    ModifiedPageCount = runtimeReport.ModifiedPageCount,
                    PatchActivatedUtcTicks = patchActivatedUtcTicks,
                    Diagnostics = diagnostics
                };
            }
            catch
            {
                // Any partial memory write disappears with a clean DWM restart.
                try
                {
                    diagnostics.Add("Fehlerpfad: sauberer DWM-Neustart ohne Patch wird erzwungen.");
                    var recovered = RestartDwm(diagnostics);
                    VerifyUnpatchedDwm(recovered, diagnostics);
                }
                catch (Exception recoveryError)
                {
                    diagnostics.Add("DWM-Rückfallneustart fehlgeschlagen: " + recoveryError.Message);
                }
                throw;
            }
        }

        public DwmPatchResponse Reset()
        {
            EnsureAdministrator();
            EnableDebugPrivilege();
            var diagnostics = new List<string>();
            var module = RestartDwm(diagnostics);
            VerifyUnpatchedDwm(module, diagnostics);
            return new DwmPatchResponse
            {
                Success = true,
                Message = "DWM wurde ungepatcht neu gestartet; die SDR-Korrektur ist entfernt.",
                DwmProcessId = module.ProcessId,
                DwmStartTimeUtcTicks = module.StartTimeUtcTicks,
                DwmCoreVersion = FileVersionInfo.GetVersionInfo(module.ModulePath).FileVersion,
                Diagnostics = diagnostics
            };
        }

        private static DwmRewriteReport PatchRunningDwm(
            DwmProcessModule module,
            CalibrationSettings settings,
            DwmRewriteReport preflightReport,
            ICollection<string> diagnostics)
        {
            var expectedPath = Path.GetFullPath(DwmCoreInspector.GetSystemDwmCorePath());
            var actualPath = Path.GetFullPath(module.ModulePath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("DWM hat dwmcore.dll nicht aus System32 geladen: " + actualPath);

            var access = DwmNativeMethods.ProcessQueryInformation
                | DwmNativeMethods.ProcessVmRead
                | DwmNativeMethods.ProcessVmWrite
                | DwmNativeMethods.ProcessVmOperation
                | DwmNativeMethods.ProcessSuspendResume;
            var process = DwmNativeMethods.OpenProcess(access, false, checked((uint)module.ProcessId));
            if (process == IntPtr.Zero) throw NewWin32("dwm.exe konnte nicht zum Patchen geöffnet werden.");

            var suspended = false;
            try
            {
                var status = DwmNativeMethods.NtSuspendProcess(process);
                if (status != 0)
                    throw new InvalidOperationException("dwm.exe konnte nicht angehalten werden (NTSTATUS 0x" + status.ToString("X8") + ").");
                suspended = true;

                var original = ReadModule(process, module, diagnostics);
                var modified = DwmShaderRewriter.Rewrite(original, settings, out var report);
                if (preflightReport == null
                    || report.PatchedShaderCount != preflightReport.PatchedShaderCount
                    || report.PatchedConstantVectorCount != preflightReport.PatchedConstantVectorCount
                    || report.ParentLibraryCount != preflightReport.ParentLibraryCount
                    || report.ChangedByteCount != preflightReport.ChangedByteCount)
                    throw new InvalidDataException(
                        "Der Laufzeit-Schreibplan weicht vom vollständig geprüften Datenträger-Schreibplan ab.");
                WriteModifiedRanges(process, module.ModuleBase, modified, report.ModifiedRanges);
                VerifyModifiedRanges(process, module, modified, report.ModifiedRanges);
                diagnostics.Add(
                    "Laufzeitpatch: " + report.PatchedShaderCount + " Originalshader / " +
                    report.PatchedConstantVectorCount + " SHDR-Konstantenvektoren, " +
                    report.ChangedByteCount + " tatsächlich geänderte Bytes auf " +
                    report.ModifiedPageCount + " Seiten; Rücklesen erfolgreich.");
                return report;
            }
            finally
            {
                try
                {
                    if (suspended)
                    {
                        var resumeStatus = DwmNativeMethods.NtResumeProcess(process);
                        if (resumeStatus != 0)
                        {
                            diagnostics.Add("FEHLER: NtResumeProcess meldete 0x" + resumeStatus.ToString("X8") + ".");
                            throw new InvalidOperationException(
                                "dwm.exe konnte nach dem Patchversuch nicht fortgesetzt werden (NTSTATUS 0x" +
                                resumeStatus.ToString("X8") + ").");
                        }
                    }
                }
                finally
                {
                    DwmNativeMethods.CloseHandle(process);
                }
            }
        }

        private static byte[] ReadModule(
            IntPtr process,
            DwmProcessModule module,
            ICollection<string> diagnostics)
        {
            var image = new byte[module.ModuleSize];
            var moduleStart = module.ModuleBase.ToInt64();
            var moduleEnd = checked(moduleStart + module.ModuleSize);
            var cursor = moduleStart;
            var readableRegionCount = 0;
            long readableBytes = 0;

            while (cursor < moduleEnd)
            {
                var querySize = new UIntPtr(checked((uint)Marshal.SizeOf(typeof(DwmNativeMethods.MemoryBasicInformation))));
                if (DwmNativeMethods.VirtualQueryEx(
                        process,
                        new IntPtr(cursor),
                        out var info,
                        querySize).ToUInt64() == 0)
                    throw NewWin32("Der Speicheraufbau von dwmcore.dll konnte nicht abgefragt werden.");

                var regionStart = info.BaseAddress.ToInt64();
                var regionSize = checked((long)info.RegionSize.ToUInt64());
                if (regionSize <= 0) throw new InvalidDataException("DWM meldet eine leere Speicherregion.");
                var regionEnd = checked(regionStart + regionSize);
                var readStart = Math.Max(moduleStart, regionStart);
                var readEnd = Math.Min(moduleEnd, regionEnd);
                var baseProtection = info.Protect & 0xFFu;
                var readable = info.State == DwmNativeMethods.MemCommit
                    && (info.Protect & DwmNativeMethods.PageGuard) == 0
                    && baseProtection != 0
                    && baseProtection != DwmNativeMethods.PageNoAccess;

                if (readable && readEnd > readStart)
                {
                    var length = checked((int)(readEnd - readStart));
                    var region = new byte[length];
                    if (!DwmNativeMethods.ReadProcessMemory(
                            process,
                            new IntPtr(readStart),
                            region,
                            new UIntPtr(checked((uint)length)),
                            out var read)
                        || read.ToUInt64() != (ulong)length)
                        throw NewWin32("Eine lesbare dwmcore.dll-Speicherregion konnte nicht vollständig gelesen werden.");
                    Buffer.BlockCopy(region, 0, image, checked((int)(readStart - moduleStart)), length);
                    readableRegionCount++;
                    readableBytes += length;
                }

                if (regionEnd <= cursor) throw new InvalidDataException("DWM meldet überlappende Speicherregionen.");
                cursor = regionEnd;
            }

            diagnostics.Add(
                "dwmcore.dll: " + readableRegionCount + " lesbare Regionen, " + readableBytes +
                " von " + module.ModuleSize + " Byte erfasst.");
            return image;
        }

        private static void WriteModifiedRanges(
            IntPtr process,
            IntPtr moduleBase,
            byte[] modified,
            IEnumerable<DwmWriteRange> ranges)
        {
            foreach (var range in ranges)
            {
                if (range == null || range.Offset < 0 || range.Length <= 0
                    || range.Offset > modified.Length - range.Length)
                    throw new InvalidDataException("Ein DWM-Schreibbereich liegt außerhalb des Modulabbilds.");
                var bytes = new byte[range.Length];
                Buffer.BlockCopy(modified, range.Offset, bytes, 0, range.Length);
                var address = Add(moduleBase, range.Offset);

                if (!DwmNativeMethods.VirtualProtectEx(
                        process,
                        address,
                        new UIntPtr(checked((uint)range.Length)),
                        DwmNativeMethods.PageReadWrite,
                        out var oldProtection))
                    throw NewWin32("Eine DWM-Speicherseite konnte nicht beschreibbar gemacht werden.");

                try
                {
                    if (!DwmNativeMethods.WriteProcessMemory(
                            process,
                            address,
                            bytes,
                            new UIntPtr(checked((uint)bytes.Length)),
                            out var written)
                        || written.ToUInt64() != (ulong)bytes.Length)
                        throw NewWin32("Ein bytegenauer DWM-Patchbereich konnte nicht geschrieben werden.");
                }
                finally
                {
                    if (!DwmNativeMethods.VirtualProtectEx(
                            process,
                            address,
                            new UIntPtr(checked((uint)range.Length)),
                            oldProtection,
                            out _))
                        throw NewWin32("Der ursprüngliche Schutz eines DWM-Patchbereichs konnte nicht wiederhergestellt werden.");
                }
            }
        }

        private static void VerifyModifiedRanges(
            IntPtr process,
            DwmProcessModule module,
            byte[] expected,
            IEnumerable<DwmWriteRange> ranges)
        {
            foreach (var range in ranges)
            {
                var actual = new byte[range.Length];
                if (!DwmNativeMethods.ReadProcessMemory(
                        process,
                        Add(module.ModuleBase, range.Offset),
                        actual,
                        new UIntPtr(checked((uint)range.Length)),
                        out var read)
                    || read.ToUInt64() != (ulong)range.Length)
                    throw NewWin32("Ein bytegenauer DWM-Patchbereich konnte nicht zurückgelesen werden.");

                for (var index = 0; index < range.Length; index++)
                {
                    if (actual[index] != expected[range.Offset + index])
                        throw new InvalidDataException("Die Rückleseprüfung des DWM-Patches ist fehlgeschlagen.");
                }
            }
        }

        private static DwmProcessModule RestartDwm(ICollection<string> diagnostics)
        {
            var sessionId = Process.GetCurrentProcess().SessionId;
            var oldProcesses = GetDwmProcessIdentities(sessionId).ToArray();
            if (oldProcesses.Length == 0)
                throw new InvalidOperationException("In der aktuellen Sitzung wurde kein dwm.exe-Prozess gefunden.");

            diagnostics.Add(
                "DWM-Neustart in Sitzung " + sessionId + "; alte Identität(en): " +
                string.Join(", ", oldProcesses.Select(item => item.ProcessId + "@" + item.StartTimeUtcTicks)));
            foreach (var process in oldProcesses) TerminateDwm(process.ProcessId, process.StartTimeUtcTicks);

            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < RestartTimeoutMilliseconds)
            {
                foreach (var candidate in GetDwmProcessIdentities(sessionId))
                {
                    if (oldProcesses.Any(old => SameIdentity(old, candidate))) continue;
                    var module = TryGetDwmCoreModule(candidate.ProcessId);
                    if (module == null) continue;
                    if (module.StartTimeUtcTicks != candidate.StartTimeUtcTicks) continue;
                    diagnostics.Add(
                        "Neuer DWM-Prozess " + candidate.ProcessId + "@" + candidate.StartTimeUtcTicks +
                        " mit dwmcore.dll nach " + stopwatch.ElapsedMilliseconds + " ms gefunden.");
                    return module;
                }
                Thread.Sleep(10);
            }
            throw new TimeoutException("Windows hat DWM nicht rechtzeitig neu gestartet.");
        }

        private static IEnumerable<TimedTestProcessIdentity> GetDwmProcessIdentities(int sessionId)
        {
            foreach (var process in Process.GetProcessesByName("dwm"))
            {
                using (process)
                {
                    int candidateSession;
                    long startTimeUtcTicks;
                    try
                    {
                        candidateSession = process.SessionId;
                        startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                    }
                    catch
                    {
                        continue;
                    }
                    if (candidateSession == sessionId)
                        yield return new TimedTestProcessIdentity
                        {
                            ProcessId = process.Id,
                            StartTimeUtcTicks = startTimeUtcTicks
                        };
                }
            }
        }

        private static bool IsCurrentSessionDwmAlive(int processId, long expectedStartTimeUtcTicks)
        {
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    return !process.HasExited
                        && process.SessionId == Process.GetCurrentProcess().SessionId
                        && process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void TerminateDwm(int processId, long expectedStartTimeUtcTicks)
        {
            using (var candidate = Process.GetProcessById(processId))
            {
                if (candidate.HasExited
                    || candidate.SessionId != Process.GetCurrentProcess().SessionId
                    || candidate.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks)
                    throw new InvalidOperationException("Die zu beendende DWM-Prozessidentität hat sich geändert.");
            }
            var process = DwmNativeMethods.OpenProcess(DwmNativeMethods.ProcessTerminate, false, checked((uint)processId));
            if (process == IntPtr.Zero) throw NewWin32("dwm.exe konnte nicht für den Neustart geöffnet werden.");
            try
            {
                if (!DwmNativeMethods.TerminateProcess(process, 0))
                    throw NewWin32("dwm.exe konnte nicht neu gestartet werden.");
            }
            finally
            {
                DwmNativeMethods.CloseHandle(process);
            }
        }

        private static DwmProcessModule TryGetDwmCoreModule(int processId)
        {
            var snapshot = DwmNativeMethods.CreateToolhelp32Snapshot(
                DwmNativeMethods.Th32csSnapModule | DwmNativeMethods.Th32csSnapModule32,
                checked((uint)processId));
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue) return null;
            try
            {
                var entry = new DwmNativeMethods.ModuleEntry32
                {
                    dwSize = checked((uint)Marshal.SizeOf(typeof(DwmNativeMethods.ModuleEntry32)))
                };
                if (!DwmNativeMethods.Module32FirstW(snapshot, ref entry)) return null;
                do
                {
                    if (!string.Equals(entry.szModule, "dwmcore.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.modBaseSize == 0 || entry.modBaseSize > int.MaxValue) return null;
                    return new DwmProcessModule
                    {
                        ProcessId = processId,
                        StartTimeUtcTicks = GetProcessStartTimeUtcTicks(processId),
                        ModuleBase = entry.modBaseAddr,
                        ModuleSize = checked((int)entry.modBaseSize),
                        ModulePath = entry.szExePath
                    };
                } while (DwmNativeMethods.Module32NextW(snapshot, ref entry));
                return null;
            }
            finally
            {
                DwmNativeMethods.CloseHandle(snapshot);
            }
        }

        private static bool SameIdentity(TimedTestProcessIdentity left, TimedTestProcessIdentity right)
        {
            return left.ProcessId == right.ProcessId
                && left.StartTimeUtcTicks == right.StartTimeUtcTicks;
        }

        private static long GetProcessStartTimeUtcTicks(int processId)
        {
            using (var process = Process.GetProcessById(processId))
                return process.StartTime.ToUniversalTime().Ticks;
        }

        private static void VerifyUnpatchedDwm(DwmProcessModule module, ICollection<string> diagnostics)
        {
            var expectedPath = Path.GetFullPath(DwmCoreInspector.GetSystemDwmCorePath());
            var actualPath = Path.GetFullPath(module.ModulePath);
            if (!string.Equals(expectedPath, actualPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Der neue DWM hat dwmcore.dll nicht aus System32 geladen.");

            var compatibility = DwmCoreInspector.InspectSystem();
            if (!compatibility.IsCompatible) throw new NotSupportedException(compatibility.Reason);

            var access = DwmNativeMethods.ProcessQueryInformation | DwmNativeMethods.ProcessVmRead;
            var process = DwmNativeMethods.OpenProcess(access, false, checked((uint)module.ProcessId));
            if (process == IntPtr.Zero) throw NewWin32("Der neue DWM konnte nicht zur Rücksetzprüfung geöffnet werden.");
            try
            {
                var image = ReadModule(process, module, diagnostics);
                var manifest = DwmCoreInspector.ValidateShaderManifest(image);
                if (manifest.Shaders.Count != 6 || manifest.Parents.Count != 3)
                    throw new InvalidDataException("Der neue DWM enthält nicht das vollständige ungepatchte Shadermanifest.");
                diagnostics.Add(
                    "Reset verifiziert: neue DWM-Identität, System32-Modul und alle sechs Originalshader sind bytegenau vorhanden.");
            }
            finally
            {
                DwmNativeMethods.CloseHandle(process);
            }
        }

        private static void EnableDebugPrivilege()
        {
            if (!DwmNativeMethods.OpenProcessToken(
                    DwmNativeMethods.GetCurrentProcess(),
                    DwmNativeMethods.TokenAdjustPrivileges | DwmNativeMethods.TokenQuery,
                    out var token))
                throw NewWin32("Das Prozesstoken konnte nicht geöffnet werden.");
            try
            {
                if (!DwmNativeMethods.LookupPrivilegeValue(null, "SeDebugPrivilege", out var luid))
                    throw NewWin32("SeDebugPrivilege konnte nicht aufgelöst werden.");
                var privileges = new DwmNativeMethods.TokenPrivileges
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = DwmNativeMethods.SePrivilegeEnabled
                };
                if (!DwmNativeMethods.AdjustTokenPrivileges(
                        token,
                        false,
                        ref privileges,
                        0,
                        IntPtr.Zero,
                        IntPtr.Zero))
                    throw NewWin32("SeDebugPrivilege konnte nicht aktiviert werden.");
                var error = Marshal.GetLastWin32Error();
                if (error == DwmNativeMethods.ErrorNotAllAssigned)
                    throw new Win32Exception(error, "SeDebugPrivilege wurde dem erhöhten Prozess nicht zugewiesen.");
            }
            finally
            {
                DwmNativeMethods.CloseHandle(token);
            }
        }

        private static void EnsureAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
            {
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Der DWM-Helfer muss mit Administratorrechten ausgeführt werden.");
            }
        }

        private static IntPtr Add(IntPtr pointer, int offset)
        {
            return new IntPtr(checked(pointer.ToInt64() + offset));
        }

        private static Win32Exception NewWin32(string message)
        {
            return new Win32Exception(Marshal.GetLastWin32Error(), message);
        }
    }
}
