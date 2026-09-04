using HDRCalibrate.Core;
using System;
using System.IO;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    [Serializable]
    public sealed class PersistedDwmState
    {
        public bool WasApplied { get; set; }
        public int DwmProcessId { get; set; }
        public long DwmStartTimeUtcTicks { get; set; }
        public int SessionId { get; set; }
        public string DwmCoreVersion { get; set; }
        public DateTime AppliedUtc { get; set; }
        public string PatchPlanFingerprint { get; set; }
        public CalibrationSettings Settings { get; set; } = new CalibrationSettings();
    }

    public sealed class DwmStateStore
    {
        private readonly string _statePath;
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(PersistedDwmState));

        public DwmStateStore(string baseDirectory = null)
        {
            var root = baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRCalibrate");
            _statePath = Path.Combine(root, "dwm-state.xml");
        }

        public string StatePath => _statePath;

        public PersistedDwmState Load()
        {
            if (!File.Exists(_statePath)) return null;
            try
            {
                using (var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return (PersistedDwmState)_serializer.Deserialize(stream);
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Der gespeicherte DWM-Zustand ist beschädigt.", ex);
            }
        }

        public void Save(PersistedDwmState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Settings == null) throw new InvalidDataException("Im DWM-Zustand fehlen die Einstellungen.");
            state.Settings.Validate();

            var directory = Path.GetDirectoryName(_statePath);
            Directory.CreateDirectory(directory);
            var temporary = _statePath + ".tmp";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _serializer.Serialize(stream, state);
                stream.Flush(true);
            }

            if (File.Exists(_statePath))
                File.Replace(temporary, _statePath, _statePath + ".bak", true);
            else
                File.Move(temporary, _statePath);
        }

        public void Clear()
        {
            if (File.Exists(_statePath)) File.Delete(_statePath);
            var temporary = _statePath + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
