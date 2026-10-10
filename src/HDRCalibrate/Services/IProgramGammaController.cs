using System;
using System.Threading.Tasks;

namespace HDRCalibrate.Services
{
    /// <summary>
    /// Owns one explicitly requested gamma session for an already running application.
    /// The controller does not launch applications or activate saved profile preferences.
    /// </summary>
    public interface IProgramGammaController : IDisposable
    {
        event EventHandler StateChanged;
        bool IsBusy { get; }
        bool IsActive { get; }
        string Status { get; }
        string ActiveExecutablePath { get; }
        /// <summary>Advisory reason why no session can start on this system; null if none is known.</summary>
        string UnavailableReason { get; }
        Task TestAsync(string executablePath);
        Task ActivateAsync(string executablePath);
        void Stop();
    }

    // Used by standalone window hosts that have no active main-window gamma session.
    internal sealed class UnavailableProgramGammaController : IProgramGammaController
    {
        public event EventHandler StateChanged { add { } remove { } }
        public bool IsBusy => false;
        public bool IsActive => false;
        public string Status => "Gamma-Erzwingen ist in diesem Fenster nicht verfügbar. Die Programmliste über das Hauptfenster öffnen.";
        public string ActiveExecutablePath => null;
        public string UnavailableReason => Status;
        public Task TestAsync(string executablePath) => Unavailable();
        public Task ActivateAsync(string executablePath) => Unavailable();
        public void Stop() { }
        public void Dispose() { }

        private Task Unavailable() => Task.FromException(new InvalidOperationException(Status));
    }
}
