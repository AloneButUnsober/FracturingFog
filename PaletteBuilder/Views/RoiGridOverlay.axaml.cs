// Views/RoiGridOverlay.axaml.cs
//
// Overlays the preview Image with a configurable rule-of-thirds-style
// grid and a draggable ROI rectangle. Reads/writes the four normalized
// ROI properties on ImagePaletteViewModel so the existing
// auto-extract trigger on RoiX/Y/Width/Height keeps firing on drag-end.
//
// All ROI maths happens in [0,1] × [0,1] image-space — the Canvas
// coordinate system only enters the picture at draw time and pointer
// translation. SourcePixelWidth/Height are wired in for the readout
// label so the user sees both normalized fraction and source-pixel
// extent.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FracturingFog.UI.Avalonia.ViewModels;

namespace PaletteBuilder.Views
{
    public partial class RoiGridOverlay : UserControl
    {
        public static readonly StyledProperty<bool> ShowGridProperty =
            AvaloniaProperty.Register<RoiGridOverlay, bool>(nameof(ShowGrid));
        public static readonly StyledProperty<bool> SnapToGridProperty =
            AvaloniaProperty.Register<RoiGridOverlay, bool>(nameof(SnapToGrid));
        public static readonly StyledProperty<int> GridRowsProperty =
            AvaloniaProperty.Register<RoiGridOverlay, int>(nameof(GridRows), defaultValue: 3);
        public static readonly StyledProperty<int> GridColsProperty =
            AvaloniaProperty.Register<RoiGridOverlay, int>(nameof(GridCols), defaultValue: 3);

        // ROI properties are OneWay (VM → overlay). Drag handlers push back
        // to the VM directly through DataContext (WriteRoiToDataContext)
        // because relying on TwoWay binding write-back through a StyledProperty
        // SetValue chain proved unreliable: the overlay drew the new rect but
        // the VM never saw the change, so the cache key stayed identical and
        // the palette never re-extracted. Direct write closes that gap.
        public static readonly StyledProperty<double> RoiXProperty =
            AvaloniaProperty.Register<RoiGridOverlay, double>(nameof(RoiX));
        public static readonly StyledProperty<double> RoiYProperty =
            AvaloniaProperty.Register<RoiGridOverlay, double>(nameof(RoiY));
        public static readonly StyledProperty<double> RoiWidthProperty =
            AvaloniaProperty.Register<RoiGridOverlay, double>(nameof(RoiWidth));
        public static readonly StyledProperty<double> RoiHeightProperty =
            AvaloniaProperty.Register<RoiGridOverlay, double>(nameof(RoiHeight));

        public static readonly StyledProperty<int> SourcePixelWidthProperty =
            AvaloniaProperty.Register<RoiGridOverlay, int>(nameof(SourcePixelWidth));
        public static readonly StyledProperty<int> SourcePixelHeightProperty =
            AvaloniaProperty.Register<RoiGridOverlay, int>(nameof(SourcePixelHeight));

        public bool ShowGrid     { get => GetValue(ShowGridProperty);     set => SetValue(ShowGridProperty, value); }
        public bool SnapToGrid   { get => GetValue(SnapToGridProperty);   set => SetValue(SnapToGridProperty, value); }
        public int GridRows      { get => GetValue(GridRowsProperty);     set => SetValue(GridRowsProperty, value); }
        public int GridCols      { get => GetValue(GridColsProperty);     set => SetValue(GridColsProperty, value); }
        public double RoiX       { get => GetValue(RoiXProperty);         set => SetValue(RoiXProperty, value); }
        public double RoiY       { get => GetValue(RoiYProperty);         set => SetValue(RoiYProperty, value); }
        public double RoiWidth   { get => GetValue(RoiWidthProperty);     set => SetValue(RoiWidthProperty, value); }
        public double RoiHeight  { get => GetValue(RoiHeightProperty);    set => SetValue(RoiHeightProperty, value); }
        public int SourcePixelWidth  { get => GetValue(SourcePixelWidthProperty);  set => SetValue(SourcePixelWidthProperty, value); }
        public int SourcePixelHeight { get => GetValue(SourcePixelHeightProperty); set => SetValue(SourcePixelHeightProperty, value); }

