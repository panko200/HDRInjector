using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HDRInjector.Settings;

namespace HDRInjector.Controls;

public enum WheelMode
{
    Triangle,
    Square
}

public class HsvColorWheel : Grid
{
    private const int CanvasSize = 150;
    private const double CX = 75.0;
    private const double CY = 75.0;
    private const double OuterR = 72.0;
    private const double InnerR = 55.0;
    private const double TriR = 53.0;
    private const int SqSide = 74;
    private const double SqLeft = CX - SqSide / 2.0;
    private const double SqTop = CY - SqSide / 2.0;
    private const double RightDragThreshold = 4.0;

    public static readonly DependencyProperty HProperty =
        DependencyProperty.Register(nameof(H), typeof(byte), typeof(HsvColorWheel),
            new FrameworkPropertyMetadata((byte)0,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHChanged));

    public static readonly DependencyProperty SProperty =
        DependencyProperty.Register(nameof(S), typeof(byte), typeof(HsvColorWheel),
            new FrameworkPropertyMetadata((byte)255,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSvChanged));

    public static readonly DependencyProperty VProperty =
        DependencyProperty.Register(nameof(V), typeof(byte), typeof(HsvColorWheel),
            new FrameworkPropertyMetadata((byte)255,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSvChanged));

    public byte H
    {
        get => (byte)GetValue(HProperty);
        set => SetValue(HProperty, value);
    }

    public byte S
    {
        get => (byte)GetValue(SProperty);
        set => SetValue(SProperty, value);
    }

    public byte V
    {
        get => (byte)GetValue(VProperty);
        set => SetValue(VProperty, value);
    }

    public Action<byte, byte, byte>? OnColorChanged { get; set; }
    public bool IsDragging => _isDragging;

    public event Action<bool>? OnTrackingRotationChanged;
    public event Action<bool>? OnFixedAngleEnabledChanged;
    public event Action<double>? OnFixedAngleChanged;

    public bool FixedAngleEnabled
    {
        get => _fixedAngleEnabled;
        set
        {
            if (_fixedAngleEnabled == value) return;
            _fixedAngleEnabled = value;
            if (_fixedAngleEnabled) _trackingRotationEnabled = false;
            OnFixedAngleEnabledChanged?.Invoke(value);
            if (_initialized)
            {
                UpdateTriangleGeometry();
                UpdateSquareGeometry();
                RenderInnerGradient();
                UpdateHueIndicator();
                UpdateSvIndicator();
            }
        }
    }

    public double FixedAngle
    {
        get => _fixedAngle;
        set
        {
            _fixedAngle = value;
            OnFixedAngleChanged?.Invoke(value);
            if ((_fixedAngleEnabled || !_trackingRotationEnabled) && _initialized)
            {
                UpdateTriangleGeometry();
                UpdateSquareGeometry();
                RenderInnerGradient();
                UpdateHueIndicator();
                UpdateSvIndicator();
            }
        }
    }

    public bool TrackingRotationEnabled
    {
        get => _trackingRotationEnabled;
        set
        {
            if (_trackingRotationEnabled == value) return;
            _trackingRotationEnabled = value;
            if (_trackingRotationEnabled) _fixedAngleEnabled = false;
            OnTrackingRotationChanged?.Invoke(value);
            if (_initialized)
            {
                UpdateTriangleGeometry();
                UpdateSquareGeometry();
                RenderInnerGradient();
                UpdateHueIndicator();
                UpdateSvIndicator();
            }
        }
    }

    public WheelMode Mode => _mode;

    private readonly Canvas _canvas;
    private readonly Rectangle _hueRingRect;
    private readonly Polygon _svTriangle;
    private readonly Rectangle _svSquare;
    private readonly Ellipse _hueIndicatorShadow;
    private readonly Ellipse _hueIndicator;
    private readonly Ellipse _svIndicatorShadow;
    private readonly Ellipse _svIndicator;

