using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ReadNow.Native;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ReadNow.Views;

/// <summary>
/// 微信式区域截图：先冻结所有屏幕的画面，全屏压暗，框选后直接裁剪冻结图。
/// 支持多显示器（覆盖整个虚拟屏幕）。
/// </summary>
public partial class SnipOverlayWindow : Window
{
    private BitmapSource? _frozen;
    private System.Windows.Point _start;
    private bool _dragging;

    /// <summary>框选完成。参数：裁剪出的区域图像、当时光标位置（物理像素）。</summary>
    public event Action<BitmapSource, Win32.POINT>? RegionCaptured;

    /// <summary>Esc 或框选区域过小 → 取消。</summary>
    public event Action? Cancelled;

    public SnipOverlayWindow()
    {
        InitializeComponent();

        // 覆盖整个虚拟屏幕（所有显示器），单位是 WPF DIP
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Cancel();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 窗口显示前冻结画面，避免把遮罩自己截进去
        _frozen = CaptureVirtualScreen();
        FrozenImage.Source = _frozen;
        SelectionBrush.Visual = FrozenImage;
        // VisualBrush 的 Viewbox 让选中区域显示原图对应位置（坐标用相对比例）
        SelectionBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
    }

    /// <summary>把整个虚拟屏幕（所有显示器）截成一张位图。</summary>
    private static BitmapSource CaptureVirtualScreen()
    {
        var vs = WinForms.SystemInformation.VirtualScreen; // 物理像素
        using var bmp = new Drawing.Bitmap(vs.Width, vs.Height, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(vs.Left, vs.Top, 0, 0, vs.Size);
        }

        var hBitmap = bmp.GetHbitmap();
        try
        {
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(RootCanvas);
        _dragging = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect, _start.Y);
        SelectionRect.Width = 0;
        SelectionRect.Height = 0;
        RootCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = e.GetPosition(RootCanvas);
        UpdateSelection(pos);
    }

    private void UpdateSelection(System.Windows.Point pos)
    {
        double x = Math.Min(pos.X, _start.X);
        double y = Math.Min(pos.Y, _start.Y);
        double w = Math.Abs(pos.X - _start.X);
        double h = Math.Abs(pos.Y - _start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect, y);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        // 让选中区域透出冻结原图：Viewbox 用相对坐标定位到原图同一区域
        if (w > 0 && h > 0 && RootCanvas.ActualWidth > 0 && RootCanvas.ActualHeight > 0)
        {
            SelectionBrush.Viewbox = new Rect(
                x / RootCanvas.ActualWidth, y / RootCanvas.ActualHeight,
                w / RootCanvas.ActualWidth, h / RootCanvas.ActualHeight);
        }
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RootCanvas.ReleaseMouseCapture();

        var pos = e.GetPosition(RootCanvas);
        double x = Math.Min(pos.X, _start.X);
        double y = Math.Min(pos.Y, _start.Y);
        double w = Math.Abs(pos.X - _start.X);
        double h = Math.Abs(pos.Y - _start.Y);

        if (w < 12 || h < 12 || _frozen == null) { Cancel(); return; }

        // DIP 坐标 → 冻结图像素坐标（整张图线性映射，天然支持多屏）
        double scaleX = _frozen.PixelWidth / RootCanvas.ActualWidth;
        double scaleY = _frozen.PixelHeight / RootCanvas.ActualHeight;
        int px = Math.Clamp((int)(x * scaleX), 0, _frozen.PixelWidth - 1);
        int py = Math.Clamp((int)(y * scaleY), 0, _frozen.PixelHeight - 1);
        int pw = Math.Clamp((int)(w * scaleX), 1, _frozen.PixelWidth - px);
        int ph = Math.Clamp((int)(h * scaleY), 1, _frozen.PixelHeight - py);

        BitmapSource region = new CroppedBitmap(_frozen, new Int32Rect(px, py, pw, ph));
        region.Freeze();

        Win32.GetCursorPos(out var cursor);
        var handler = RegionCaptured;
        Close();
        handler?.Invoke(region, cursor);
    }

    private void Cancel()
    {
        var handler = Cancelled;
        Close();
        handler?.Invoke();
    }
}
