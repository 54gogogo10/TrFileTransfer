using System;
using System.IO;

namespace TrFileTransfer
{
    public class TransferProgress
    {
        public long BytesTransferred { get; set; }
        public long TotalBytes { get; set; }
        public double SpeedBytesPerSecond { get; set; }
        public TimeSpan Elapsed { get; set; }
        public string FileName { get; set; }
    }

    public static class Utils
    {
        public static string FormatSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            double size = bytes;
            while (size >= 1024 && idx < suffixes.Length - 1)
            {
                size /= 1024;
                idx++;
            }
            return string.Format("{0:F1} {1}", size, suffixes[idx]);
        }

        public static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static void LogTo(Action<string> handler, string msg)
        {
            if (handler != null)
                handler(string.Format("[{0:HH:mm:ss}] {1}", DateTime.Now, msg));
        }

        public static string SanitizeRelativePath(string path)
        {
            path = path.Replace('\\', '/').TrimStart('/');
            var parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] == ".." || parts[i] == ".")
                    parts[i] = "_";
                if (string.IsNullOrWhiteSpace(parts[i]))
                    parts[i] = "_";
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        public static string GetUniqueSavePath(string directory, string name)
        {
            string savePath = Path.Combine(directory, name);
            int counter = 1;
            string baseName = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            while (File.Exists(savePath) || Directory.Exists(savePath))
            {
                savePath = Path.Combine(directory,
                    string.Format("{0}_{1}{2}", baseName, counter, ext));
                counter++;
            }
            return savePath;
        }
    }
}
