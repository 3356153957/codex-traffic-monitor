using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexTraffic;

// The documented provider used by Get-DnsClientCache, accessed directly through COM.
// No PowerShell process, extra managed package, undocumented DNS API, or DNS request.
// https://learn.microsoft.com/windows/win32/fwp/wmi/dnsclientcimprov/msft-dnsclientcache
internal static class WindowsDnsCacheReader
{
    private const string Query = "SELECT Entry, Data, TimeToLive, Type, Status FROM MSFT_DNSClientCache WHERE Type = 1 OR Type = 28";

    internal static IReadOnlyList<DnsCacheRecord>? Read() => Read(out _);

    internal static IReadOnlyList<DnsCacheRecord>? Read(out string error)
    {
        error = "";
        IWbemLocator? locator = null;
        IWbemServices? services = null;
        IEnumWbemClassObject? rows = null;
        try
        {
            var elapsed = Stopwatch.StartNew();
            locator = (IWbemLocator)Activator.CreateInstance(Type.GetTypeFromCLSID(
                new Guid("4590F811-1D3A-11D0-891F-00AA004B2E24"), throwOnError: true)!)!;
            // A local namespace only. MAX_WAIT bounds a broken WMI connection to two minutes;
            // it runs on one background timer callback and never blocks UI or traffic capture.
            Marshal.ThrowExceptionForHR(locator.ConnectServer(@"root\StandardCimv2", null, null, null,
                0x80, null, IntPtr.Zero, out services));
            SetProxySecurity(services);
            // RETURN_IMMEDIATELY | FORWARD_ONLY avoids retaining the entire COM result set.
            Marshal.ThrowExceptionForHR(services.ExecQuery("WQL", Query, 0x30, IntPtr.Zero, out rows));
            SetProxySecurity(rows);
            var result = new List<DnsCacheRecord>();
            int visited = 0;
            while (visited < DnsCacheHints.MaximumRecords && elapsed.ElapsedMilliseconds < 8000)
            {
                IWbemClassObject? row = null;
                try
                {
                    int status = rows.Next(250, 1, out row, out uint returned);
                    Marshal.ThrowExceptionForHR(status);
                    if (returned == 0)
                    {
                        if (status == 1) return result; // WBEM_S_FALSE: complete, including an empty cache.
                        if (status == 0x40004) continue; // WBEM_S_TIMEDOUT: bounded retry.
                        return null;
                    }
                    if (returned != 1 || row == null) return null;
                    visited++;
                    if (Get(row, "Entry") is string name && Get(row, "Data") is string address &&
                        TryUInt32(Get(row, "TimeToLive"), out uint ttl) &&
                        TryUInt32(Get(row, "Type"), out uint type) && type <= ushort.MaxValue &&
                        TryUInt32(Get(row, "Status"), out uint recordStatus))
                        result.Add(new(name, address, ttl, (ushort)type, recordStatus));
                    if (status == 1) return result;
                }
                finally { Release(row); }
            }
            // A bounded partial snapshot still supplies hints; it never affects byte accounting.
            return result;
        }
        catch (Exception exception)
        {
            error = exception.ToString();
            return null; // WMI disabled, denied, unavailable, or unsupported: omit optional hints.
        }
        finally
        {
            Release(rows);
            Release(services);
            Release(locator);
        }
    }

    private static object? Get(IWbemClassObject row, string name) =>
        row.Get(name, 0, out object? value, IntPtr.Zero, IntPtr.Zero) >= 0 ? value : null;

    private static bool TryUInt32(object? value, out uint result)
    {
        // CIM uint16 is exposed as VARIANT int32; CIM uint32 may use int32's bit pattern.
        if (value is int signed) { result = unchecked((uint)signed); return true; }
        if (value is uint unsigned) { result = unsigned; return true; }
        if (value is ushort small) { result = small; return true; }
        result = 0;
        return false;
    }

    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private static void SetProxySecurity<T>(T proxy) where T : class
    {
        // Proxy blankets belong to an interface pointer. Passing an IUnknown-marshalled
        // object here leaves the actual WMI interface at its default impersonation level.
        IntPtr pointer = Marshal.GetComInterfaceForObject(proxy, typeof(T));
        try { Marshal.ThrowExceptionForHR(CoSetProxyBlanket(pointer, 10, 0, IntPtr.Zero, 6, 3, IntPtr.Zero, 0)); }
        finally { Marshal.Release(pointer); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoSetProxyBlanket(IntPtr proxy,
        uint authenticationService, uint authorizationService, IntPtr serverPrincipalName,
        uint authenticationLevel, uint impersonationLevel, IntPtr authenticationInfo, uint capabilities);

    // Partial COM interfaces keep native vtable order from the Windows SDK WbemCli.h.
    // Unused slots are placeholders and must never be called. All returned pointers/VARIANTs
    // use the CLR COM marshaller; no unbounded native strings or linked lists are traversed.
    // https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/WbemCli.h
    [ComImport, Guid("DC12A687-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemLocator
    {
        [PreserveSig] int ConnectServer([MarshalAs(UnmanagedType.BStr)] string resource,
            [MarshalAs(UnmanagedType.BStr)] string? user, [MarshalAs(UnmanagedType.BStr)] string? password,
            [MarshalAs(UnmanagedType.BStr)] string? locale, int flags,
            [MarshalAs(UnmanagedType.BStr)] string? authority, IntPtr context, out IWbemServices services);
    }

    [ComImport, Guid("9556DC99-828C-11CF-A37E-00AA003240C7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemServices
    {
        void OpenNamespace();
        void CancelAsyncCall();
        void QueryObjectSink();
        void GetObject();
        void GetObjectAsync();
        void PutClass();
        void PutClassAsync();
        void DeleteClass();
        void DeleteClassAsync();
        void CreateClassEnum();
        void CreateClassEnumAsync();
        void PutInstance();
        void PutInstanceAsync();
        void DeleteInstance();
        void DeleteInstanceAsync();
        void CreateInstanceEnum();
        void CreateInstanceEnumAsync();
        [PreserveSig] int ExecQuery([MarshalAs(UnmanagedType.BStr)] string language,
            [MarshalAs(UnmanagedType.BStr)] string query, int flags, IntPtr context, out IEnumWbemClassObject rows);
    }

    [ComImport, Guid("027947E1-D731-11CE-A357-000000000001"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumWbemClassObject
    {
        void Reset();
        // The native API accepts an array; requesting exactly one object makes this one out pointer.
        [PreserveSig] int Next(int timeout, uint count, out IWbemClassObject? row, out uint returned);
    }

    [ComImport, Guid("DC12A681-737F-11CF-884D-00AA004B2E24"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWbemClassObject
    {
        void GetQualifierSet();
        [PreserveSig] int Get([MarshalAs(UnmanagedType.LPWStr)] string name, int flags,
            [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr type, IntPtr flavor);
    }
}
