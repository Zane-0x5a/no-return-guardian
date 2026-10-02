using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace NoReturnGuardian
{
    /// <summary>
    /// 系统的新式文件夹选择框（IFileOpenDialog + FOS_PICKFOLDERS），和资源管理器一样带地址栏与快速访问。
    /// 新式对话框不可用时退回 WinForms 的树形对话框。
    /// </summary>
    internal static class FolderPicker
    {
        private const uint PickFolders = 0x20;
        private const uint ForceFileSystem = 0x40;
        private const uint PathMustExist = 0x800;
        private const uint FileSystemPath = 0x80058000;
        private const int Cancelled = unchecked((int)0x800704C7);

        /// <summary>返回选中的目录；玩家取消时返回 null。</summary>
        public static string Pick(IWin32Window owner, string title, string initialPath)
        {
            try
            {
                return PickModern(owner, title, initialPath);
            }
            catch (Exception error) when (error is COMException || error is InvalidCastException)
            {
                using (var dialog = new FolderBrowserDialog
                {
                    Description = title,
                    SelectedPath = initialPath ?? string.Empty,
                    ShowNewFolderButton = false
                })
                {
                    return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.SelectedPath : null;
                }
            }
        }

        private static string PickModern(IWin32Window owner, string title, string initialPath)
        {
            var dialog = (IFileDialog)new FileOpenDialogClass();
            try
            {
                uint options;
                dialog.GetOptions(out options);
                dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
                dialog.SetTitle(title);
                if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
                {
                    Guid shellItem = typeof(IShellItem).GUID;
                    IShellItem folder;
                    if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref shellItem, out folder) == 0)
                    {
                        dialog.SetFolder(folder);
                    }
                }

                int result = dialog.Show(owner == null ? IntPtr.Zero : owner.Handle);
                if (result == Cancelled)
                {
                    return null;
                }

                Marshal.ThrowExceptionForHR(result);
                IShellItem picked;
                dialog.GetResult(out picked);
                IntPtr name;
                picked.GetDisplayName(FileSystemPath, out name);
                try
                {
                    return Marshal.PtrToStringUni(name);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(name);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(dialog);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            string path, IntPtr bindContext, [In] ref Guid interfaceId, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogClass
        {
        }

        // 方法顺序即 vtable 顺序（IModalWindow::Show 在前），不能调换。
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr filters);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int placement);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int result);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, out IntPtr name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
