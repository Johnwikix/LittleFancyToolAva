using System.Runtime.InteropServices;

namespace FancyToolAva.Services;

/// <summary>Identifies a physical DXGI adapter and its dedicated video memory.</summary>
internal sealed record VideoGpuAdapter(int Index, string Name, uint VendorId, uint DeviceId, nuint VideoMemory);

/// <summary>Selects a physical DirectML adapter, preferring dedicated graphics memory over integrated adapters.</summary>
internal static unsafe class VideoSrGpuSelector
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Description[128];
        public uint Vendor, Device, Subsystem, Revision;
        public nuint VideoMemory, SystemMemory, SharedMemory;
        public long Luid;
        public uint Flags;
    }
    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enumerate(nint factory, uint index, out nint adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Describe(nint adapter, AdapterDescription* description);

    internal static (int Index, string Name, uint VendorId, uint DeviceId) Select()
    {
        var best = EnumerateAdapters().OrderByDescending(adapter => adapter.VideoMemory).FirstOrDefault()
            ?? throw new InvalidOperationException("No physical GPU available");
        return (best.Index, best.Name, best.VendorId, best.DeviceId);
    }

    internal static IReadOnlyList<VideoGpuAdapter> EnumerateAdapters()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DirectML requires Windows");
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(iid, out nint factory));
        try
        {
            var enumerate = Marshal.GetDelegateForFunctionPointer<Enumerate>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), 12 * IntPtr.Size));
            var adapters = new List<VideoGpuAdapter>();
            for (uint i = 0; enumerate(factory, i, out nint adapter) >= 0; i++)
            {
                try
                {
                    var describe = Marshal.GetDelegateForFunctionPointer<Describe>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(adapter), 10 * IntPtr.Size));
                    AdapterDescription description;
                    if (describe(adapter, &description) < 0 || (description.Flags & 2) != 0) continue;
                    adapters.Add(new VideoGpuAdapter((int)i, new string(description.Description), description.Vendor, description.Device, description.VideoMemory));
                }
                finally { Marshal.Release(adapter); }
            }
            return adapters;
        }
        finally { Marshal.Release(factory); }
    }
}
