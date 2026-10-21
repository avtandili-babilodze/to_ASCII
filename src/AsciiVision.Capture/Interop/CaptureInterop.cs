using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace AsciiVision.Capture.Interop;

/// <summary>
/// COM interop needed by Windows.Graphics.Capture from a desktop app:
/// IGraphicsCaptureItemInterop (create items from HWND/HMONITOR), the WinRT Direct3D device
/// wrapper and IDirect3DDxgiInterfaceAccess (get the ID3D11Texture2D behind a capture surface).
/// Calls go through vtable function pointers so they do not depend on the built-in COM marshaller.
/// </summary>
internal static unsafe class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid GraphicsCaptureItemInteropIid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid DxgiInterfaceAccessIid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private const int VtblCreateForWindow = 3;
    private const int VtblCreateForMonitor = 4;
    private const int VtblGetInterface = 3;

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd) => CreateItem(hwnd, VtblCreateForWindow);

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor) => CreateItem(hmonitor, VtblCreateForMonitor);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
    {
        IntPtr factory = GetInteropFactory();
        try
        {
            var iid = GraphicsCaptureItemIid;
            IntPtr itemPtr;
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(*(void***)factory + slot));
            int hr = fn(factory, handle, &iid, &itemPtr);
            Marshal.ThrowExceptionForHR(hr);
            try
            {
                return GraphicsCaptureItem.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(itemPtr);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static IntPtr GetInteropFactory()
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(className, className.Length, out var hstring));
        try
        {
            var iid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(hstring, ref iid, out var factory));
            return factory;
        }
        finally
        {
            NativeMethods.WindowsDeleteString(hstring);
        }
    }

    /// <summary>Creates a hardware D3D11 device with BGRA support, falling back to WARP (software).</summary>
    public static (ID3D11Device Device, ID3D11DeviceContext Context, bool IsSoftware) CreateD3DDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device? device, out ID3D11DeviceContext? context);
        if (result.Success && device is not null && context is not null)
        {
            return (device, context, false);
        }

        result = D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out device, out context);
        result.CheckError();
        return (device!, context!, true);
    }

    [DllImport("d3d11.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        // Called directly: the wrapper library's generic overload marshals the result as a COM object.
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr graphicsDevice));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            Marshal.Release(graphicsDevice);
        }
    }

    /// <summary>Returns the texture behind a capture surface. The caller owns (and must dispose) the result.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        IntPtr surfacePtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessIid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePtr, in accessIid, out IntPtr access));
            try
            {
                var textureIid = Texture2DIid;
                IntPtr texture;
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(*(void***)access + VtblGetInterface));
                Marshal.ThrowExceptionForHR(fn(access, &textureIid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePtr);
        }
    }
}
