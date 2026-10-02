using System.Runtime.InteropServices;
using AdTrim.Encoders;

namespace AdTrim.Services;

public static class GraphicsAdapters
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescription(IntPtr self, out Description description);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckInterface(IntPtr self, in Guid iid, out long version);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint Vendor, Device, Subsystem, Revision;
        public UIntPtr VideoMemory, SystemMemory, SharedMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    public static IReadOnlyList<HardwareAdapter> Enumerate()
    {
        var result = new List<HardwareAdapter>();
        if (!OperatingSystem.IsWindows()) return result;
        var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(iid, out var factory));
        try
        {
            var enumerate = Method<EnumAdapters>(factory, 12); // IDXGIFactory1.EnumAdapters1
            for (uint i = 0; ; i++)
            {
                int hr = enumerate(factory, i, out var adapter);
                if (hr == unchecked((int)0x887A0002)) break;
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(Method<GetDescription>(adapter, 10)(adapter, out var desc));
                    if ((desc.Flags & 2) != 0) continue; // Software adapter.
                    var deviceIid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
                    int driverHr = Method<CheckInterface>(adapter, 9)(adapter, deviceIid, out var version);
                    result.Add(new((int)i, desc.Name, desc.Vendor, desc.Device,
                        $"{desc.LuidHigh:x8}{desc.LuidLow:x8}", driverHr >= 0 ? version.ToString("x16") : "unknown"));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return result;
    }
}
