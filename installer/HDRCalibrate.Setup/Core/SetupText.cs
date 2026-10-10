using System.Globalization;

namespace HDRCalibrate.Setup.Core
{
    /// <summary>German for a German Windows display language, English otherwise.</summary>
    public sealed class SetupText
    {
        private readonly bool _german;

        public SetupText(bool german) { _german = german; }

        public static SetupText ForCurrentCulture() =>
            new SetupText(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de");

        public bool IsGerman => _german;

        private string T(string german, string english) => _german ? german : english;

        public string WindowTitle(string version) => T("HDR Calibrate {0} · Setup", "HDR Calibrate {0} · Setup").Replace("{0}", version);
        public string InstallHeading => T("HDR Calibrate installieren", "Install HDR Calibrate");
        public string UpdateHeading => T("HDR Calibrate aktualisieren", "Update HDR Calibrate");
        public string InstallIntro => T(
            "Installiert die Anwendung für den aktuellen Benutzer. Administratorrechte sind nicht erforderlich.",
            "Installs the application for the current user. Administrator rights are not required.");
        public string TargetDirectory(string path) => T("Zielordner: ", "Destination: ") + path;
        public string InstalledVersion(string version) => T("Installierte Version: ", "Installed version: ") + version;
        public string RunningAppNote => T(
            "Eine laufende HDR-Calibrate-Instanz wird vor dem Ersetzen der Dateien sauber beendet; es wird kein Prozess erzwungen beendet.",
            "A running HDR Calibrate instance is closed cleanly before any file is replaced; no process is force-terminated.");
        public string DesktopTask => T("Desktop-Verknüpfung erstellen", "Create a desktop shortcut");
        public string AutoStartTask => T("Mit Windows starten (im Tray)", "Start with Windows (in the notification area)");
        public string InstallButton => T("Installieren", "Install");
        public string UpdateButton => T("Aktualisieren", "Update");
        public string CancelButton => T("Abbrechen", "Cancel");
        public string FinishButton => T("Fertig", "Finish");
        public string CloseButton => T("Schließen", "Close");
        public string UninstallButton => T("Entfernen", "Remove");

        public string ProgressVerifying => T("Installationspaket wird geprüft …", "Verifying the installation package …");
        public string ProgressStopping => T("HDR Calibrate wird beendet …", "Closing HDR Calibrate …");
        public string ProgressCopying => T("Dateien werden installiert …", "Installing files …");
        public string ProgressRegistering => T("Verknüpfungen und Autostart werden eingerichtet …", "Setting up shortcuts and startup …");
        public string ProgressRemoving => T("Dateien werden entfernt …", "Removing files …");

        public string InstallDone(string version) => T("HDR Calibrate {0} wurde installiert.", "HDR Calibrate {0} has been installed.").Replace("{0}", version);
        public string LaunchApp => T("HDR Calibrate jetzt starten", "Start HDR Calibrate now");
        public string UninstallHeading => T("HDR Calibrate entfernen", "Remove HDR Calibrate");
        public string UninstallIntro(string path) => T(
            "HDR Calibrate wird aus folgendem Ordner entfernt:\n",
            "HDR Calibrate will be removed from:\n") + path;
        public string UninstallKeepsSettings => T(
            "Ihre Einstellungen und Freigaben unter %LOCALAPPDATA%\\HDRCalibrate bleiben erhalten.",
            "Your settings and approvals in %LOCALAPPDATA%\\HDRCalibrate are kept.");
        public string UninstallDone => T("HDR Calibrate wurde entfernt.", "HDR Calibrate has been removed.");
        public string RemainingFiles(string path) => T(
            "Der Ordner enthält weitere, nicht vom Setup stammende Dateien und wurde nicht gelöscht: ",
            "The folder contains other files that were not installed by Setup and was not deleted: ") + path;
        public string NotInstalled => T("HDR Calibrate ist für diesen Benutzer nicht installiert.", "HDR Calibrate is not installed for this user.");

        public string ErrorHeading => T("Vorgang nicht abgeschlossen", "Operation not completed");
        public string InstallFailed(string reason) => T("Die Installation ist fehlgeschlagen: ", "Installation failed: ") + reason;
        public string UninstallFailed(string reason) => T("Die Deinstallation ist fehlgeschlagen: ", "Uninstallation failed: ") + reason;
        public string RolledBack => T(
            "Alle Änderungen an den Anwendungsdateien wurden zurückgenommen.",
            "All changes to the application files were rolled back.");
        public string LogLocation(string path) => T("Protokoll: ", "Log: ") + path;

        public string DotNetMissing => T(
            "Microsoft .NET Framework 4.8.1 oder neuer wird benötigt. Installieren Sie es und starten Sie das Setup erneut.",
            "Microsoft .NET Framework 4.8.1 or later is required. Install it and run Setup again.");
        public string AlreadyRunning => T("Das HDR-Calibrate-Setup wird bereits ausgeführt.", "HDR Calibrate Setup is already running.");
        public string InvalidArguments => T("Unbekannte Setup-Parameter. Unterstützt wird nur --uninstall.", "Unknown Setup arguments. Only --uninstall is supported.");
        public string PayloadMissing => T(
            "Das Installationspaket ist unvollständig oder beschädigt. Bitte das Setup erneut herunterladen.",
            "The installation package is incomplete or damaged. Please download Setup again.");
        public string StopStartFailed => T(
            "Die laufende HDR-Calibrate-Instanz konnte nicht angesprochen werden. Das Setup hat keine Anwendungsdateien geändert.",
            "The running HDR Calibrate instance could not be contacted. Setup did not change any application files.");
        public string StopTimedOut => T(
            "HDR Calibrate wurde nicht rechtzeitig beendet. Beenden Sie die Anwendung manuell und versuchen Sie es erneut. Es wurde kein Prozess erzwungen beendet.",
            "HDR Calibrate did not close within the allowed time. Close it manually and try again. No process was force-terminated.");
        public string StopBusy => T(
            "HDR Calibrate wird noch ausgeführt. Beenden Sie die Anwendung manuell und versuchen Sie es erneut. Es wurde kein Prozess erzwungen beendet.",
            "HDR Calibrate is still running. Close it manually and try again. No process was force-terminated.");
        public string StopFailed(int code) => T(
            "HDR Calibrate konnte nicht sauber beendet werden (Exitcode {0}). Das Setup hat keine Anwendungsdateien geändert.",
            "HDR Calibrate could not be closed cleanly (exit code {0}). Setup did not change any application files.")
            .Replace("{0}", code.ToString(CultureInfo.InvariantCulture));
        public string NativeVersionMismatch => T(
            "Die vorhandene native Gamma-Datei dieser Version weicht vom Paket ab oder ist nicht prüfbar. Das Setup hat keine Anwendungsdateien geändert. Ein anderer nativer Build benötigt ein eigenes Versionsverzeichnis.",
            "The existing native gamma file for this version differs from the package or cannot be verified. Setup has not changed any application files. A different native build requires its own version directory.");
        public string NativeHookRetained(string directories) => T(
            "Die native Gamma-DLL konnte nicht gelöscht werden und bleibt mit ihrer MinHook-Lizenz in:\n{0}\nSie kann noch von Windows geladen sein. Keine Windows-Komponente wurde beendet und kein Neustart angefordert.",
            "The native gamma DLL could not be deleted and remains with its MinHook license in:\n{0}\nIt may still be loaded by Windows. No Windows component was stopped and no restart was requested.")
            .Replace("{0}", directories);
        public string InstallDirectoryInvalid => T(
            "Der Installationsordner ist ungültig oder eine Verknüpfung (Reparse-Punkt).",
            "The installation folder is invalid or a link (reparse point).");
    }
}
