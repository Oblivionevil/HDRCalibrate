using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HDRCalibrate.Services
{
    public static class ProgramGammaWindowFinder
    {
        private delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr state);
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr state);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int capacity);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint rights, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder path, ref int length);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        // Bounds the enumeration itself and, separately, the per-window process
        // inspection. Invisible helper windows must not exhaust the inspection budget.
        private const int MaximumEnumeratedWindows = 65536;
        private const int MaximumInspectedWindows = 2048;

        public static ProgramGammaTarget Find(string executablePath)
        {
            var path = ProgramProfileRules.NormalizePath(executablePath);
            // A path selected through a junction, symlink or subst drive reaches the
            // process image under its resolved name. Compare both by final path,
            // but bind the target to the image name the controller will verify.
            var finalPath = TryGetFinalPath(path);
            var finalPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var own = Process.GetCurrentProcess())
            {
                var candidates = new List<Tuple<long, ProgramGammaTarget>>();
                var enumerated = 0;
                var inspected = 0;
                var completed = EnumWindows((hwnd, ignoredState) =>
                {
                    if (++enumerated > MaximumEnumeratedWindows) return false;
                    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || hwnd.ToInt64() <= 0) return true;
                    if (++inspected > MaximumInspectedWindows) return false;
                    if (GetWindowThreadProcessId(hwnd, out var pid) == 0 || pid <= 0 || pid == own.Id
                        || !ProcessIdToSessionId(pid, out var session) || session != own.SessionId) return true;
                    // No VM_READ, VM_WRITE, CREATE_THREAD or terminate rights on applications.
                    using (var handle = OpenProcess(0x1000 | 0x100000, false, pid))
                    {
                        if (handle.IsInvalid || WaitForSingleObject(handle, 0) != 258) return true;
                        var currentPath = new StringBuilder(32768);
                        var length = currentPath.Capacity;
                        if (!QueryFullProcessImageName(handle, 0, currentPath, ref length)) return true;
                        var imagePath = currentPath.ToString();
                        if (!string.Equals(imagePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            if (finalPath == null) return true;
                            if (!finalPaths.TryGetValue(imagePath, out var imageFinalPath))
                                finalPaths[imagePath] = imageFinalPath = TryGetFinalPath(imagePath);
                            if (!string.Equals(imageFinalPath, finalPath, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        if (!GetProcessTimes(handle, out var creation, out _, out _, out _) || creation <= 0
                            || !GetClientRect(hwnd, out var rect)) return true;
                        var width = (long)rect.Right - rect.Left;
                        var height = (long)rect.Bottom - rect.Top;
                        if (width <= 0 || height <= 0 || width > 32768 || height > 32768) return true;
                        var title = new StringBuilder(256);
                        GetWindowText(hwnd, title, title.Capacity);
                        ProgramGammaTarget candidate;
                        try { candidate = new ProgramGammaTarget(imagePath, pid, creation, hwnd.ToInt64(), title.ToString()); }
                        catch (ArgumentException) { return true; }
                        candidates.Add(Tuple.Create(width * height, candidate));
                    }
                    return true;
                }, IntPtr.Zero);
                if (!completed) throw new InvalidOperationException("Die Fensterliste konnte nicht vollständig geprüft werden.");
                // Deterministic, explicitly reported selection; no focus/input changes.
                var target = candidates.OrderByDescending(item => item.Item1).ThenBy(item => item.Item2.ProcessId)
                    .ThenBy(item => item.Item2.WindowHandle).FirstOrDefault()?.Item2;
                return target ?? throw new InvalidOperationException("Kein sichtbares Fenster dieser EXE gefunden. Das Programm bitte selbst starten und nicht minimieren.");
            }
        }

        private static string TryGetFinalPath(string path)
        {
            // FILE_READ_ATTRIBUTES only, all sharing; never blocks or modifies the file.
            using (var file = CreateFile(path, 0x80, 0x7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
            {
                if (file.IsInvalid) return null;
                var buffer = new StringBuilder(32768);
                var length = GetFinalPathNameByHandle(file, buffer, (uint)buffer.Capacity, 0);
                return length == 0 || length >= buffer.Capacity ? null : buffer.ToString();
            }
        }
    }
}
