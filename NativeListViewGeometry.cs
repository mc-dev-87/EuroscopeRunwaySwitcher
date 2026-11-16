using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// The selector's UI Automation provider can recycle a DataItem while retaining
// an older BoundingRectangle.  This helper deliberately obtains both identity
// and geometry from the native report ListView instead.  EuroScope is an x86
// program, so the caller must compile the Manager as x86 too: LVITEM contains
// target-process pointers and cannot safely be marshalled between bitnesses.
internal static class NativeListViewGeometry
{
    internal enum Result
    {
        Unavailable,
        Success,
        Unsafe
    }

    private const int GWL_STYLE = -16;
    private const int LVS_TYPEMASK = 0x0003;
    private const int LVS_REPORT = 0x0001;
    private const int LVS_OWNERDATA = 0x1000;

    private const uint LVM_FIRST = 0x1000;
    private const uint LVM_GETITEMCOUNT = LVM_FIRST + 4;
    private const uint LVM_ENSUREVISIBLE = LVM_FIRST + 19;
    private const uint LVM_GETITEMTEXTA = LVM_FIRST + 45;
    private const uint LVM_GETSUBITEMRECT = LVM_FIRST + 56;
    private const uint LVM_SUBITEMHITTEST = LVM_FIRST + 57;
    private const uint LVM_GETITEMTEXTW = LVM_FIRST + 115;
    private const uint CCM_GETUNICODEFORMAT = 0x2006;

    private const uint LVIF_TEXT = 0x0001;
    private const int LVIR_BOUNDS = 0;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint MESSAGE_TIMEOUT_MS = 750;

    private const uint PROCESS_VM_OPERATION = 0x0008;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint PROCESS_VM_WRITE = 0x0020;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04;

    private const int TEXT_CHAR_CAPACITY = 128;
    private const int REMOTE_BLOCK_SIZE = 4096;
    private const int MAX_LIST_ITEMS = 4096;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(
        IntPtr hWnd,
        StringBuilder lpClassName,
        int nMaxCount);

