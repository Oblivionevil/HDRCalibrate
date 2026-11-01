using HDRCalibrate.Core;
using System;
using System.IO;
using System.Xml;
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
                if (new FileInfo(_statePath).Length > 64 * 1024)
                    throw new InvalidDataException("Der gespeicherte DWM-Zustand besitzt eine ungültige Größe.");
                using (var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                }))
                {
                    return (PersistedDwmState)_serializer.Deserialize(reader);
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Der gespeicherte DWM-Zustand ist beschädigt.", ex);
            }
            catch (XmlException ex)
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
            foreach (var path in new[] { _statePath, _statePath + ".tmp", _statePath + ".bak" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
