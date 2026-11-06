using System.Runtime.InteropServices;

namespace LockPeek.Windows;

public static class RecycleBin
{
    public static Task<IReadOnlyList<RecycleOutcome>> MoveAsync(IReadOnlyList<RecycleTarget> confirmed, IntPtr owner,
        CancellationToken cancellation = default, Action? onDeleting = null)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<RecycleOutcome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var outcomes = new Dictionary<string, RecycleOutcome>(StringComparer.OrdinalIgnoreCase);
            var items = new List<IShellItem>();
            var sinks = new List<RecycleSink>();
            IFileOperation? operation = null;
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"))!)!;
                Check(operation.SetOwnerWindow(owner));
                // Recycle when possible and ask before permanently deleting an item.
                Check(operation.SetOperationFlags(0x00080000 | 0x20000000 | 0x00004000));
                foreach (var target in confirmed)
                {
                    cancellation.ThrowIfCancellationRequested();
                                        var fresh = RecyclePolicy.Inspect(target.Path, cancellation);
                    if (fresh.Volume != target.Volume || fresh.FileId != target.FileId)
                        throw new IOException("An item was moved or replaced since confirmation. Review the selection again.");
                    Guid iid = typeof(IShellItem).GUID;
                    Check(SHCreateItemFromParsingName(target.Path, IntPtr.Zero, ref iid, out var item));
                    items.Add(item);
                    var sink = new RecycleSink(target, outcomes, cancellation);
                    sinks.Add(sink);
                    Check(operation.DeleteItem(item, sink));
                }
                cancellation.ThrowIfCancellationRequested();
                onDeleting?.Invoke();
                cancellation.ThrowIfCancellationRequested();
                int result = operation.PerformOperations();
                Check(operation.GetAnyOperationsAborted(out bool aborted));
                foreach (var target in confirmed)
                    if (!outcomes.ContainsKey(target.Path))
                        outcomes[target.Path] = new(target.Path, false, aborted ? "Cancelled or not attempted" : result < 0 ? Marshal.GetExceptionForHR(result)?.Message ?? "Delete failed" : "No delete result was reported");
            }
            catch (OperationCanceledException)
            {
                foreach (var target in confirmed)
                    if (!outcomes.ContainsKey(target.Path)) outcomes[target.Path] = new(target.Path, false, "Cancelled before deletion");
            }
            catch (Exception error)
            {
                foreach (var target in confirmed)
                    if (!outcomes.ContainsKey(target.Path)) outcomes[target.Path] = new(target.Path, false, error.Message);
            }
            finally
            {
                foreach (var item in items) Release(item);
                if (operation is not null) Release(operation);
                completion.TrySetResult(confirmed.Select(t => outcomes.TryGetValue(t.Path, out var outcome)
                    ? outcome : new RecycleOutcome(t.Path, false, "No delete result was reported")).ToArray());
            }
        }) { IsBackground = true, Name = "LockPeek delete" };
        try { thread.SetApartmentState(ApartmentState.STA); thread.Start(); }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void Release(object item)
    {
        try { Marshal.FinalReleaseComObject(item); }
        catch (Exception error) when (error is COMException or InvalidComObjectException) { }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink(RecycleTarget target, Dictionary<string, RecycleOutcome> outcomes,
        CancellationToken cancellation) : IFileOperationProgressSink
    {
        private readonly string path = target.Path;
        public int PreDeleteItem(uint flags, IShellItem item)
        {
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (RecyclePolicy.Inspect(path, cancellation) != target)
                    throw new IOException("The selected item changed. Review it again.");
                return 0;
            }
            catch (Exception error)
            {
                outcomes[path] = new(path, false, error is OperationCanceledException ? "Cancelled" : error.Message);
                return unchecked((int)0x80004004);
            }
        }
        public int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? recycledItem)
        {
            // Windows can return a nonzero success code when it supplies the item in the Recycle Bin.
            bool recycled = result >= 0 && recycledItem is not null;
            bool deleted = recycled || result == 0;
            string message = recycled ? "Moved to Recycle Bin" : result < 0 ? Marshal.GetExceptionForHR(result)?.Message ?? "Delete failed" : deleted ? "Deleted; Recycle Bin destination was not confirmed" : "Deletion was not confirmed";
            if (deleted || !outcomes.ContainsKey(path)) outcomes[path] = new(path, recycled, message, deleted);
            return 0;
        }
        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IShellItem item, string name) => 0;
        public int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem) => 0;
        public int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name) => 0;
        public int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem) => 0;
        public int PreNewItem(uint flags, IShellItem destination, string name) => 0;
        public int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? newItem) => 0;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint kind, out IntPtr name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        void ApplyPropertiesToItem(IShellItem item);
        void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink? sink);
        void RenameItems([MarshalAs(UnmanagedType.IUnknown)] object items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        void MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void MoveItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        void CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string? name, IFileOperationProgressSink? sink);
        void CopyItems([MarshalAs(UnmanagedType.IUnknown)] object items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        void DeleteItems([MarshalAs(UnmanagedType.IUnknown)] object items);
        void NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string? template, IFileOperationProgressSink? sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int result);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, string name, int result, IShellItem? newItem);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, string? name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, string? name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, string? name, int result, IShellItem? newItem);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int result, IShellItem? newItem);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, string name, string? template, uint attributes, int result, IShellItem? newItem);
        [PreserveSig] int UpdateProgress(uint total, uint completed);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItem item);
}
