using HDRCalibrate.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace HDRCalibrate.Services
{
    [Serializable]
    public sealed class PersistedAppState
    {
        public List<PersistedDisplayState> Displays { get; set; } = new List<PersistedDisplayState>();
    }

    [Serializable]
    public sealed class PersistedDisplayState
    {
        public string DisplayKey { get; set; }
        public string OriginalProfileName { get; set; }
        public int OriginalProfileScope { get; set; }
        public string LastAppliedProfileName { get; set; }
        public CalibrationSettings Settings { get; set; } = new CalibrationSettings();
        public DisplayColorMetadata BaseMetadata { get; set; } = new DisplayColorMetadata();
    }

    public sealed class ProfileStateStore
    {
        private readonly string _statePath;
        private readonly XmlSerializer _serializer = new XmlSerializer(typeof(PersistedAppState));

        public ProfileStateStore(string baseDirectory = null)
        {
            var root = baseDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRCalibrate");
            _statePath = Path.Combine(root, "state.xml");
        }

        public string BaseDirectory => Path.GetDirectoryName(_statePath);
        public string ProfilesDirectory => Path.Combine(BaseDirectory, "Profiles");

        public PersistedAppState Load()
        {
            if (!File.Exists(_statePath)) return new PersistedAppState();
            try
            {
                using (var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    return (PersistedAppState)_serializer.Deserialize(stream) ?? new PersistedAppState();
                }
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Der gespeicherte Wiederherstellungszustand ist beschädigt.", ex);
            }
        }

        public PersistedDisplayState Get(string displayKey)
        {
            return Load().Displays.FirstOrDefault(item => string.Equals(item.DisplayKey, displayKey, StringComparison.Ordinal));
        }

        public void Upsert(PersistedDisplayState displayState)
        {
            var state = Load();
            var index = state.Displays.FindIndex(item => string.Equals(item.DisplayKey, displayState.DisplayKey, StringComparison.Ordinal));
            if (index >= 0) state.Displays[index] = displayState;
            else state.Displays.Add(displayState);
            Save(state);
        }

        public void Remove(string displayKey)
        {
            var state = Load();
            state.Displays.RemoveAll(item => string.Equals(item.DisplayKey, displayKey, StringComparison.Ordinal));
            Save(state);
        }

        private void Save(PersistedAppState state)
        {
            Directory.CreateDirectory(BaseDirectory);
            var temporaryPath = _statePath + ".tmp";
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                _serializer.Serialize(stream, state);
                stream.Flush(true);
            }

            if (File.Exists(_statePath))
            {
                var backupPath = _statePath + ".bak";
                File.Replace(temporaryPath, _statePath, backupPath, true);
            }
            else
            {
                File.Move(temporaryPath, _statePath);
            }
        }
    }
}
