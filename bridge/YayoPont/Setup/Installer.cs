using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace YayoPont.Setup
{
    /// <summary>
    /// Installation sans droits administrateur, entièrement dans le profil :
    /// copie dans %LOCALAPPDATA%\YayoPont, démarrage avec la session, entrée
    /// dans « Applications installées ». Yayo double-clique, et c'est tout.
    /// </summary>
    internal static class Installer
    {
        public const string SiteUrl = "https://yayoextractor.louis-nectoux.fr/#telephone";
        public const string MutexName = @"Local\YayoPont";
        public const string QuitEventName = @"Local\YayoPont.Quit";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "YayoPont";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YayoPont";

        public static string InstallDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YayoPont");

        public static string InstalledExe => Path.Combine(InstallDir, "YayoPont.exe");

        public static string CurrentExe => Assembly.GetExecutingAssembly().Location;

        public static bool RunningFromInstallDir =>
            string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        /// <summary>Installe (ou met à jour), lance le pont en arrière-plan et ouvre le site.</summary>
        public static void Install()
        {
            StopRunning();
            Directory.CreateDirectory(InstallDir);
            // Réécrire les octets plutôt que copier : la copie emporterait la marque
            // « téléchargé d'internet » et Windows redemanderait au démarrage.
            var bytes = File.ReadAllBytes(CurrentExe);
            var temp = InstalledExe + ".new";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(InstalledExe)) File.Delete(InstalledExe);
            File.Move(temp, InstalledExe);

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.SetValue(RunValue, $"\"{InstalledExe}\" --background");
            using (var entry = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                entry.SetValue("DisplayName", "Pont Yayo");
                entry.SetValue("DisplayVersion", Version);
                entry.SetValue("Publisher", "Yayo Extractor");
                entry.SetValue("DisplayIcon", InstalledExe);
                entry.SetValue("InstallLocation", InstallDir);
                entry.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
                entry.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --silencieux");
                entry.SetValue("URLInfoAbout", "https://yayoextractor.louis-nectoux.fr/");
                entry.SetValue("NoModify", 1, RegistryValueKind.DWord);
                entry.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                entry.SetValue("EstimatedSize", Math.Max(1, bytes.Length / 1024), RegistryValueKind.DWord);
            }
            Log.Info("installé en version " + Version);
            Process.Start(new ProcessStartInfo(InstalledExe, "--background") { UseShellExecute = false, WorkingDirectory = InstallDir });
            OpenSite();
        }

        public static void Uninstall(bool silent)
        {
            StopRunning();
            Registry.CurrentUser.OpenSubKey(RunKey, true)?.DeleteValue(RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
            Log.Info("désinstallé");
            // L'exe ne peut pas s'effacer lui-même pendant qu'il tourne : cmd s'en
            // charge deux secondes après sa fermeture.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Path.GetTempPath(),
            });
            if (!silent)
                MessageBox.Show("Le Pont Yayo a bien été désinstallé.", "Pont Yayo", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void OpenSite()
        {
            try { Process.Start(new ProcessStartInfo(SiteUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("ouverture du site", ex); }
        }

        /// <summary>Demande à l'instance en cours de s'arrêter et attend qu'elle libère le mutex.</summary>
        private static void StopRunning()
        {
            if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit)) return;
            using (quit) quit.Set();
            for (int i = 0; i < 50; i++)
            {
                if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return;
                mutex.Dispose();
                Thread.Sleep(100);
            }
        }
    }
}
