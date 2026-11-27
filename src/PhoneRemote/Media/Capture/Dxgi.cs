using System.Runtime.InteropServices;

namespace PhoneRemote.Media.Capture;

/// <summary>
/// The outputs of the default graphics adapter, in DXGI order: exactly what ffmpeg's <c>ddagrab</c> means by
/// <c>output_idx</c> (it opens the default D3D11 device and enumerates that adapter's outputs). Each output
/// knows its Windows device name ("\\.\DISPLAY2") and its desktop rectangle.
/// </summary>
public static class Dxgi
{
    public sealed record Output(int Index, string DeviceName, int Left, int Top, int Right, int Bottom, bool Attached);

    /// <summary>Outputs of adapter 0; empty if DXGI is unavailable (remote session, no GPU).</summary>
    public static Output[] Outputs()
    {
        var list = new List<Output>();
        IntPtr factory = IntPtr.Zero, adapter = IntPtr.Zero;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return Array.Empty<Output>();
            var enumAdapters = Fn<EnumAdaptersFn>(factory, 7);
            if (enumAdapters(factory, 0, out adapter) != 0 || adapter == IntPtr.Zero) return Array.Empty<Output>();
            var enumOutputs = Fn<EnumOutputsFn>(adapter, 7);
            for (uint i = 0; i < 16; i++)
            {
                if (enumOutputs(adapter, i, out var output) != 0 || output == IntPtr.Zero) break;
                try
                {
                    var getDesc = Fn<GetOutputDescFn>(output, 7);
                    if (getDesc(output, out var desc) == 0)
                        list.Add(new Output((int)i, desc.DeviceName ?? "", desc.Left, desc.Top, desc.Right, desc.Bottom, desc.AttachedToDesktop != 0));
                }
                finally { Marshal.Release(output); }
            }
        }
        catch { }
        finally
        {
            if (adapter != IntPtr.Zero) Marshal.Release(adapter);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
        return list.ToArray();
    }

    private static T Fn<T>(IntPtr obj, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdaptersFn(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumOutputsFn(IntPtr self, uint index, out IntPtr output);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetOutputDescFn(IntPtr self, out DXGI_OUTPUT_DESC desc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_OUTPUT_DESC
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public int Left, Top, Right, Bottom;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
