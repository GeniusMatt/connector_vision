using System;
using System.IO;
using System.Runtime.Serialization.Json;
using Connector_Vision.Models;

namespace Connector_Vision.Helpers
{
    public class SettingsManager
    {
        private readonly string _filePath;
        private readonly string _modelsDir;

        public SettingsManager()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "inspection_settings.json");
            _modelsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models");
        }

        public InspectionSettings Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                    return new InspectionSettings();

                var json = File.ReadAllBytes(_filePath);
                var serializer = new DataContractJsonSerializer(typeof(InspectionSettings),
                    new DataContractJsonSerializerSettings
                    {
                        KnownTypes = new[] { typeof(MeasurementLine) }
                    });
                using (var stream = new MemoryStream(json))
                {
                    return (InspectionSettings)serializer.ReadObject(stream);
                }
            }
            catch
            {
                return new InspectionSettings();
            }
        }

        public void Save(InspectionSettings settings)
        {
            var serializer = new DataContractJsonSerializer(typeof(InspectionSettings),
                new DataContractJsonSerializerSettings
                {
                    KnownTypes = new[] { typeof(MeasurementLine) }
                });
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, settings);
                var json = stream.ToArray();
                File.WriteAllBytes(_filePath, json);
            }
        }

        public string[] GetModelNames()
        {
            if (!Directory.Exists(_modelsDir))
                return new string[0];

            var dirs = Directory.GetDirectories(_modelsDir);
            var names = new string[dirs.Length];
            for (int i = 0; i < dirs.Length; i++)
                names[i] = Path.GetFileName(dirs[i]);
            return names;
        }

        public InspectionSettings LoadModel(string name)
        {
            try
            {
                string file = Path.Combine(_modelsDir, name, "settings.json");
                if (!File.Exists(file))
                    return null;

                var json = File.ReadAllBytes(file);
                var serializer = new DataContractJsonSerializer(typeof(InspectionSettings),
                    new DataContractJsonSerializerSettings
                    {
                        KnownTypes = new[] { typeof(MeasurementLine) }
                    });
                using (var stream = new MemoryStream(json))
                {
                    return (InspectionSettings)serializer.ReadObject(stream);
                }
            }
            catch
            {
                return null;
            }
        }

        public void SaveModel(string name, InspectionSettings settings)
        {
            string dir = Path.Combine(_modelsDir, name);
            Directory.CreateDirectory(dir);

            string file = Path.Combine(dir, "settings.json");
            var serializer = new DataContractJsonSerializer(typeof(InspectionSettings),
                new DataContractJsonSerializerSettings
                {
                    KnownTypes = new[] { typeof(MeasurementLine) }
                });
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, settings);
                File.WriteAllBytes(file, stream.ToArray());
            }
        }

        public void DeleteModel(string name)
        {
            string dir = Path.Combine(_modelsDir, name);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }

        public string GetModelDirectory(string name)
        {
            return Path.Combine(_modelsDir, name);
        }
    }
}
