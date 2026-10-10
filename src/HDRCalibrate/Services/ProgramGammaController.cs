using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace HDRCalibrate.Services
{
    public sealed class ProgramGammaRunResult
    {
        public int ExitCode { get; set; }
        public ProgramGammaNativeEvent Completion { get; set; }
        public string Failure { get; set; }
    }

    public interface IProgramGammaSessionRunner
    {
        Task<ProgramGammaRunResult> RunAsync(ProgramGammaTarget target, ProgramGammaApproval approval,
            bool continuous, Func<bool> authorizeStart, Action<ProgramGammaNativeEvent> onEvent,
            CancellationToken cancellationToken);
    }

    public sealed class ProgramGammaController : IProgramGammaController
    {
        private readonly object _gate = new object();
        private readonly Func<ProgramGammaApproval> _approvalProvider;
        private readonly Func<string, ProgramGammaTarget> _findTarget;
        private readonly IProgramGammaSessionRunner _runner;
        private readonly Lazy<string> _unavailableReason;
        private CancellationTokenSource _session;
        private bool _disposed;
        private bool _busy;
        private bool _active;
        private bool _nativeWithdrawn;
        private string _path;
        private string _proof;
        private string _status = "Zuerst die globale SDR-Korrektur testen und anwenden, danach ein laufendes Programm 5 Sekunden testen.";

        public ProgramGammaController(Func<ProgramGammaApproval> approvalProvider)
            : this(approvalProvider, ProgramGammaWindowFinder.Find, new ProgramGammaSessionRunner(),
                ProgramGammaBuildSupport.GetSystemUnavailableReason) { }

        // Dependencies permit offline state-machine tests without a DWM/app write.
        public ProgramGammaController(Func<ProgramGammaApproval> approvalProvider,
            Func<string, ProgramGammaTarget> findTarget, IProgramGammaSessionRunner runner)
            : this(approvalProvider, findTarget, runner, null) { }

        public ProgramGammaController(Func<ProgramGammaApproval> approvalProvider,
            Func<string, ProgramGammaTarget> findTarget, IProgramGammaSessionRunner runner,
            Func<string> unavailableReason)
        {
            _approvalProvider = approvalProvider ?? throw new ArgumentNullException(nameof(approvalProvider));
            _findTarget = findTarget ?? throw new ArgumentNullException(nameof(findTarget));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _unavailableReason = unavailableReason == null ? null : new Lazy<string>(unavailableReason);
        }

        public event EventHandler StateChanged;
        public bool IsBusy { get { lock (_gate) return _busy; } }
        public bool IsActive { get { lock (_gate) return _active; } }
        public string Status { get { lock (_gate) return _status; } }
        public string ActiveExecutablePath { get { lock (_gate) return _path; } }
        public string UnavailableReason => _unavailableReason?.Value;
        public Task TestAsync(string executablePath) => StartAsync(executablePath, false);
        public Task ActivateAsync(string executablePath) => StartAsync(executablePath, true);

        private Task StartAsync(string executablePath, bool continuous)
        {
            var path = ProgramProfileRules.NormalizePath(executablePath);
            CancellationTokenSource session;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(ProgramGammaController));
                if (_session != null) throw new InvalidOperationException("Zuerst die laufende Gamma-Auswahl stoppen.");
                _session = session = new CancellationTokenSource();
                _busy = true;
                _active = false;
                _nativeWithdrawn = false;
                _path = path;
                _status = "Prüfe aktive SDR-Korrektur und laufendes Programmfenster …";
                if (!continuous) _proof = null;
            }
            Changed();
            return Task.Run(() => RunCoreAsync(path, continuous, session));
        }

        private async Task RunCoreAsync(string path, bool continuous, CancellationTokenSource session)
        {
            try
            {
                var token = session.Token;
                token.ThrowIfCancellationRequested();
                var approval = _approvalProvider();
                if (approval == null || !approval.IsStillValid()) throw new InvalidOperationException("Keine gültige aktive SDR-Gamma-Freigabe.");
                // The global shader allowlist is not a private-hook ABI approval.
                // Reject before looking up the application or starting a helper.
                ProgramGammaBuildSupport.RequireSupported(approval.DwmCoreVersion, approval.WindowsEnvironment.DwmCoreSha256);
                var target = _findTarget(path);
                var key = ProofKey(target, approval);
                lock (_gate)
                    if (continuous && !string.Equals(_proof, key, StringComparison.Ordinal))
                        throw new InvalidOperationException("Dieses laufende Fenster mit den aktuellen Gammaeinstellungen zuerst 5 Sekunden testen.");
                token.ThrowIfCancellationRequested();
                SetSessionState(session, true, false, "Warte auf Freigabe des DWM-Helfers · " + TargetLabel(target));

                // Events arrive sequentially from one reader. A single unconfirmed
                // telemetry sample between confirmed ones must not flip the status.
                long lastConfirmedTimestamp = 0;
                var result = await _runner.RunAsync(target, approval, continuous, () =>
                {
                    token.ThrowIfCancellationRequested();
                    // Recheck after UAC, before the helper receives permission to write DWM.
                    var current = _approvalProvider();
                    return current != null && current.IsStillValid() && approval.IsStillValid()
                        && string.Equals(key, ProofKey(_findTarget(path), current), StringComparison.Ordinal);
                }, item =>
                {
                    if (token.IsCancellationRequested) return;
                    if (item.Event == "managed_surface_rejected" || item.Event == "complete"
                        || (item.Event == "telemetry" && (item.Phase == "expired" || item.Phase == "recovery")))
                        SetSessionWithdrawal(session, item.Event == "managed_surface_rejected"
                            ? "SDR-Fläche geändert; Erzwingen zurückgenommen. Abschlussprüfung läuft …"
                            : "Auswahl-Lease beendet; Rücknahme wird bestätigt …");
                    else if (item.ConfirmsActiveSelectionFor(target))
                    {
                        lastConfirmedTimestamp = Stopwatch.GetTimestamp();
                        SetSessionState(session, !continuous, continuous, (continuous ? "SDR-Auswahl aktiv" : "5-Sekunden-Test läuft")
                            + " · Gamma " + approval.Gamma.ToString("0.0#", CultureInfo.CurrentCulture)
                            + " · " + TargetLabel(target) + " · VRR ungeprüft");
                    }
                    else if (continuous && item.Event == "telemetry" && IsActive
                        && Stopwatch.GetTimestamp() - lastConfirmedTimestamp >= Stopwatch.Frequency)
                        SetSessionState(session, false, true, "Gamma-Sitzung läuft · derzeit kein frisches SDR-Veto bestätigt (Stillstand oder Pfadwechsel möglich) · " + TargetLabel(target));
                }, token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    SetState(false, false, result.Completion?.ConfirmsWithdrawal == true
                        ? "Erzwingen beendet. Die globale SDR-Korrektur und das Programm bleiben unverändert."
                        : "Stop angefordert; der DWM-Lease läuft spätestens nach 5 Sekunden aus. Rückmeldung nicht vollständig bestätigt.");
                    return;
                }
                if (!string.IsNullOrEmpty(result.Failure) || result.Completion?.ConfirmsSafeCompletion(result.ExitCode) != true)
                    throw new InvalidOperationException(result.Failure ?? FailureMessage(result.ExitCode));
                if (!continuous)
                {
                    var current = _approvalProvider();
                    if (!approval.IsStillValid() || current == null || !current.IsStillValid()
                        || key != ProofKey(_findTarget(path), current))
                        throw new InvalidOperationException("Fenster oder Gamma-Freigabe hat sich während des Tests geändert. Kein Testnachweis übernommen.");
                    lock (_gate)
                    {
                        token.ThrowIfCancellationRequested();
                        if (_disposed || !ReferenceEquals(_session, session))
                            throw new OperationCanceledException("Die Gamma-Auswahlsitzung wurde geschlossen.");
                        _proof = key;
                    }
                    SetState(false, false, "5-Sekunden-Auswahltest beendet und zurückgesetzt. Jetzt manuell Erzwingen möglich; Bildwirkung und VRR selbst prüfen.");
                }
                else
                {
                    SetState(false, false, "Erzwingen beendet. Für eine neue Sitzung das Programm erneut testen.");
                    lock (_gate) _proof = null;
                }
            }
            catch (OperationCanceledException)
            {
                lock (_gate) _proof = null;
                SetState(false, false, "Gamma-Auswahl abgebrochen. Keine weitere Lease-Verlängerung.");
            }
            catch (Exception ex)
            {
                lock (_gate) _proof = null;
                SetState(false, false, "Gamma-Auswahl nicht aktiv: " + ex.Message);
                throw;
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_session, session)) _session = null;
                    _busy = false;
                    _active = false;
                    _path = null;
                }
                session.Dispose();
                Changed();
            }
        }

        private static string TargetLabel(ProgramGammaTarget target) => string.IsNullOrWhiteSpace(target.WindowTitle)
            ? System.IO.Path.GetFileName(target.ExecutablePath) : target.WindowTitle;

        public static string ProofKey(ProgramGammaTarget target, ProgramGammaApproval approval)
        {
            if (target == null || approval == null) throw new ArgumentNullException();
            var windows = approval.WindowsEnvironment;
            return target.IdentityKey + "|" + approval.DwmProcessId.ToString(CultureInfo.InvariantCulture)
                + "|" + approval.DwmCreationFileTime.ToString(CultureInfo.InvariantCulture)
                + "|" + approval.SessionId.ToString(CultureInfo.InvariantCulture)
                + "|" + approval.SettingsKey + "|" + approval.PlanFingerprint
                + "|" + windows.OsBuildNumber.ToString(CultureInfo.InvariantCulture)
                + "|" + windows.UpdateBuildRevision.ToString(CultureInfo.InvariantCulture)
                + "|" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(windows.BuildLabEx))
                + "|" + windows.DwmExecutableSha256.ToUpperInvariant() + "|" + windows.DwmCoreSha256.ToUpperInvariant();
        }

        private static string FailureMessage(int code)
        {
            switch (code)
            {
                case 20: return "Kein ausreichend bestätigter SDR-Bypass erkannt. HDR und unbekannte Flächen werden nicht erzwungen.";
                case 12:
                case 21:
                case 22: return "Programm-, Fenster- oder DWM-Identität stimmt nicht mehr überein.";
                case 23: return "Die Freigabe über den überwachten Gamma-Kanal wurde nicht bestätigt.";
                case 24: return "Eine andere DWM-Hook-Version ist noch geladen. Eine neue DWM-Sitzung und ein erneuter globaler Test sind nötig; kein automatischer Neustart.";
                default: return "Der native SDR-Auswahltest wurde nicht vollständig bestätigt (Exitcode " + code + "). Details im Helferstatus prüfen; nicht automatisch erneut anwenden.";
            }
        }

        private void SetState(bool busy, bool active, string status)
        {
            lock (_gate)
            {
                if (_busy == busy && _active == active && _status == status) return;
                _busy = busy; _active = active; _status = status;
            }
            Changed();
        }

        private void SetSessionState(CancellationTokenSource session, bool busy, bool active, string status)
        {
            lock (_gate)
            {
                // Stop and activity adoption share this lock. A telemetry callback
                // that began before Stop cannot restore an active/busy indication.
                if (_disposed || _nativeWithdrawn || !ReferenceEquals(_session, session) || session.IsCancellationRequested) return;
                if (_busy == busy && _active == active && _status == status) return;
                _busy = busy; _active = active; _status = status;
            }
            Changed();
        }

        private void SetSessionWithdrawal(CancellationTokenSource session, string status)
        {
            lock (_gate)
            {
                if (_disposed || !ReferenceEquals(_session, session) || session.IsCancellationRequested || _nativeWithdrawn) return;
                _nativeWithdrawn = true;
                _busy = true; _active = false; _status = status;
            }
            Changed();
        }

        private void Changed() => StateChanged?.Invoke(this, EventArgs.Empty);

        public void Stop()
        {
            lock (_gate)
            {
                _proof = null;
                if (_session == null) return;
                _busy = true;
                _active = false;
                _status = "Erzwingen wird gestoppt; das Programm läuft weiter …";
                _session.Cancel();
            }
            Changed();
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            Stop();
        }
    }
}
