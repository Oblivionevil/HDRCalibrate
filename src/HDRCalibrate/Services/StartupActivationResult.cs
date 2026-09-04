namespace HDRCalibrate.Services
{
    public sealed class StartupActivationResult
    {
        public StartupActivationResult(
            StartupActivationDecision decision,
            bool attempted,
            bool success,
            string message)
        {
            Decision = decision;
            Attempted = attempted;
            Success = success;
            Message = message ?? string.Empty;
        }

        public StartupActivationDecision Decision { get; }
        public bool Attempted { get; }
        public bool Success { get; }
        public string Message { get; }
    }
}
