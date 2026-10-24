using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace YayoPont.Setup
{
    internal interface IFolderPicker
    {
        /// <summary>Chemin choisi, ou null si la fenêtre a été fermée sans choisir.</summary>
        string Pick();
    }

    /// <summary>
    /// Sélecteur de dossier Windows (celui de l'Explorateur, IFileOpenDialog en
    /// mode dossier), ouvert au premier plan alors que le pont n'a pas de fenêtre :
    /// une fenêtre propriétaire invisible, toujours au-dessus, prend le premier
    /// plan avant d'ouvrir le dialogue. Repli : l'ancien FolderBrowserDialog.
    /// </summary>
    internal sealed class WindowsFolderPicker : IFolderPicker
    {
        private const uint FosNoChangeDir = 0x8;
        private const uint FosPickFolders = 0x20;
        private const uint FosForceFileSystem = 0x40;
        private const uint FosPathMustExist = 0x800;
        private const uint SigdnFileSysPath = 0x80058000;
        private const int ErrorCancelled = unchecked((int)0x800704C7);
        private static readonly Guid ClientGuid = new Guid("5b0d3c1e-7a51-4c55-9b8e-59a7f0c1d2a4");

        public string Pick()
        {
            string result = null;
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { result = PickOnSta(); }
                catch (Exception ex) { error = ex; }
            })
            { IsBackground = true, Name = "choix-dossier" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) throw new InvalidOperationException("Sélecteur de dossier : " + error.Message, error);
            return result;
        }

        private static string PickOnSta()
        {
            var screen = Screen.PrimaryScreen.WorkingArea;
            using (var owner = new Form
            {
                Text = "Pont Yayo",
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1),
                Location = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 3),
                Opacity = 0,
            })
            {
                owner.Show();
                BringToFront(owner.Handle);
                try
                {
                    return Modern(owner.Handle);
                }
                catch (Exception ex) when (ex is COMException || ex is InvalidCastException)
                {
                    Log.Error("sélecteur de dossier moderne", ex);
                    using (var classic = new FolderBrowserDialog { Description = "Choisis le dossier où copier les photos", ShowNewFolderButton = true })
                        return classic.ShowDialog(owner) == DialogResult.OK ? classic.SelectedPath : null;
                }
            }
        }

        private static string Modern(IntPtr owner)
        {
            var dialog = (IFileOpenDialog)new FileOpenDialogCoClass();
            try
            {
                dialog.GetOptions(out uint options);
                dialog.SetOptions(options | FosPickFolders | FosForceFileSystem | FosPathMustExist | FosNoChangeDir);
                dialog.SetTitle("Choisis le dossier où copier les photos");
                dialog.SetOkButtonLabel("Choisir ce dossier");
                var guid = ClientGuid;
                dialog.SetClientGuid(ref guid);
                int hr = dialog.Show(owner);
                if (hr == ErrorCancelled) return null;
                Marshal.ThrowExceptionForHR(hr);
                Marshal.ThrowExceptionForHR(dialog.GetResult(out var item));
                try
                {
                    Marshal.ThrowExceptionForHR(item.GetDisplayName(SigdnFileSysPath, out var name));
                    try { return Marshal.PtrToStringUni(name); }
                    finally { Marshal.FreeCoTaskMem(name); }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        /// <summary>
        /// Windows refuse le premier plan à un programme d'arrière-plan : on
        /// s'attache le temps d'un appel au fil de la fenêtre active (le navigateur).
        /// </summary>
        private static void BringToFront(IntPtr window)
        {
            var foreground = GetForegroundWindow();
            uint foregroundThread = GetWindowThreadProcessId(foreground, IntPtr.Zero);
            uint me = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != me && AttachThreadInput(me, foregroundThread, true);
            try
            {
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(me, foregroundThread, false);
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool on);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass { }
    }

    // Vtables de ShObjIdl_core.h : IModalWindow, puis IFileDialog, puis IFileOpenDialog,
    // dans l'ordre exact. Aucun paramètre tableau (voir le piège WPD du README).
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        [PreserveSig] int SetFileTypes(uint count, IntPtr filterSpec);
        [PreserveSig] int SetFileTypeIndex(uint index);
        [PreserveSig] int GetFileTypeIndex(out uint index);
        [PreserveSig] int Advise(IntPtr events, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOptions(uint options);
        [PreserveSig] int GetOptions(out uint options);
        [PreserveSig] int SetDefaultFolder(IShellItem item);
        [PreserveSig] int SetFolder(IShellItem item);
        [PreserveSig] int GetFolder(out IShellItem item);
        [PreserveSig] int GetCurrentSelection(out IShellItem item);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetFileName(out IntPtr name);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        [PreserveSig] int GetResult(out IShellItem item);
        [PreserveSig] int AddPlace(IShellItem item, int place);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr filter);
        [PreserveSig] int GetResults(out IntPtr items);
        [PreserveSig] int GetSelectedItems(out IntPtr items);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
}