    // This helper intentionally requires a 32-bit Manager, therefore
    // GetWindowLong is the correct pointer-width API here.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(
        IntPtr hWnd,
        out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    // LVM_GETSUBITEMRECT is an inter-process, DPI-dependent exchange.  Its
    // rectangle is expressed in the legacy ListView's client coordinate space.
    // The Manager is PerMonitorV2, so obtain the actual physical client extent
    // separately and convert by the measured per-axis scale.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(IntPtr hWnd, ref NativePoint lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SendMessageTimeout(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(
        IntPtr process,
        [MarshalAs(UnmanagedType.Bool)] out bool wow64Process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint allocationType,
        uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(
        IntPtr process,
        IntPtr address,
        UIntPtr size,
        uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        IntPtr size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [In] byte[] buffer,
        IntPtr size,
        out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    // Exact 32-bit layout of LVITEMW/LVITEMA.  The character encoding changes
    // the pointed-to text buffer only, not this structure's layout.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LVITEM32
    {
        internal uint mask;
        internal int iItem;
        internal int iSubItem;
        internal uint state;
        internal uint stateMask;
        internal IntPtr pszText;
        internal int cchTextMax;
        internal int iImage;
        internal IntPtr lParam;
        internal int iIndent;
        internal int iGroupId;
        internal uint cColumns;
        internal IntPtr puColumns;
        internal IntPtr piColFmt;
        internal int iGroup;
    }

    // LVHITTESTINFO has an iGroup tail field on supported Windows versions;
    // include it so the remote list-view never writes beyond our buffer.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct LVHITTESTINFO32
    {
        internal NativePoint pt;
        internal uint flags;
        internal int iItem;
        internal int iSubItem;
        internal int iGroup;
    }

    internal static Result TryGetCellScreenRectangle(
        IntPtr listHwnd,
        string airport,
        string runway,
        int subItem,
        out System.Windows.Rect rect,
        out string error)
    {
        rect = System.Windows.Rect.Empty;
        error = null;

        if (IntPtr.Size != 4)
        {
            error = "Native ListView geometry requires the x86 Manager build.";
            return Result.Unavailable;
        }

        if (String.IsNullOrWhiteSpace(airport) ||
            String.IsNullOrWhiteSpace(runway) ||
            subItem < 1 || subItem > 63)
        {
            error = "The requested ListView row or column is invalid.";
            return Result.Unsafe;
        }

        if (!IsNativeReportListView(listHwnd, out error))
            return Result.Unavailable;

        RemoteMemory remote;
        Result openResult = RemoteMemory.TryOpen(listHwnd, out remote, out error);
        if (openResult != Result.Success)
            return openResult;

        try
        {
            IntPtr messageResult;
            if (!TrySend(listHwnd, LVM_GETITEMCOUNT, IntPtr.Zero, IntPtr.Zero, out messageResult))
            {
                error = "The native ListView did not respond while reading its item count.";
                return Result.Unavailable;
            }

            int itemCount = messageResult.ToInt32();
            if (itemCount < 1 || itemCount > MAX_LIST_ITEMS)
            {
                error = "The native ListView reported an unsafe item count: " + itemCount + ".";
                return Result.Unsafe;
            }

            bool unicode;
            if (!TrySend(listHwnd, CCM_GETUNICODEFORMAT, IntPtr.Zero, IntPtr.Zero, out messageResult))
            {
                error = "Could not determine the native ListView text encoding.";
                return Result.Unavailable;
            }
            unicode = messageResult != IntPtr.Zero;

            int itemIndex;
            Result findResult = TryFindExactItem(
                remote, listHwnd, itemCount, unicode, airport, runway,
                out itemIndex, out error);
            if (findResult != Result.Success)
                return findResult;

            // Any result is acceptable here: a zero result can merely mean it
            // was already visible.  Geometry and hit-testing below prove it.
            if (!TrySend(listHwnd, LVM_ENSUREVISIBLE, new IntPtr(itemIndex), IntPtr.Zero, out messageResult))
            {
                error = "The native ListView did not respond while making the requested row visible.";
                return Result.Unavailable;
            }
            Thread.Sleep(50);

            // A sorted/control-backed list may change after EnsureVisible.
            // Re-read the two identity cells immediately before using its rect.
            string actualAirport;
            string actualRunway;
            Result textResult = TryReadItemText(
                remote, listHwnd, itemIndex, 0, unicode, out actualAirport, out error);
            if (textResult != Result.Success)
                return textResult;
            textResult = TryReadItemText(
                remote, listHwnd, itemIndex, 3, unicode, out actualRunway, out error);
            if (textResult != Result.Success)
                return textResult;
            if (!EqualText(actualAirport, airport) || !EqualText(actualRunway, runway))
            {
                error = "The native ListView changed the target row before its geometry was read.";
                return Result.Unsafe;
            }

            NativeRect clientCellRect;
            Result cellResult = TryGetSubItemClientRectangle(
                remote, listHwnd, itemIndex, subItem, out clientCellRect, out error);
            if (cellResult != Result.Success)
                return cellResult;

            NativePoint clientCenter = new NativePoint
            {
                X = clientCellRect.Left + (clientCellRect.Right - clientCellRect.Left) / 2,
                Y = clientCellRect.Top + (clientCellRect.Bottom - clientCellRect.Top) / 2
            };
            Result hitResult = TryVerifySubItemHit(
                remote, listHwnd, clientCenter, itemIndex, subItem, out error);
            if (hitResult != Result.Success)
                return hitResult;

            // The legacy ListView returns 96-DPI client coordinates.  Measure
            // the same client area both in our PerMonitorV2 context and in the
            // ListView's DPI-unaware context; their ratio is the actual input
            // scale.  LogicalToPhysicalPointForPerMonitorDPI cannot be used
            // here: on this legacy control Windows rejects the correct logical
            // screen point if it is numerically outside its physical bounds.
            double scaleX;
            double scaleY;
            Result scaleResult = TryGetMeasuredClientScale(
                listHwnd, out scaleX, out scaleY, out error);
            if (scaleResult != Result.Success)
                return scaleResult;

            NativePoint physicalClientOrigin = new NativePoint { X = 0, Y = 0 };
            if (!ClientToScreen(listHwnd, ref physicalClientOrigin))
            {
                error = "Could not obtain the native ListView's physical screen origin.";
                return Result.Unavailable;
            }

            long scaledX = physicalClientOrigin.X + (long)Math.Round(
                clientCenter.X * scaleX, MidpointRounding.AwayFromZero);
            long scaledY = physicalClientOrigin.Y + (long)Math.Round(
                clientCenter.Y * scaleY, MidpointRounding.AwayFromZero);
            if (scaledX < Int32.MinValue || scaledX > Int32.MaxValue ||
                scaledY < Int32.MinValue || scaledY > Int32.MaxValue)
            {
                error = "The native ListView DPI conversion produced an unsafe screen point.";
                return Result.Unsafe;
            }

            // Use the verified center only; this avoids exclusive right/bottom
            // RECT edge rounding at non-integer display scales.
            NativePoint screenCenter = new NativePoint
            {
                X = (int)scaledX,
                Y = (int)scaledY
            };

            NativePoint roundTripClient = new NativePoint
            {
                X = (int)Math.Round(
                    (screenCenter.X - physicalClientOrigin.X) / scaleX,
                    MidpointRounding.AwayFromZero),
                Y = (int)Math.Round(
                    (screenCenter.Y - physicalClientOrigin.Y) / scaleY,
                    MidpointRounding.AwayFromZero)
            };
            hitResult = TryVerifySubItemHit(
                remote, listHwnd, roundTripClient, itemIndex, subItem, out error);
            if (hitResult != Result.Success)
                return hitResult;

            IntPtr pointWindow = WindowFromPoint(screenCenter);
            if (pointWindow == IntPtr.Zero ||
                (pointWindow != listHwnd && !IsChild(listHwnd, pointWindow)))
            {
                error = "The native ListView cell screen point is covered by another window.";
                return Result.Unsafe;
            }

            // Callers only require the validated click center.  A one-pixel
            // rectangle preserves that public shape without adding scaled-edge
            // rounding back into the calculation.
            rect = new System.Windows.Rect(
                screenCenter.X - 0.5,
                screenCenter.Y - 0.5,
                1.0,
                1.0);
            return Result.Success;
        }
        finally
        {
            remote.Dispose();
        }
    }

    private static Result TryGetMeasuredClientScale(
        IntPtr listHwnd,
        out double scaleX,
        out double scaleY,
        out string error)
    {
        scaleX = 0.0;
        scaleY = 0.0;
        error = null;

        // The Manager itself is PerMonitorV2, so this is the actual physical
        // client size which SetPhysicalCursorPos and SendInput use.
        NativeRect physicalClient;
        if (!GetClientRect(listHwnd, out physicalClient))
        {
            error = "Could not read the native ListView's physical client size.";
            return Result.Unavailable;
        }

        // Ask for exactly the same rectangle through the legacy target's
        // DPI-unaware view.  Only this thread is switched, and it is restored
        // unconditionally before the method returns.
        IntPtr originalContext = SetThreadDpiAwarenessContext(new IntPtr(-1));
        if (originalContext == IntPtr.Zero)
        {
            error = "Could not enter the native ListView's DPI context.";
            return Result.Unavailable;
        }

        NativeRect logicalClient = new NativeRect();
        bool readLogical = false;
        bool restored = false;
        try
        {
            readLogical = GetClientRect(listHwnd, out logicalClient);
        }
        finally
        {
            restored = SetThreadDpiAwarenessContext(originalContext) != IntPtr.Zero;
        }

        if (!restored)
        {
            error = "Could not restore the Manager's DPI context after measuring the native ListView.";
            return Result.Unavailable;
        }
        if (!readLogical)
        {
            error = "Could not read the native ListView's logical client size.";
            return Result.Unavailable;
        }

        int physicalWidth = physicalClient.Right - physicalClient.Left;
        int physicalHeight = physicalClient.Bottom - physicalClient.Top;
        int logicalWidth = logicalClient.Right - logicalClient.Left;
        int logicalHeight = logicalClient.Bottom - logicalClient.Top;
        if (physicalWidth < 1 || physicalHeight < 1 ||
            logicalWidth < 1 || logicalHeight < 1)
        {
            error = "The native ListView returned an invalid client size for DPI conversion.";
            return Result.Unsafe;
        }

        scaleX = (double)physicalWidth / logicalWidth;
        scaleY = (double)physicalHeight / logicalHeight;
        if (Double.IsNaN(scaleX) || Double.IsInfinity(scaleX) ||
            Double.IsNaN(scaleY) || Double.IsInfinity(scaleY) ||
            scaleX < 0.25 || scaleX > 8.0 ||
            scaleY < 0.25 || scaleY > 8.0)
        {
            error = "The native ListView reported an unsafe DPI scale.";
            return Result.Unsafe;
        }

        return Result.Success;
    }

    private static bool IsNativeReportListView(IntPtr listHwnd, out string error)
    {
        error = null;
        if (listHwnd == IntPtr.Zero || !IsWindow(listHwnd))
        {
            error = "The native ListView window handle is no longer valid.";
            return false;
        }

        var className = new StringBuilder(128);
        if (GetClassName(listHwnd, className, className.Capacity) == 0 ||
            !className.ToString().Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
        {
            error = "EuroScope runway list is not a standard SysListView32 control.";
            return false;
        }

        int style = GetWindowLong(listHwnd, GWL_STYLE);
        if ((style & LVS_TYPEMASK) != LVS_REPORT)
        {
            error = "EuroScope runway list is not in report view.";
            return false;
        }
        if ((style & LVS_OWNERDATA) != 0)
        {
            // Microsoft documents LVM_GETITEMTEXT as unsupported for this
            // mode, so do not guess item identity from an owner-data control.
            error = "EuroScope runway list uses unsupported owner-data mode.";
            return false;
        }

        return true;
    }

    private static Result TryFindExactItem(
        RemoteMemory remote,
        IntPtr listHwnd,
        int itemCount,
        bool unicode,
        string airport,
        string runway,
        out int itemIndex,
        out string error)
    {
        itemIndex = -1;
        error = null;
        var matches = new List<int>();

        for (int index = 0; index < itemCount; index++)
        {
            string actualAirport;
            Result textResult = TryReadItemText(
                remote, listHwnd, index, 0, unicode, out actualAirport, out error);
            if (textResult != Result.Success)
                return textResult;

            if (!EqualText(actualAirport, airport))
                continue;

            string actualRunway;
            textResult = TryReadItemText(
                remote, listHwnd, index, 3, unicode, out actualRunway, out error);
            if (textResult != Result.Success)
                return textResult;

            if (EqualText(actualRunway, runway))
            {
                matches.Add(index);
                if (matches.Count > 1)
                {
                    error = "Native ListView has more than one " + airport +
                            " RWY " + runway + " row.";
                    return Result.Unsafe;
                }
            }
        }

        if (matches.Count != 1)
        {
            error = "Native ListView has no " + airport +
                    " RWY " + runway + " row.";
            return Result.Unsafe;
        }

        itemIndex = matches[0];
        return Result.Success;
    }

    private static Result TryReadItemText(
        RemoteMemory remote,
        IntPtr listHwnd,
        int itemIndex,
        int subItem,
        bool unicode,
        out string text,
        out string error)
    {
        text = null;
        error = null;

        byte[] zeroes = new byte[unicode ? TEXT_CHAR_CAPACITY * 2 : TEXT_CHAR_CAPACITY];
        if (!remote.TryWrite(remote.TextBuffer, zeroes, out error))
            return Result.Unavailable;

        var item = new LVITEM32
        {
            mask = LVIF_TEXT,
            iItem = itemIndex,
            iSubItem = subItem,
            pszText = remote.TextBuffer,
            cchTextMax = TEXT_CHAR_CAPACITY
        };
        if (!remote.TryWriteStructure(remote.ItemBuffer, item, out error))
            return Result.Unavailable;

        IntPtr messageResult;
        uint message = unicode ? LVM_GETITEMTEXTW : LVM_GETITEMTEXTA;
        if (!TrySend(listHwnd, message, new IntPtr(itemIndex), remote.ItemBuffer, out messageResult))
        {
            error = "The native ListView did not respond while reading item text.";
            return Result.Unavailable;
        }

        byte[] raw;
        if (!remote.TryRead(remote.TextBuffer, zeroes.Length, out raw, out error))
            return Result.Unavailable;

        int byteLength;
        if (unicode)
        {
            byteLength = FindUnicodeTerminator(raw);
            if (byteLength < 0)
            {
                error = "Native ListView text exceeded the safe buffer size.";
                return Result.Unsafe;
            }
            text = Encoding.Unicode.GetString(raw, 0, byteLength);
        }
        else
        {
            byteLength = Array.IndexOf(raw, (byte)0);
            if (byteLength < 0)
            {
                error = "Native ListView text exceeded the safe buffer size.";
                return Result.Unsafe;
            }
            text = Encoding.Default.GetString(raw, 0, byteLength);
        }

        return Result.Success;
    }

    private static Result TryGetSubItemClientRectangle(
        RemoteMemory remote,
        IntPtr listHwnd,
        int itemIndex,
        int subItem,
        out NativeRect rect,
        out string error)
    {
        rect = new NativeRect();
        error = null;

        // Per LVM_GETSUBITEMRECT: left is LVIR_*, top is the subitem index.
        var request = new NativeRect
        {
            Left = LVIR_BOUNDS,
            Top = subItem
        };
        if (!remote.TryWriteStructure(remote.RectBuffer, request, out error))
            return Result.Unavailable;

        IntPtr messageResult;
        if (!TrySend(
                listHwnd,
                LVM_GETSUBITEMRECT,
                new IntPtr(itemIndex),
                remote.RectBuffer,
                out messageResult))
        {
            error = "The native ListView did not respond while reading the subitem rectangle.";
            return Result.Unavailable;
        }
        if (messageResult == IntPtr.Zero)
        {
            error = "The native ListView did not return a rectangle for the requested subitem.";
            return Result.Unsafe;
        }

        if (!remote.TryReadStructure(remote.RectBuffer, out rect, out error))
            return Result.Unavailable;

        if (rect.Right - rect.Left < 6 || rect.Bottom - rect.Top < 6)
        {
            error = "The native ListView returned an invalid subitem rectangle.";
            return Result.Unsafe;
        }

        return Result.Success;
    }

    private static Result TryVerifySubItemHit(
        RemoteMemory remote,
        IntPtr listHwnd,
        NativePoint clientPoint,
        int expectedItem,
        int expectedSubItem,
        out string error)
    {
        error = null;
        var request = new LVHITTESTINFO32
        {
            pt = clientPoint,
            iItem = -1,
            iSubItem = -1
        };
        if (!remote.TryWriteStructure(remote.HitTestBuffer, request, out error))
            return Result.Unavailable;

        IntPtr messageResult;
        if (!TrySend(
                listHwnd,
                LVM_SUBITEMHITTEST,
                IntPtr.Zero,
                remote.HitTestBuffer,
                out messageResult))
        {
            error = "The native ListView did not respond while hit-testing the target cell.";
            return Result.Unavailable;
        }

        LVHITTESTINFO32 result;
        if (!remote.TryReadStructure(remote.HitTestBuffer, out result, out error))
            return Result.Unavailable;

        if (messageResult.ToInt32() != expectedItem ||
            result.iItem != expectedItem ||
            result.iSubItem != expectedSubItem)
        {
            error = "Native ListView hit-testing did not resolve the requested airport/runway cell.";
            return Result.Unsafe;
        }

        return Result.Success;
    }

    private static int FindUnicodeTerminator(byte[] bytes)
    {
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0)
                return i;
        return -1;
    }

    private static bool EqualText(string actual, string expected)
    {
        return String.Equals(
            (actual ?? String.Empty).Trim(),
            (expected ?? String.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TrySend(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        out IntPtr result)
    {
        return SendMessageTimeout(
            hWnd,
            message,
            wParam,
            lParam,
            SMTO_ABORTIFHUNG,
            MESSAGE_TIMEOUT_MS,
            out result);
    }

    private sealed class RemoteMemory : IDisposable
    {
        private IntPtr process;
        private IntPtr block;

        internal IntPtr ItemBuffer { get { return block; } }
        internal IntPtr TextBuffer { get { return IntPtr.Add(block, 128); } }
        internal IntPtr RectBuffer { get { return IntPtr.Add(block, 1024); } }
        internal IntPtr HitTestBuffer { get { return IntPtr.Add(block, 1152); } }

        private RemoteMemory(IntPtr process, IntPtr block)
        {
            this.process = process;
            this.block = block;
        }

        internal static Result TryOpen(
            IntPtr listHwnd,
            out RemoteMemory remote,
            out string error)
        {
            remote = null;
            error = null;

            uint processId;
            if (GetWindowThreadProcessId(listHwnd, out processId) == 0 || processId == 0)
            {
                error = "Could not obtain the native ListView process.";
                return Result.Unavailable;
            }

            IntPtr process = OpenProcess(
                PROCESS_VM_OPERATION |
                PROCESS_VM_READ |
                PROCESS_VM_WRITE |
                PROCESS_QUERY_INFORMATION |
                PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);
            if (process == IntPtr.Zero)
            {
                error = "Could not open the EuroScope process for native ListView queries.";
                return Result.Unavailable;
            }

            try
            {
                if (Environment.Is64BitOperatingSystem)
                {
                    bool targetWow64;
                    if (!IsWow64Process(process, out targetWow64))
                    {
                        error = "Could not determine EuroScope process architecture.";
                        return Result.Unavailable;
                    }
                    if (!targetWow64)
                    {
                        error = "Native ListView geometry is only enabled for 32-bit EuroScope.";
                        return Result.Unavailable;
                    }
                }

                IntPtr block = VirtualAllocEx(
                    process,
                    IntPtr.Zero,
                    new UIntPtr(REMOTE_BLOCK_SIZE),
                    MEM_COMMIT | MEM_RESERVE,
                    PAGE_READWRITE);
                if (block == IntPtr.Zero)
                {
                    error = "Could not allocate a native ListView query buffer in EuroScope.";
                    return Result.Unavailable;
                }

                remote = new RemoteMemory(process, block);
                process = IntPtr.Zero;
                return Result.Success;
            }
            finally
            {
                if (process != IntPtr.Zero)
                    CloseHandle(process);
            }
        }

        internal bool TryWrite(IntPtr address, byte[] bytes, out string error)
        {
            error = null;
            IntPtr written;
            if (!WriteProcessMemory(
                    process,
                    address,
                    bytes,
                    new IntPtr(bytes.Length),
                    out written) ||
                written.ToInt64() != bytes.Length)
            {
                error = "Could not write the native ListView query buffer.";
                return false;
            }
            return true;
        }

        internal bool TryRead(IntPtr address, int length, out byte[] bytes, out string error)
        {
            bytes = new byte[length];
            error = null;
            IntPtr read;
            if (!ReadProcessMemory(
                    process,
                    address,
                    bytes,
                    new IntPtr(length),
                    out read) ||
                read.ToInt64() != length)
            {
                error = "Could not read the native ListView query buffer.";
                return false;
            }
            return true;
        }

        internal bool TryWriteStructure(IntPtr address, object value, out string error)
        {
            error = null;
            int length = Marshal.SizeOf(value);
            IntPtr local = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(value, local, false);
                var bytes = new byte[length];
                Marshal.Copy(local, bytes, 0, length);
                return TryWrite(address, bytes, out error);
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }

        internal bool TryReadStructure<T>(IntPtr address, out T value, out string error)
            where T : struct
        {
            value = default(T);
            error = null;
            int length = Marshal.SizeOf(typeof(T));
            byte[] bytes;
            if (!TryRead(address, length, out bytes, out error))
                return false;

            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                value = (T)Marshal.PtrToStructure(
                    handle.AddrOfPinnedObject(), typeof(T));
                return true;
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            if (block != IntPtr.Zero)
            {
                VirtualFreeEx(process, block, UIntPtr.Zero, MEM_RELEASE);
                block = IntPtr.Zero;
            }
            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
                process = IntPtr.Zero;
            }
        }
    }
}