    private WheelMode _mode = WheelMode.Triangle;
    private Point _a, _b, _c;
    private Point _baseA, _baseB, _baseC;
    private double _hue;
    private double _sat = 1.0;
    private double _val = 1.0;
    private double _hueOffset;
    private bool _isDragging;
    private bool _isRightDragging;
    private bool _dragRing;
    private bool _dragInner;
    private Point _rightDownPos;
    private bool _rightDragMoved;
    private bool _updating;
    private bool _initialized;
    private bool _fixedAngleEnabled;
    private double _fixedAngle = 0.0;
    private bool _trackingRotationEnabled = true;

    private static double _savedHueOffset;
    private static WriteableBitmap? _cachedHueRing;

    public HsvColorWheel()
    {
        Width = CanvasSize;
        Height = CanvasSize;
        ClipToBounds = false;

        _canvas = new Canvas
        {
            Width = CanvasSize,
            Height = CanvasSize,
            Background = Brushes.Transparent
        };
        Children.Add(_canvas);

        _hueRingRect = new Rectangle
        {
            Width = CanvasSize,
            Height = CanvasSize,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_hueRingRect);

        _svTriangle = new Polygon
        {
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_svTriangle);

        _svSquare = new Rectangle
        {
            Width = SqSide,
            Height = SqSide,
            Stroke = Brushes.Black,
            StrokeThickness = 1,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        Canvas.SetLeft(_svSquare, CX - SqSide / 2.0);
        Canvas.SetTop(_svSquare, CY - SqSide / 2.0);
        _canvas.Children.Add(_svSquare);

        _hueIndicatorShadow = new Ellipse
        {
            Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            Width = 16,
            Height = 16,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_hueIndicatorShadow);

        _hueIndicator = new Ellipse
        {
            Stroke = Brushes.White,
            StrokeThickness = 2,
            Width = 14,
            Height = 14,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_hueIndicator);

        _svIndicatorShadow = new Ellipse
        {
            Width = 16,
            Height = 16,
            Stroke = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)),
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_svIndicatorShadow);

        _svIndicator = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _canvas.Children.Add(_svIndicator);

        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove += OnMouseMove;
        _canvas.MouseLeftButtonUp += OnMouseUp;
        _canvas.MouseRightButtonDown += OnRightMouseDown;
        _canvas.MouseRightButtonUp += OnRightMouseUp;

        var ctxMenu = new ContextMenu();
        AddOffsetPreset(ctxMenu, "赤=右（ibisPaint / MediBang）", 0);
        AddOffsetPreset(ctxMenu, "赤=上（PaintShop Pro）", -90);
        AddOffsetPreset(ctxMenu, "赤=左（Krita）", 180);
        AddOffsetPreset(ctxMenu, "CLIP STUDIO PAINT（5π/6）", -150);
        ctxMenu.Items.Add(new Separator());

        var trackingItem = new MenuItem { Header = "追尾回転" };
        trackingItem.IsCheckable = true;
        trackingItem.Click += (s, e) => TrackingRotationEnabled = trackingItem.IsChecked;
        ctxMenu.Opened += (s, e) => trackingItem.IsChecked = TrackingRotationEnabled;
        ctxMenu.Items.Add(trackingItem);

        var fixedAngleItem = new MenuItem { Header = "角度固定" };
        fixedAngleItem.IsCheckable = true;
        fixedAngleItem.Click += (s, e) => FixedAngleEnabled = fixedAngleItem.IsChecked;
        ctxMenu.Opened += (s, e) => fixedAngleItem.IsChecked = FixedAngleEnabled;
        ctxMenu.Items.Add(fixedAngleItem);

        var setAngleItem = new MenuItem { Header = "固定角度を指定..." };
        setAngleItem.Click += (s, e) => ShowFixedAngleDialog();
        ctxMenu.Items.Add(setAngleItem);

