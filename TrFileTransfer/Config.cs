using System;
using System.Collections.Generic;
using System.IO;

namespace TrFileTransfer
{
    /// <summary>Simple key-value configuration persisted to %AppData%\TrFileTransfer\config.ini.</summary>
    #pragma warning disable 1591
    public static class Config
    {
        private static readonly string _dir;
        private static readonly string _path;
        private static readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        static Config()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrFileTransfer");
            _path = Path.Combine(_dir, "config.ini");
        }

        public static void Load()
        {
            _values.Clear();
            try
            {
                if (!File.Exists(_path)) return;
                foreach (var line in File.ReadAllLines(_path))
                {
                    int idx = line.IndexOf('=');
                    if (idx <= 0 || idx >= line.Length - 1) continue;
                    _values[line.Substring(0, idx)] = line.Substring(idx + 1);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(_dir);
                var lines = new string[_values.Count];
                int i = 0;
                foreach (var kv in _values)
                    lines[i++] = kv.Key + "=" + kv.Value;
                File.WriteAllLines(_path, lines);
            }
            catch { }
        }

        public static string Get(string key, string fallback)
        {
            string val;
            return _values.TryGetValue(key, out val) ? val : fallback;
        }

        public static int GetInt(string key, int fallback)
        {
            string val;
            if (!_values.TryGetValue(key, out val)) return fallback;
            int result;
            return int.TryParse(val, out result) ? result : fallback;
        }

        public static bool GetBool(string key, bool fallback)
        {
            string val;
            if (!_values.TryGetValue(key, out val)) return fallback;
            return val == "true" || val == "1";
        }

        public static void Set(string key, string val) { _values[key] = val; }
        public static void SetInt(string key, int val) { _values[key] = val.ToString(); }
        public static void SetBool(string key, bool val) { _values[key] = val ? "true" : "false"; }
    }
}
