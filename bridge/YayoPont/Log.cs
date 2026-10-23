using System;
using System.IO;

namespace YayoPont
{
    /// <summary>
    /// Journal dans %LOCALAPPDATA%\YayoPont\pont.log, plafonné à 1 Mo : sans
    /// console, c'est la seule trace pour comprendre un souci chez Yayo.
    /// </summary>
    internal static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YayoPont", "pont.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception ex) => Write("ERREUR", message + " : " + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 1024 * 1024)
                    {
                        File.Delete(FilePath + ".old");
                        File.Move(FilePath, FilePath + ".old");
                    }
                    File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