        private Canvas? _surface;
        private TextBlock? _readout;

        // Drag state — all coordinates are normalized [0,1] image space.
        private enum DragMode { None, NewRect, Move, ResizeNw, ResizeNe, ResizeSw, ResizeSe }
        private DragMode _dragMode = DragMode.None;
        private double _dragStartX, _dragStartY;     // normalized start point of the gesture
        private double _origX, _origY, _origW, _origH; // ROI at drag start

        public RoiGridOverlay()
        {
            InitializeComponent();
            _surface = this.FindControl<Canvas>("Surface");
            SizeChanged += (_, _) => Rebuild();
            PointerPressed += OnPointerPressed;
            PointerMoved += OnPointerMoved;
            PointerReleased += OnPointerReleased;

            // Repaint on any styled-property change.
            PropertyChanged += (_, e) =>
            {
                if (e.Property == ShowGridProperty ||
                    e.Property == GridRowsProperty ||
                    e.Property == GridColsProperty ||
                    e.Property == RoiXProperty ||
                    e.Property == RoiYProperty ||
                    e.Property == RoiWidthProperty ||
                    e.Property == RoiHeightProperty ||
                    e.Property == SourcePixelWidthProperty ||
                    e.Property == SourcePixelHeightProperty)
                {
                    Rebuild();
                }
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        // ── Rendering ───────────────────────────────────────────────────

        private void Rebuild()
        {
            if (_surface == null) return;
            _surface.Children.Clear();

            double w = Bounds.Width;
            double h = Bounds.Height;
            if (w <= 0 || h <= 0) return;

            if (ShowGrid)
            {
                // Halo strokes: a wider dark backing line behind a thin
                // light line. The dark backing reads against bright image
                // regions; the light inner reads against dark image regions.
                // Same idea as a typeface halo / Photoshop guide rule —
                // visible regardless of underlying luminance without any
                // per-image pixel sampling.
                var darkHalo  = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0));
                var lightCore = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF));
                for (int i = 1; i < GridCols; i++)
                {
                    double x = w * i / GridCols;
                    AddHaloLine(new Point(x, 0), new Point(x, h), darkHalo, lightCore);
                }
                for (int i = 1; i < GridRows; i++)
                {
                    double y = h * i / GridRows;
                    AddHaloLine(new Point(0, y), new Point(w, y), darkHalo, lightCore);
                }
            }

