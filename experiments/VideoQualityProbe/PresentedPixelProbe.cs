using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using RemoteDesk;
using Vortice.Direct3D11;

// Markers belong to the test window. Read its rendered texture even if another
// application covers the window; never sample the workstation's desktop.
internal static class PresentedPixelProbe
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static Color? Read(RemoteViewerWindow viewer, double horizontal = .5)
    {
        var gate = (CapturePresentationTransitionGate)typeof(RemoteViewerWindow)
            .GetField("_capturePresentationTransitionGate", Fields)!.GetValue(viewer)!;
        return gate.Run<Color?>(() =>
        {
            var presenter = typeof(RemoteViewerWindow).GetField("_d3d11VideoPresenter", Fields)!.GetValue(viewer);
            if (presenter is null || typeof(D3D11HwndVideoPresenter).GetField("_validationSource", Fields)!
                    .GetValue(presenter) is not ID3D11Texture2D surface)
            {
                var picture = (System.Windows.Forms.PictureBox)typeof(RemoteViewerWindow).GetField("_pictureBox", Fields)!.GetValue(viewer)!;
                return picture.Image is Bitmap bitmap ? bitmap.GetPixel((int)(bitmap.Width * horizontal), bitmap.Height / 2) : null;
            }
            var device = (ID3D11Device)typeof(D3D11HwndVideoPresenter).GetField("_device", Fields)!.GetValue(presenter)!;
            var desc = surface.Description;
            int x = (int)(desc.Width * horizontal), y = (int)desc.Height / 2;
            desc.Width = desc.Height = 1;
            desc.Usage = ResourceUsage.Staging;
            desc.BindFlags = BindFlags.None;
            desc.CPUAccessFlags = CpuAccessFlags.Read;
            using var pixel = device.CreateTexture2D(desc);
            var context = device.ImmediateContext;
            context.CopySubresourceRegion(pixel, 0, 0, 0, 0, surface, 0, new Vortice.Mathematics.Box(x, y, 0, x+1, y+1, 1));
            var map = context.Map(pixel, 0, MapMode.Read);
            try { return Color.FromArgb(Marshal.ReadByte(map.DataPointer, 2), Marshal.ReadByte(map.DataPointer, 1), Marshal.ReadByte(map.DataPointer, 0)); }
            finally { context.Unmap(pixel, 0); }
        });
    }
}