        this.ContextMenu = ctxMenu;
        this.ContextMenuOpening += OnContextMenuOpening;
    }

    private void AddOffsetPreset(ContextMenu menu, string header, double offset)
    {
        var item = new MenuItem { Header = header };
        item.Click += (s, e) => SetHueOffset(offset);
        menu.Items.Add(item);
    }

    private void ShowFixedAngleDialog()
    {
        var win = new Window
        {
            Title = "固定角度",
            Width = 280,
            Height = 140,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock
        {
            Text = $"三角形/四角形の固定角度（度）を入力:\n現在: {_fixedAngle:F1}°",
            Margin = new Thickness(0, 0, 0, 4)
        });
        var tb = new TextBox { Text = _fixedAngle.ToString("F1") };
        sp.Children.Add(tb);
        var btn = new Button
        {
            Content = "適用",
            Width = 80,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btn.Click += (s, e) =>
        {
            if (double.TryParse(tb.Text, out double v))
            {
                _fixedAngle = v;
                FixedAngleEnabled = true;
                if (_initialized)
                {
                    UpdateTriangleGeometry();
                    UpdateSquareGeometry();
                    RenderInnerGradient();
                    UpdateHueIndicator();
                    UpdateSvIndicator();
                }
                win.Close();
            }
        };
        sp.Children.Add(btn);
        win.Content = sp;
        win.ShowDialog();
    }

    public void SetHueOffset(double offset)
    {
        _hueOffset = offset;
        _savedHueOffset = offset;
        _hueRingRect.RenderTransform = new RotateTransform(offset, CX, CY);
        if (_initialized)
        {
            UpdateTriangleGeometry();
            UpdateSquareGeometry();
            RenderInnerGradient();
            UpdateHueIndicator();
            UpdateSvIndicator();
        }
    }

    public void ApplySettings(HdrColorPickerSettings settings)
    {
        if (_mode == WheelMode.Triangle)
        {
            TrackingRotationEnabled = settings.TriangleTrackingRotationEnabled;
            FixedAngleEnabled = settings.TriangleFixedAngleEnabled;
            FixedAngle = settings.TriangleFixedAngle;
        }
        else
        {
            TrackingRotationEnabled = settings.SquareTrackingRotationEnabled;
            FixedAngleEnabled = settings.SquareFixedAngleEnabled;
            FixedAngle = settings.SquareFixedAngle;
        }
    }

    public void SetMode(WheelMode mode)
    {
        _mode = mode;
        if (_mode == WheelMode.Triangle)
        {
            _svTriangle.Visibility = Visibility.Visible;
            _svSquare.Visibility = Visibility.Collapsed;
        }
        else
        {
            _svTriangle.Visibility = Visibility.Collapsed;
            _svSquare.Visibility = Visibility.Visible;
        }
        if (_initialized)
        {
            UpdateTriangleGeometry();
            UpdateSquareGeometry();
            RenderInnerGradient();
            UpdateHueIndicator();
            UpdateSvIndicator();
        }
    }

    public void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        InitBaseTriangle();
        _hue = H / 255.0 * 360.0;
        _sat = S / 255.0;
        _val = V / 255.0;
        _hueOffset = _savedHueOffset;
        _hueRingRect.RenderTransform = new RotateTransform(_hueOffset, CX, CY);
        RenderHueRing();
        UpdateTriangleGeometry();
        UpdateSquareGeometry();
        RenderInnerGradient();
        UpdateHueIndicator();
        UpdateSvIndicator();
    }

    private static void OnHChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HsvColorWheel w) return;
        w._hue = (byte)e.NewValue / 255.0 * 360.0;
        if (!w._updating && !w._isDragging && w._initialized)
        {
            w._updating = true;
            try
            {
                w.UpdateTriangleGeometry();
                w.UpdateSquareGeometry();
                w.RenderInnerGradient();
                w.UpdateHueIndicator();
                w.UpdateSvIndicator();
            }
            finally { w._updating = false; }
        }
    }

    private static void OnSvChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HsvColorWheel w) return;
        w._sat = w.S / 255.0;
        w._val = w.V / 255.0;
        if (!w._updating && !w._isDragging && w._initialized)
        {
            w._updating = true;
            try { w.UpdateSvIndicator(); }
            finally { w._updating = false; }
        }
    }

    private void InitBaseTriangle()
    {
        _baseB = new Point(CX + TriR, CY);
        _baseA = new Point(CX - TriR / 2.0, CY - TriR * Math.Sqrt(3) / 2.0);
        _baseC = new Point(CX - TriR / 2.0, CY + TriR * Math.Sqrt(3) / 2.0);
    }

    private void UpdateTriangleGeometry()
    {
        double angle = (!_trackingRotationEnabled || _fixedAngleEnabled) ? _fixedAngle : _hue;
        double rad = (angle + _hueOffset) * Math.PI / 180.0;
        var center = new Point(CX, CY);
        _a = RotatePoint(_baseA, center, rad);
        _b = RotatePoint(_baseB, center, rad);
        _c = RotatePoint(_baseC, center, rad);
        _svTriangle.Points = new PointCollection { _a, _b, _c };
    }

    private static Point RotatePoint(Point pt, Point center, double angle)
    {
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        var v = pt - center;
        return new Point(center.X + v.X * cos - v.Y * sin,
                         center.Y + v.X * sin + v.Y * cos);
    }

    private void UpdateSquareGeometry()
    {
        Canvas.SetLeft(_svSquare, SqLeft);
        Canvas.SetTop(_svSquare, SqTop);
        _svSquare.RenderTransform = new RotateTransform(GetSquareRotationAngle());
    }

    private double GetInnerRotationAngle()
    {
        return (!_trackingRotationEnabled || _fixedAngleEnabled) ? _fixedAngle : _hue;
    }

    private double GetVisualRotationAngle()
    {
        return GetInnerRotationAngle() + _hueOffset;
    }

    private double GetSquareRotationAngle()
    {
        return GetVisualRotationAngle() + 45.0;
    }

    private void RenderHueRing()
    {
        if (_cachedHueRing == null)
            _cachedHueRing = GenerateHueRingBitmap();
        _hueRingRect.Fill = new ImageBrush(_cachedHueRing);
    }

    private static WriteableBitmap GenerateHueRingBitmap()
    {
        var bmp = new WriteableBitmap(CanvasSize, CanvasSize, 96, 96,
                                     PixelFormats.Bgra32, null);
        int stride = CanvasSize * 4;
        var px = new byte[CanvasSize * stride];

        for (int y = 0; y < CanvasSize; y++)
        {
            for (int x = 0; x < CanvasSize; x++)
            {
                double dx = x - CX, dy = y - CY;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                int i = (y * CanvasSize + x) * 4;
                if (dist >= InnerR - 0.5 && dist <= OuterR + 0.5)
                {
                    double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                    if (angle < 0) angle += 360.0;
                    var c = HsvToColor(angle, 1.0, 1.0);
                    double alphaInner = Math.Clamp((dist - (InnerR - 0.5)) * 2.0, 0, 1);
                    double alphaOuter = Math.Clamp(((OuterR + 0.5) - dist) * 2.0, 0, 1);
                    byte a = (byte)(alphaInner * alphaOuter * 255);
                    px[i + 0] = c.B;
                    px[i + 1] = c.G;
                    px[i + 2] = c.R;
                    px[i + 3] = a;
                }
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, CanvasSize, CanvasSize), px, stride, 0);
        return bmp;
    }

    private void RenderInnerGradient()
    {
        if (_mode == WheelMode.Triangle)
            RenderTriangleGradient();
        else
            RenderSquareGradient();
    }

    private void RenderTriangleGradient()
    {
        double minX = Math.Min(_a.X, Math.Min(_b.X, _c.X));
        double minY = Math.Min(_a.Y, Math.Min(_b.Y, _c.Y));
        double maxX = Math.Max(_a.X, Math.Max(_b.X, _c.X));
        double maxY = Math.Max(_a.Y, Math.Max(_b.Y, _c.Y));
        int w = (int)Math.Ceiling(maxX - minX);
        int h = (int)Math.Ceiling(maxY - minY);
        if (w <= 0 || h <= 0) return;

        var bmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        int stride = w * 4;
        var px = new byte[h * stride];
        var pureHue = HsvToColor(_hue, 1.0, 1.0);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var p = new Point(minX + x, minY + y);
                Barycentric(p, _a, _b, _c, out double wA, out double wB, out double wC);
                int i = (y * w + x) * 4;
                if (wA >= -0.01 && wB >= -0.01 && wC >= -0.01)
                {
                    double cWA = Math.Max(wA, 0);
                    double cWB = Math.Max(wB, 0);
                    double cWC = Math.Max(wC, 0);
                    double sum = cWA + cWB + cWC;
                    if (sum > 0) { cWA /= sum; cWB /= sum; }

                    byte r = (byte)Math.Clamp(cWA * 255 + cWB * pureHue.R, 0, 255);
                    byte g = (byte)Math.Clamp(cWA * 255 + cWB * pureHue.G, 0, 255);
                    byte b = (byte)Math.Clamp(cWA * 255 + cWB * pureHue.B, 0, 255);
                    px[i + 0] = b;
                    px[i + 1] = g;
                    px[i + 2] = r;
                    px[i + 3] = 255;
                }
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, w, h), px, stride, 0);
        _svTriangle.Fill = new ImageBrush(bmp)
        {
            Viewbox = new Rect(0, 0, w, h),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(minX, minY, w, h),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill
        };
    }

    private void RenderSquareGradient()
    {
        var bmp = new WriteableBitmap(SqSide, SqSide, 96, 96, PixelFormats.Bgra32, null);
        int stride = SqSide * 4;
        var px = new byte[SqSide * stride];

        for (int y = 0; y < SqSide; y++)
        {
            for (int x = 0; x < SqSide; x++)
            {
                double s = (double)x / (SqSide - 1);
                double v = 1.0 - (double)y / (SqSide - 1);
                var c = HsvToColor(_hue, s, v);
                int i = (y * SqSide + x) * 4;
                px[i + 0] = c.B;
                px[i + 1] = c.G;
                px[i + 2] = c.R;
                px[i + 3] = 255;
            }
        }
        bmp.WritePixels(new Int32Rect(0, 0, SqSide, SqSide), px, stride, 0);
        _svSquare.Fill = new ImageBrush(bmp);
    }

    private void UpdateHueIndicator()
    {
        double rad = (_hue + _hueOffset) * Math.PI / 180.0;
        var dir = new Vector(Math.Cos(rad), Math.Sin(rad));
        var center = new Point(CX, CY);
        var pos = center + dir * ((InnerR + OuterR) / 2.0);

        Canvas.SetLeft(_hueIndicatorShadow, pos.X - 8);
        Canvas.SetTop(_hueIndicatorShadow, pos.Y - 8);
        Canvas.SetLeft(_hueIndicator, pos.X - 7);
        Canvas.SetTop(_hueIndicator, pos.Y - 7);
        _hueIndicator.Fill = new SolidColorBrush(HsvToColor(_hue, 1.0, 1.0));
    }

    private void UpdateSvIndicator()
    {
        if (_mode == WheelMode.Triangle)
            UpdateSvIndicatorTriangle();
        else
            UpdateSvIndicatorSquare();
    }

    private void UpdateSvIndicatorTriangle()
    {
        double wA = _val * (1.0 - _sat);
        double wB = _val * _sat;
        double wC = 1.0 - _val;
        var pos = new Point(
            wA * _a.X + wB * _b.X + wC * _c.X,
            wA * _a.Y + wB * _b.Y + wC * _c.Y);
        PositionIndicator(pos);
    }

    private void UpdateSvIndicatorSquare()
    {
        double localX = SqLeft + _sat * SqSide;
        double localY = SqTop + (1.0 - _val) * SqSide;
        var pos = ToSquareVisualPoint(new Point(localX, localY));
        PositionIndicator(pos);
    }

    private void PositionIndicator(Point pos)
    {
        Canvas.SetLeft(_svIndicatorShadow, pos.X - 8);
        Canvas.SetTop(_svIndicatorShadow, pos.Y - 8);
        Canvas.SetLeft(_svIndicator, pos.X - 7);
        Canvas.SetTop(_svIndicator, pos.Y - 7);
        var color = HsvToColor(_hue, _sat, _val);
        _svIndicator.Fill = new SolidColorBrush(color);
        _svIndicatorShadow.Visibility = Visibility.Visible;
        _svIndicator.Visibility = Visibility.Visible;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return;
        var pos = e.GetPosition(_canvas);
        double dist = (pos - new Point(CX, CY)).Length;
        _isDragging = true;
        _canvas.CaptureMouse();

        if (dist >= InnerR && dist <= OuterR + 4)
        {
            _dragRing = true;
            _dragInner = false;
            UpdateHueFromPoint(pos);
        }
        else if ((_mode == WheelMode.Square && PointInSquare(pos)) ||
                 (_mode == WheelMode.Triangle && dist < InnerR))
        {
            _dragInner = true;
            _dragRing = false;
            UpdateSvFromPoint(pos);
        }
        else
        {
            _isDragging = false;
            _canvas.ReleaseMouseCapture();
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        if (_isDragging)
        {
            if (_dragRing) UpdateHueFromPoint(pos);
            else if (_dragInner) UpdateSvFromPoint(pos);
        }
        else if (_isRightDragging)
        {
            if (!_rightDragMoved && (pos - _rightDownPos).Length > RightDragThreshold)
                _rightDragMoved = true;
            if (_rightDragMoved)
                UpdateFixedAngleFromPoint(pos);
        }
    }

    private void OnRightMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_initialized) return;
        _isRightDragging = true;
        _rightDragMoved = false;
        _rightDownPos = e.GetPosition(_canvas);
        _canvas.CaptureMouse();
    }

    private void OnRightMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isRightDragging = false;
        _canvas.ReleaseMouseCapture();
        if (_rightDragMoved)
        {
            e.Handled = true;
        }
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_rightDragMoved)
        {
            e.Handled = true;
        }
    }

    private void UpdateFixedAngleFromPoint(Point pos)
    {
        double angle = Math.Atan2(pos.Y - CY, pos.X - CX) * 180.0 / Math.PI;
        angle -= _hueOffset;
        FixedAngle = (angle + 720.0) % 360.0;
        FixedAngleEnabled = true;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _dragRing = false;
        _dragInner = false;
        _canvas.ReleaseMouseCapture();
    }

    private void UpdateHueFromPoint(Point pos)
    {
        double screenAngle = Math.Atan2(pos.Y - CY, pos.X - CX) * 180.0 / Math.PI;
        if (screenAngle < 0) screenAngle += 360.0;
        _hue = (screenAngle - _hueOffset + 720.0) % 360.0;
        byte newH = (byte)Math.Clamp(_hue / 360.0 * 255.0, 0, 255);
        if (H == newH)
        {
            UpdateHueIndicator();
            return;
        }
        _updating = true;
        try { H = newH; }
        finally { _updating = false; }
        UpdateTriangleGeometry();
        UpdateSquareGeometry();
        RenderInnerGradient();
        UpdateHueIndicator();
        UpdateSvIndicator();
        OnColorChanged?.Invoke(H, S, V);
    }

    private void UpdateSvFromPoint(Point pos)
    {
        if (_mode == WheelMode.Triangle)
            UpdateSvFromPointTriangle(pos);
        else
            UpdateSvFromPointSquare(pos);
    }

    private void UpdateSvFromPointTriangle(Point pos)
    {
        Barycentric(pos, _a, _b, _c, out double wA, out double wB, out double wC);
        wA = Math.Clamp(wA, 0, 1);
        wB = Math.Clamp(wB, 0, 1);
        wC = Math.Clamp(wC, 0, 1);
        double sum = wA + wB + wC;
        if (sum > 0) { wA /= sum; wB /= sum; wC /= sum; }
        _val = Math.Clamp(wA + wB, 0, 1);
        _sat = _val > 0 ? Math.Clamp(wB / _val, 0, 1) : 0;

        byte newS = (byte)Math.Clamp(_sat * 255.0, 0, 255);
        byte newV = (byte)Math.Clamp(_val * 255.0, 0, 255);
        if (S == newS && V == newV)
        {
            UpdateSvIndicator();
            return;
        }
        _updating = true;
        try { S = newS; V = newV; }
        finally { _updating = false; }
        UpdateSvIndicator();
        OnColorChanged?.Invoke(H, S, V);
    }

    private void UpdateSvFromPointSquare(Point pos)
    {
        var local = ToSquareLocalPoint(pos);
        _sat = Math.Clamp((local.X - SqLeft) / SqSide, 0, 1);
        _val = Math.Clamp(1.0 - (local.Y - SqTop) / SqSide, 0, 1);

        byte newS = (byte)Math.Clamp(_sat * 255.0, 0, 255);
        byte newV = (byte)Math.Clamp(_val * 255.0, 0, 255);
        if (S == newS && V == newV)
        {
            UpdateSvIndicator();
            return;
        }
        _updating = true;
        try { S = newS; V = newV; }
        finally { _updating = false; }
        UpdateSvIndicator();
        OnColorChanged?.Invoke(H, S, V);
    }

    private static void Barycentric(Point p, Point a, Point b, Point c,
        out double wA, out double wB, out double wC)
    {
        double det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (Math.Abs(det) < 1e-9)
        {
            wA = wB = wC = 0;
            return;
        }
        wA = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
        wB = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
        wC = 1.0 - wA - wB;
    }

    private bool PointInSquare(Point pos)
    {
        var local = ToSquareLocalPoint(pos);
        return local.X >= SqLeft && local.X <= SqLeft + SqSide &&
               local.Y >= SqTop && local.Y <= SqTop + SqSide;
    }

    private Point ToSquareVisualPoint(Point local)
    {
        double rad = GetSquareRotationAngle() * Math.PI / 180.0;
        return RotatePoint(local, new Point(CX, CY), rad);
    }

    private Point ToSquareLocalPoint(Point pos)
    {
        double rad = GetSquareRotationAngle() * Math.PI / 180.0;
        return RotatePoint(pos, new Point(CX, CY), -rad);
    }

    internal static Color HsvToColor(double hue, double saturation, double value)
    {
        hue %= 360.0;
        if (hue < 0) hue += 360.0;
        int hi = (int)Math.Floor(hue / 60.0) % 6;
        double f = hue / 60.0 - Math.Floor(hue / 60.0);
        byte v = (byte)Math.Clamp(value * 255.0, 0, 255);
        byte p = (byte)Math.Clamp(v * (1.0 - saturation), 0, 255);
        byte q = (byte)Math.Clamp(v * (1.0 - f * saturation), 0, 255);
        byte t = (byte)Math.Clamp(v * (1.0 - (1.0 - f) * saturation), 0, 255);
        return hi switch
        {
            0 => Color.FromArgb(255, v, t, p),
            1 => Color.FromArgb(255, q, v, p),
            2 => Color.FromArgb(255, p, v, t),
            3 => Color.FromArgb(255, p, q, v),
            4 => Color.FromArgb(255, t, p, v),
            5 => Color.FromArgb(255, v, p, q),
            _ => Colors.White
        };
    }
}
