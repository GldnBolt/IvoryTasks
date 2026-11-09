using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;
using Ivory.Sync;

namespace Ivory.Widget;
[ComImport, ComVisible(false), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr value);
    [PreserveSig] int LockServer(bool locked);
}
[ComVisible(true)]
sealed class ProviderFactory : IClassFactory
{
    static readonly Lazy<WidgetProvider> Provider = new(() => new WidgetProvider());
    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr value)
    {
        value = IntPtr.Zero; if (outer != IntPtr.Zero) return unchecked((int)0x80040110);
        IntPtr pointer = IntPtr.Zero;
        try { pointer = MarshalInspectable<IWidgetProvider>.FromManaged(Provider.Value); return Marshal.QueryInterface(pointer, in iid, out value); }
        catch (Exception e) { AppLog.Write("widget.factory.failed", e); return Marshal.GetHRForException(e); }
        finally { if (pointer != IntPtr.Zero) Marshal.Release(pointer); }
    }
    public int LockServer(bool locked) => 0;
}
static class Program
{
    [DllImport("ole32.dll")] static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);
    [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);
    [MTAThread] static void Main()
    {
        try {
            ComWrappersSupport.InitializeComWrappers();
            var factory = new ProviderFactory();
            Marshal.ThrowExceptionForHR(CoRegisterClassObject(new Guid("58125CC9-C763-46EF-9C95-D2076099C047"), factory, 4, 1, out var cookie));
            try { WidgetProvider.Exit.WaitOne(); } finally { CoRevokeClassObject(cookie); GC.KeepAlive(factory); }
        } catch (Exception e) { AppLog.Write("widget.start.failed", e); }
    }
}
