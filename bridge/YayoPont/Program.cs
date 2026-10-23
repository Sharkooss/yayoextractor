using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Windows.Forms;
using YayoPont.Drives;
using YayoPont.Http;
using YayoPont.Phone;
using YayoPont.Setup;

namespace YayoPont
{
    /// <summary>
    /// Sans argument, hors de son dossier d'installation : s'installe (double-clic
    /// sur le fichier téléchargé). Avec --background : tourne en silence.
    /// --uninstall : se retire. --faux-telephone/--fausse-cle : mode simulation.
    /// </summary>
    internal static class Program
    {
        public const int Port = 47863;

        [STAThread]
        private static int Main(string[] args)
        {
            var phoneDir = Option(args, "--faux-telephone");
            var driveDir = Option(args, "--fausse-cle");
            bool simulation = phoneDir != null || driveDir != null;
            bool background = args.Contains("--background");

            try
            {
                if (args.Contains("--uninstall"))
                {
                    Installer.Uninstall(args.Contains("--silencieux"));
                    return 0;
                }
                if (!simulation && !background && !Installer.RunningFromInstallDir)
                {
                    Installer.Install();
                    return 0;
                }
                return Serve(simulation, background, phoneDir, driveDir);
            }
            catch (Exception ex)
            {
                Log.Error("arrêt inattendu", ex);
                if (!background)
                    MessageBox.Show("Le Pont Yayo a rencontré un problème :\n" + ex.Message, "Pont Yayo",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static int Serve(bool simulation, bool background, string phoneDir, string driveDir)
        {
            var suffix = simulation ? ".Simulation" : "";
            using (var mutex = new Mutex(true, Installer.MutexName + suffix, out bool first))
            {
                if (!first)
                {
                    // Déjà en route : un double-clic sur l'exe installé ouvre juste le site.
                    if (!background) Installer.OpenSite();
                    return 0;
                }
                using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, Installer.QuitEventName + suffix))
                using (IPhoneSource phone = phoneDir != null ? (IPhoneSource)new FolderPhoneSource(phoneDir) : new WpdPhoneSource())
                {
                    IDriveProvider drives = driveDir != null ? (IDriveProvider)new FakeDriveProvider(driveDir) : new RealDriveProvider();
                    var security = new Security(Port);
                    var api = new Api(security, phone, drives, Installer.Version);
                    var server = new HttpServer(Port, api.Handle);
                    try
                    {
                        server.Start();
                    }
                    catch (SocketException ex)
                    {
                        Log.Error("port " + Port + " occupé", ex);
                        if (!background)
                            MessageBox.Show($"Le Pont Yayo ne peut pas démarrer : le port {Port} est déjà utilisé par un autre programme.",
                                "Pont Yayo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return 2;
                    }
                    Log.Info($"pont {Installer.Version} prêt sur 127.0.0.1:{Port}" + (simulation ? " (simulation)" : ""));
                    if (!background && !simulation) Installer.OpenSite();
                    quit.WaitOne();
                    server.Dispose();
                    Log.Info("arrêt demandé");
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        private static string Option(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
