using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    /// --maj : installation silencieuse lancée par la mise à jour automatique.
    /// --uninstall : se retire. --faux-telephone (plusieurs fois = plusieurs
    /// téléphones) / --fausse-cle : mode simulation.
    /// </summary>
    internal static class Program
    {
        public const int Port = 47863;

        [STAThread]
        private static int Main(string[] args)
        {
            var phoneDirs = Options(args, "--faux-telephone");
            var driveDir = Options(args, "--fausse-cle").FirstOrDefault();
            bool simulation = phoneDirs.Count > 0 || driveDir != null;
            bool update = args.Contains("--maj");
            bool background = args.Contains("--background") || update;

            try
            {
                // GitHub (mise à jour) n'accepte que TLS 1.2 et plus.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                if (args.Contains("--uninstall"))
                {
                    Installer.Uninstall(args.Contains("--silencieux"));
                    return 0;
                }
                if (update)
                {
                    Installer.Install(openSite: false);
                    return 0;
                }
                if (!simulation && !background && !Installer.RunningFromInstallDir)
                {
                    Installer.Install();
                    return 0;
                }
                // --lent : la simulation lit à ~3 Mo/s, comme un vrai téléphone (pour voir l'animation).
                int simulatedSpeed = args.Contains("--lent") ? 3 * 1024 * 1024 : 0;
                return Serve(simulation, background, phoneDirs, driveDir, simulatedSpeed);
            }
            catch (Exception ex)
            {
                Log.Error(update ? "installation de la mise à jour" : "arrêt inattendu", ex);
                if (!background)
                    MessageBox.Show("Le Pont Yayo a rencontré un problème :\n" + ex.Message, "Pont Yayo",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static int Serve(bool simulation, bool background, IList<string> phoneDirs, string driveDir, int simulatedSpeed)
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
                using (IPhoneSource phone = phoneDirs.Count > 0 ? (IPhoneSource)new FolderPhoneSource(phoneDirs, simulatedSpeed) : new WpdPhoneSource())
                {
                    IDriveProvider drives = driveDir != null ? (IDriveProvider)new FakeDriveProvider(driveDir) : new RealDriveProvider();
                    var security = new Security(Port);
                    var api = new Api(security, phone, drives, Installer.Version);
                    // Pas de mise à jour en simulation, ni pour un exe lancé hors de son dossier d'installation.
                    var notAllowed = simulation ? "Pas de mise à jour en mode simulation."
                        : !Installer.RunningFromInstallDir ? "Ce pont n'est pas installé : pas de mise à jour automatique."
                        : null;
                    using (var updater = new Updater(Installer.Version, () => api.Busy, Path.Combine(Installer.InstallDir, "maj"), notAllowed))
                    {
                        api.Updater = updater;
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
                        updater.Start();
                        if (!background && !simulation) Installer.OpenSite();
                        quit.WaitOne();
                        server.Dispose();
                        Log.Info("arrêt demandé");
                    }
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        /// <summary>Toutes les valeurs d'une option répétable (« --faux-telephone A --faux-telephone B »).</summary>
        private static List<string> Options(string[] args, string name)
        {
            var values = new List<string>();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == name) values.Add(args[i + 1]);
            }
            return values;
        }
    }
}
