namespace HDRCalibrate.Services
{
    public sealed class ExitRequestGate
    {
        public bool IsPending { get; private set; }

        public bool Request(bool operationIsBusy)
        {
            if (!operationIsBusy) return true;
            IsPending = true;
            return false;
        }

        public bool CanComplete(bool operationIsBusy)
        {
            return IsPending && !operationIsBusy;
        }

        public void Complete()
        {
            IsPending = false;
        }
    }
}