            bool hasRoi = RoiWidth > 0 && RoiHeight > 0;
            if (hasRoi)
            {
                double rx = RoiX * w;
                double ry = RoiY * h;
                double rw = RoiWidth * w;
                double rh = RoiHeight * h;

                // Dim outside the ROI with four rectangles around it.
                var dim = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));
                AddRect(0, 0, w, ry, dim);                       // top
                AddRect(0, ry + rh, w, h - (ry + rh), dim);      // bottom
                AddRect(0, ry, rx, rh, dim);                     // left
                AddRect(rx + rw, ry, w - (rx + rw), rh, dim);    // right

                // ROI outline gets the same halo treatment as the grid
                // lines so the boundary stays legible against any image.
                // Dark backing rectangle slightly inflated, then the
                // accent-colour inner rectangle on top.
                _surface.Children.Add(new Rectangle
                {
                    Width = rw + 2,
                    Height = rh + 2,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
                    StrokeThickness = 1,
                    [Canvas.LeftProperty] = rx - 1,
                    [Canvas.TopProperty] = ry - 1,
                });
                _surface.Children.Add(new Rectangle
                {
                    Width = rw,
                    Height = rh,
                    Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xC8, 0x64)),
                    StrokeThickness = 1.5,
                    [Canvas.LeftProperty] = rx,
                    [Canvas.TopProperty] = ry,
                });
            }

            // Readout label (top-left).
            _readout = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC8, 0xC8, 0x64)),
                Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
                Padding = new Thickness(6, 2),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                Text = BuildReadout(),
                [Canvas.LeftProperty] = 4.0,
                [Canvas.TopProperty] = 4.0,
            };
            _surface.Children.Add(_readout);
        }

        private void AddRect(double x, double y, double w, double h, IBrush fill)
        {
            if (w <= 0 || h <= 0) return;
            _surface!.Children.Add(new Rectangle
            {
                Width = w,
                Height = h,
                Fill = fill,
                [Canvas.LeftProperty] = x,
                [Canvas.TopProperty] = y,
            });
        }

        private void AddHaloLine(Point a, Point b, IBrush halo, IBrush core)
        {
            // Dark wider line first; light thin line on top. Two passes is
            // cheaper than per-pixel sampling under the grid and always
            // renders the lines visible regardless of underlying tone.
            _surface!.Children.Add(new Line
            {
                StartPoint = a,
                EndPoint = b,
                Stroke = halo,
                StrokeThickness = 3,
            });
            _surface.Children.Add(new Line
            {
                StartPoint = a,
                EndPoint = b,
                Stroke = core,
                StrokeThickness = 1,
            });
        }

        private string BuildReadout()
        {
            bool hasRoi = RoiWidth > 0 && RoiHeight > 0;
            int cellW = (hasRoi && GridCols > 0) ? Math.Max(1, (int)Math.Round(RoiWidth * GridCols)) : 0;
            int cellH = (hasRoi && GridRows > 0) ? Math.Max(1, (int)Math.Round(RoiHeight * GridRows)) : 0;

            string px = "";
            if (SourcePixelWidth > 0 && SourcePixelHeight > 0 && hasRoi)
            {
                int xPx = (int)(RoiX * SourcePixelWidth);
                int yPx = (int)(RoiY * SourcePixelHeight);
                int wPx = (int)(RoiWidth * SourcePixelWidth);
                int hPx = (int)(RoiHeight * SourcePixelHeight);
                px = $"  ({xPx},{yPx}) {wPx}×{hPx}px";
            }
            return hasRoi
                ? $"X={RoiX:0.00} Y={RoiY:0.00} W={RoiWidth:0.00} H={RoiHeight:0.00}  cells={cellW * cellH} of {GridRows * GridCols}{px}"
                : $"Grid {GridCols}×{GridRows} — drag to set ROI";
        }

        // ── Pointer handling ────────────────────────────────────────────

        private const double EdgeGrabPx = 8.0;

        private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var p = e.GetPosition(this);
            double nx = Math.Clamp(p.X / Bounds.Width, 0, 1);
            double ny = Math.Clamp(p.Y / Bounds.Height, 0, 1);

            _dragStartX = nx;
            _dragStartY = ny;
            _origX = RoiX; _origY = RoiY; _origW = RoiWidth; _origH = RoiHeight;

            _dragMode = HitTest(p);
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        private DragMode HitTest(Point p)
        {
            if (RoiWidth <= 0 || RoiHeight <= 0)
                return DragMode.NewRect;

            double rx = RoiX * Bounds.Width;
            double ry = RoiY * Bounds.Height;
            double rw = RoiWidth * Bounds.Width;
            double rh = RoiHeight * Bounds.Height;

            bool nearLeft   = Math.Abs(p.X - rx) < EdgeGrabPx;
            bool nearRight  = Math.Abs(p.X - (rx + rw)) < EdgeGrabPx;
            bool nearTop    = Math.Abs(p.Y - ry) < EdgeGrabPx;
            bool nearBottom = Math.Abs(p.Y - (ry + rh)) < EdgeGrabPx;

            if (nearLeft  && nearTop)    return DragMode.ResizeNw;
            if (nearRight && nearTop)    return DragMode.ResizeNe;
            if (nearLeft  && nearBottom) return DragMode.ResizeSw;
            if (nearRight && nearBottom) return DragMode.ResizeSe;

            bool insideX = p.X >= rx && p.X <= rx + rw;
            bool insideY = p.Y >= ry && p.Y <= ry + rh;
            if (insideX && insideY) return DragMode.Move;

            return DragMode.NewRect;
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_dragMode == DragMode.None) return;
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var p = e.GetPosition(this);
            double nx = Math.Clamp(p.X / Bounds.Width, 0, 1);
            double ny = Math.Clamp(p.Y / Bounds.Height, 0, 1);

            switch (_dragMode)
            {
                case DragMode.NewRect:
                    {
                        double x0 = Math.Min(_dragStartX, nx);
                        double y0 = Math.Min(_dragStartY, ny);
                        double x1 = Math.Max(_dragStartX, nx);
                        double y1 = Math.Max(_dragStartY, ny);
                        RoiX = x0; RoiY = y0;
                        RoiWidth = x1 - x0; RoiHeight = y1 - y0;
                        break;
                    }
                case DragMode.Move:
                    {
                        double dx = nx - _dragStartX;
                        double dy = ny - _dragStartY;
                        double newX = Math.Clamp(_origX + dx, 0, 1 - _origW);
                        double newY = Math.Clamp(_origY + dy, 0, 1 - _origH);
                        RoiX = newX; RoiY = newY;
                        break;
                    }
                case DragMode.ResizeNw:
                    {
                        double r = _origX + _origW;
                        double b = _origY + _origH;
                        RoiX = Math.Min(nx, r); RoiY = Math.Min(ny, b);
                        RoiWidth = Math.Max(0, r - RoiX);
                        RoiHeight = Math.Max(0, b - RoiY);
                        break;
                    }
                case DragMode.ResizeNe:
                    {
                        double l = _origX;
                        double b = _origY + _origH;
                        RoiX = l; RoiY = Math.Min(ny, b);
                        RoiWidth = Math.Max(0, nx - l);
                        RoiHeight = Math.Max(0, b - RoiY);
                        break;
                    }
                case DragMode.ResizeSw:
                    {
                        double r = _origX + _origW;
                        double t = _origY;
                        RoiX = Math.Min(nx, r); RoiY = t;
                        RoiWidth = Math.Max(0, r - RoiX);
                        RoiHeight = Math.Max(0, ny - t);
                        break;
                    }
                case DragMode.ResizeSe:
                    {
                        double l = _origX;
                        double t = _origY;
                        RoiX = l; RoiY = t;
                        RoiWidth = Math.Max(0, nx - l);
                        RoiHeight = Math.Max(0, ny - t);
                        break;
                    }
            }
            WriteRoiToDataContext();
        }

        private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_dragMode == DragMode.None) return;
            _dragMode = DragMode.None;
            e.Pointer.Capture(null);

            if (SnapToGrid && GridCols > 0 && GridRows > 0)
            {
                double cellW = 1.0 / GridCols;
                double cellH = 1.0 / GridRows;
                double x0 = Math.Round(RoiX / cellW) * cellW;
                double y0 = Math.Round(RoiY / cellH) * cellH;
                double x1 = Math.Round((RoiX + RoiWidth) / cellW) * cellW;
                double y1 = Math.Round((RoiY + RoiHeight) / cellH) * cellH;
                x0 = Math.Clamp(x0, 0, 1); y0 = Math.Clamp(y0, 0, 1);
                x1 = Math.Clamp(x1, 0, 1); y1 = Math.Clamp(y1, 0, 1);
                RoiX = Math.Min(x0, x1);
                RoiY = Math.Min(y0, y1);
                RoiWidth = Math.Abs(x1 - x0);
                RoiHeight = Math.Abs(y1 - y0);
            }

            WriteRoiToDataContext();
        }

        /// <summary>
        /// Push the current ROI rect into the VM. Called after every drag
        /// update so the VM's WhenAnyValue throttle observes a real change
        /// stream — without this, auto-extract never fires because the VM
        /// properties never move off their default (0,0,0,0).
        /// </summary>
        private void WriteRoiToDataContext()
        {
            if (DataContext is ImagePaletteViewModel vm)
            {
                vm.RoiX = RoiX;
                vm.RoiY = RoiY;
                vm.RoiWidth = RoiWidth;
                vm.RoiHeight = RoiHeight;
            }
        }
    }
}
