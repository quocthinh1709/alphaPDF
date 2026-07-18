using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AlphaPDF
{
    /// <summary>
    /// alphaPDF's own print dialog with a working preview.
    /// </summary>
    internal sealed class PrintPreviewWindow : Window
    {
        private readonly BitmapSource[] _pages;
        private readonly int[] _rasterW;
        private readonly int[] _rasterH;

        private readonly List<PrintQueue> _queues = [];
        private PrintQueue? _queue;
        private LocalPrintServer? _server;
        private bool _landscape;
        private int _previewIndex;

        // Các tùy chọn in nâng cao
        private int _alignH = 1;
        private int _alignV = 1;
        private int _scaleMode = 0;
        private double _customPct = 100;
        private TextBox _scaleBox = null!;
        private double _marginPx;
        private int _nUp = 1;
        private bool _duplex;
        private CheckBox _duplexCheck = null!;
        private bool _grayscale;

        private double _areaW = 816;
        private double _areaH = 1056;

        private readonly Grid _previewHost = new();
        private readonly TextBlock _pageLabel = new();
        private ComboBox _printerCombo = null!;
        private TextBox _copiesBox = null!;
        private TextBox _pagesBox = null!;

        public int PrintedPageCount { get; private set; }

        public PrintPreviewWindow(Window? owner, BitmapSource[] pages, int[] rasterW, int[] rasterH)
        {
            _pages = pages;
            _rasterW = rasterW;
            _rasterH = rasterH;

            Title = "alphaPDF - Print";
            Width = 960;
            Height = 720;
            MinWidth = 720;
            MinHeight = 480;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.CanResize;
            Owner = owner;
            WindowStartupLocation = owner != null
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;

            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                ResizeBorderThickness = new Thickness(8),
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            BuildUi();
            LoadPrinters();
            UpdateDuplexAvailability();
            RefreshArea();
            UpdatePreview();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try { _server?.Dispose(); } catch { }
        }

        private static SolidColorBrush R(string key) => (SolidColorBrush)Application.Current.Resources[key];
        private static FontFamily FontUI() => (FontFamily)Application.Current.FindResource("FontUI");
        private static Style S(string key) => (Style)Application.Current.FindResource(key);

        private Style? FindOwnerStyle(string key) => Owner?.TryFindResource(key) as Style;

        private void ApplyComboStyle(ComboBox combo)
        {
            if (FindOwnerStyle("DarkComboBox") is Style s)
                combo.Style = s;
            else
            {
                combo.Foreground = R("TextPrimary");
                combo.BorderBrush = R("BorderDim");
            }
            combo.Background = R("BgCanvas");
        }

        private static void StyleTextBox(TextBox tb)
        {
            tb.BorderThickness = new Thickness(1);
            tb.CaretBrush = R("TextPrimary");
            tb.SelectionBrush = R("AccentDim");
            tb.SelectionTextBrush = R("TextPrimary");

            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            b.AppendChild(sv);
            var ct = new ControlTemplate(typeof(TextBox)) { VisualTree = b };

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            ct.Triggers.Add(disabled);
            tb.Template = ct;
        }

        private static (Func<int> Get, Action<int> Set) NumericField(TextBox box, int min, int max)
        {
            int Get() => int.TryParse(box.Text?.Trim(), out int n) ? Math.Min(Math.Max(n, min), max) : min;
            void Set(int n)
            {
                n = Math.Min(Math.Max(n, min), max);
                box.Text = n.ToString();
                box.CaretIndex = box.Text.Length;
            }
            box.PreviewTextInput += (_, ev) => ev.Handled = !ev.Text.All(char.IsDigit);
            DataObject.AddPastingHandler(box, (_, ev) =>
            {
                if (ev.DataObject.GetData(typeof(string)) is string s && !s.All(char.IsDigit))
                    ev.CancelCommand();
            });
            box.PreviewKeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Up) { Set(Get() + 1); ev.Handled = true; }
                if (ev.Key == Key.Down) { Set(Get() - 1); ev.Handled = true; }
            };
            box.PreviewMouseWheel += (_, ev) => { Set(Get() + (ev.Delta > 0 ? 1 : -1)); ev.Handled = true; };
            box.LostFocus += (_, _) => Set(Get());
            return (Get, Set);
        }

        private static Grid BuildStepper(Func<int> get, Action<int> set)
        {
            var g = new Grid { Width = 18, Margin = new Thickness(-1, 0, 0, 0) };
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            System.Windows.Controls.Primitives.RepeatButton Step(string glyph, int delta, int row)
            {
                var b = new System.Windows.Controls.Primitives.RepeatButton
                {
                    Content = glyph,
                    Padding = new Thickness(0),
                    FontSize = 7,
                    Foreground = R("TextPrimary"),
                    Background = R("BgCanvas"),
                    BorderBrush = R("BorderDim"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Focusable = false
                };
                b.Click += (_, _) => set(get() + delta);
                Grid.SetRow(b, row);
                return b;
            }
            g.Children.Add(Step("▲", +1, 0));
            g.Children.Add(Step("▼", -1, 1));
            return g;
        }

        private void BuildUi()
        {
            var outer = new Border
            {
                Background = R("BgSidebar"),
                BorderBrush = R("BorderDim"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 24,
                    ShadowDepth = 4,
                    Direction = 270,
                    Opacity = 0.4
                }
            };
            var root = new DockPanel();
            outer.Child = root;
            Content = outer;

            var titleBar = new Border
            {
                Background = R("BgPanel"),
                CornerRadius = new CornerRadius(7, 7, 0, 0)
            };
            DockPanel.SetDock(titleBar, Dock.Top);
            titleBar.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            var titleGrid = new Grid();
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var titleText = new TextBlock
            {
                Text = "alphaPDF – Print",
                Foreground = R("TextPrimary"),
                FontWeight = FontWeights.SemiBold,
                FontSize = (double)Application.Current.FindResource("FsDialogTitle"),
                FontFamily = FontUI(),
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(titleText, 0);

            var closeBtn = new Button
            {
                Style = S("StudioIconButton"),
                Content = Application.Current.FindResource("Ico_WinClose")
            };
            closeBtn.Click += (_, _) => { DialogResult = false; Close(); };
            Grid.SetColumn(closeBtn, 1);

            titleGrid.Children.Add(titleText);
            titleGrid.Children.Add(closeBtn);
            titleBar.Child = titleGrid;
            root.Children.Add(titleBar);

            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.Children.Add(body);

            body.Children.Add(BuildSettingsColumn());
            body.Children.Add(BuildPreviewColumn());
        }

        private UIElement BuildSettingsColumn()
        {
            var panel = new StackPanel { Margin = new Thickness(16, 14, 12, 14) };

            panel.Children.Add(Label("Printer"));
            var printerCombo = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(printerCombo);
            printerCombo.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < _queues.Count) { _queue = _queues[i]; RefreshArea(); UpdateDuplexAvailability(); UpdatePreview(); }
            };
            _printerCombo = printerCombo;
            panel.Children.Add(printerCombo);

            panel.Children.Add(Label("Orientation"));
            var orient = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(orient);
            orient.Items.Add("Portrait");
            orient.Items.Add("Landscape");
            orient.SelectedIndex = 0;
            orient.SelectionChanged += (s, _) =>
            {
                _landscape = ((ComboBox)s).SelectedIndex == 1;
                RefreshArea();
                UpdatePreview();
            };
            panel.Children.Add(orient);

            panel.Children.Add(Label("Color"));
            var colorMode = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(colorMode);
            colorMode.Items.Add("Color");
            colorMode.Items.Add("Black & White");
            colorMode.SelectedIndex = 0;
            colorMode.SelectionChanged += (s, _) => _grayscale = ((ComboBox)s).SelectedIndex == 1;
            panel.Children.Add(colorMode);

            panel.Children.Add(Label("Margins"));
            var margins = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(margins);
            var marginOpts = new (string name, double inches)[]
            {
                ("None", 0), ("Narrow (0.25\")", 0.25), ("Normal (0.5\")", 0.5), ("Wide (1\")", 1.0)
            };
            foreach (var (name, _) in marginOpts) margins.Items.Add(name);
            margins.SelectedIndex = 0;
            margins.SelectionChanged += (s, _) =>
            {
                int i = ((ComboBox)s).SelectedIndex;
                if (i >= 0 && i < marginOpts.Length) { _marginPx = marginOpts[i].inches * 96.0; UpdatePreview(); }
            };
            panel.Children.Add(margins);

            panel.Children.Add(Label("Pages per sheet"));
            var nup = new ComboBox { Margin = new Thickness(0, 4, 0, 12), Height = 26 };
            ApplyComboStyle(nup);
            foreach (var n in new[] { "1", "2", "4", "6", "9" }) nup.Items.Add(n);
            nup.SelectedIndex = 0;
            nup.SelectionChanged += (s, _) =>
            {
                _nUp = int.TryParse((string)((ComboBox)s).SelectedItem, out int n) && n > 0 ? n : 1;
                _previewIndex = 0;
                UpdatePreview();
            };
            panel.Children.Add(nup);

            panel.Children.Add(Label("Scale"));
            var scale = new ComboBox { Margin = new Thickness(0, 4, 0, 6), Height = 26 };
            ApplyComboStyle(scale);
            scale.Items.Add("Fit to printable area");
            scale.Items.Add("Actual size");
            scale.Items.Add("Custom");
            scale.SelectedIndex = 0;
            panel.Children.Add(scale);

            _scaleBox = new TextBox
            {
                Text = "100",
                Background = R("BgCanvas"),
                Foreground = R("TextPrimary"),
                BorderBrush = R("BorderDim"),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            StyleTextBox(_scaleBox);
            var (getScale, setScale) = NumericField(_scaleBox, 1, 1000);
            _scaleBox.TextChanged += (s, _) =>
            {
                if (int.TryParse(((TextBox)s).Text?.Trim(), out int p) && p > 0)
                {
                    _customPct = p;
                    if (_scaleMode == 2) UpdatePreview();
                }
            };

            var scaleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true, Visibility = Visibility.Collapsed };
            var scalePct = new TextBlock { Text = "%", Foreground = R("TextSecondary"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            DockPanel.SetDock(scalePct, Dock.Right);
            var scaleSpin = BuildStepper(getScale, setScale);
            DockPanel.SetDock(scaleSpin, Dock.Right);
            scaleRow.Children.Add(scalePct);
            scaleRow.Children.Add(scaleSpin);
            scaleRow.Children.Add(_scaleBox);

            scale.SelectionChanged += (s, _) =>
            {
                _scaleMode = ((ComboBox)s).SelectedIndex;
                if (_scaleMode == 1) { _customPct = 100; _scaleBox.Text = "100"; }
                scaleRow.Visibility = _scaleMode == 2 ? Visibility.Visible : Visibility.Collapsed;
                UpdatePreview();
            };
            panel.Children.Add(scaleRow);

            panel.Children.Add(Label("Copies"));
            _copiesBox = new TextBox
            {
                Text = "1",
                Background = R("BgCanvas"),
                Foreground = R("TextPrimary"),
                BorderBrush = R("BorderDim"),
                Padding = new Thickness(6, 4, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            StyleTextBox(_copiesBox);
            var (getCopies, setCopies) = NumericField(_copiesBox, 1, 9999);
            var copiesSpin = BuildStepper(getCopies, setCopies);
            var copiesRow = new DockPanel { Margin = new Thickness(0, 4, 0, 12), LastChildFill = true };
            DockPanel.SetDock(copiesSpin, Dock.Right);
            copiesRow.Children.Add(copiesSpin);
            copiesRow.Children.Add(_copiesBox);
            panel.Children.Add(copiesRow);

            panel.Children.Add(Label("Pages"));
            _pagesBox = new TextBox
            {
                Text = "",
                Margin = new Thickness(0, 4, 0, 2),
                Background = R("BgCanvas"),
                Foreground = R("TextPrimary"),
                BorderBrush = R("BorderDim"),
                Padding = new Thickness(6, 4, 6, 4)
            };
            StyleTextBox(_pagesBox);
            _pagesBox.TextChanged += (_, _) => { _previewIndex = 0; UpdatePreview(); };
            panel.Children.Add(_pagesBox);
            panel.Children.Add(new TextBlock
            {
                Text = "e.g. 1-3,5  (blank = all)",
                Foreground = R("TextSecondary"),
                FontFamily = FontUI(),
                FontSize = (double)Application.Current.FindResource("FsStatus"),
                Margin = new Thickness(0, 0, 0, 16),
                TextWrapping = TextWrapping.Wrap
            });

            _duplexCheck = new CheckBox { Content = "Print on both sides", Foreground = R("TextPrimary"), Margin = new Thickness(0, 2, 0, 14) };
            _duplexCheck.Checked += (_, _) => _duplex = true;
            _duplexCheck.Unchecked += (_, _) => _duplex = false;
            panel.Children.Add(_duplexCheck);

            var optionsScroller = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = MakeButton("Cancel", false);
            cancel.Click += (_, _) => { DialogResult = false; Close(); };
            var print = MakeButton("Print", true);
            print.Margin = new Thickness(8, 0, 0, 0);
            print.Click += (_, _) => DoPrint();
            btnRow.Children.Add(cancel);
            btnRow.Children.Add(print);

            var btnHost = new Border { Child = btnRow, Padding = new Thickness(16, 8, 12, 12) };

            var column = new Grid();
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(optionsScroller, 0);
            Grid.SetRow(btnHost, 1);
            column.Children.Add(optionsScroller);
            column.Children.Add(btnHost);
            Grid.SetColumn(column, 0);

            return column;
        }

        private UIElement BuildPreviewColumn()
        {
            var wrap = new Border
            {
                Background = R("BgCanvas"),
                Margin = new Thickness(0, 4, 8, 12),
                CornerRadius = new CornerRadius(4)
            };
            Grid.SetColumn(wrap, 1);

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(_previewHost, 0);
            grid.Children.Add(_previewHost);

            var nav = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 8)
            };
            var prev = MakeButton("◀", false);
            prev.Click += (_, _) => { if (_previewIndex > 0) { _previewIndex--; UpdatePreview(); } };
            var next = MakeButton("▶", false);
            next.Click += (_, _) => { if (_previewIndex < SheetCount() - 1) { _previewIndex++; UpdatePreview(); } };
            _pageLabel.Foreground = R("TextPrimary");
            _pageLabel.FontFamily = FontUI();
            _pageLabel.VerticalAlignment = VerticalAlignment.Center;
            _pageLabel.Margin = new Thickness(12, 0, 12, 0);
            _pageLabel.FontSize = (double)Application.Current.FindResource("FsBody");
            nav.Children.Add(prev);
            nav.Children.Add(_pageLabel);
            nav.Children.Add(next);
            Grid.SetRow(nav, 1);
            grid.Children.Add(nav);

            wrap.Child = grid;
            return wrap;
        }

        private static TextBlock Label(string text) => new()
        {
            Text = text,
            Foreground = R("TextPrimary"),
            FontFamily = FontUI(),
            FontSize = (double)Application.Current.FindResource("FsBody"),
            FontWeight = FontWeights.SemiBold
        };

        // ---- Behavior and Layout Algorithms ----------------------------------

        private void LoadPrinters()
        {
            try
            {
                _server = new LocalPrintServer();
                var found = _server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
                foreach (var q in found) _queues.Add(q);
            }
            catch { }

            PrintQueue? def = null;
            try { def = LocalPrintServer.GetDefaultPrintQueue(); } catch { }
            if (def != null && !_queues.Any(q => q.FullName == def.FullName))
                _queues.Insert(0, def);

            foreach (var q in _queues) _printerCombo.Items.Add(q.FullName);

            int sel = def != null ? _queues.FindIndex(q => q.FullName == def.FullName) : 0;
            if (_queues.Count > 0)
            {
                _printerCombo.SelectedIndex = sel >= 0 ? sel : 0;
                _queue = _queues[_printerCombo.SelectedIndex];
            }
        }

        private void UpdateDuplexAvailability()
        {
            bool ok = false;
            try
            {
                var caps = _queue?.GetPrintCapabilities();
                ok = caps?.DuplexingCapability?.Contains(Duplexing.TwoSidedLongEdge) == true;
            }
            catch { }

            if (_duplexCheck is null) return;
            _duplexCheck.IsEnabled = ok;
            if (!ok) { _duplexCheck.IsChecked = false; _duplex = false; }
            _duplexCheck.Opacity = ok ? 1.0 : 0.4;
        }

        private PageMediaSize? MediaSizeForDocument()
        {
            try
            {
                if (_queue is null || _rasterW.Length == 0) return null;
                double pw = _rasterW[0], ph = _rasterH[0];
                if (pw > ph) (pw, ph) = (ph, pw);
                var caps = _queue.GetPrintCapabilities();
                foreach (var ms in caps.PageMediaSizeCapability)
                {
                    if (ms is null || !ms.Width.HasValue || !ms.Height.HasValue) continue;
                    double w = ms.Width.Value, h = ms.Height.Value;
                    if (w > h) (w, h) = (h, w);
                    if (Math.Abs(w - pw) <= 6 && Math.Abs(h - ph) <= 6) return ms;
                }
            }
            catch { }
            return null;
        }

        private void RefreshArea()
        {
            double w = 816, h = 1056;
            try
            {
                if (_queue != null)
                {
                    var pd = new PrintDialog { PrintQueue = _queue };
                    var docMedia = MediaSizeForDocument();
                    if (docMedia != null)
                    {
                        var t = pd.PrintTicket;
                        t.PageMediaSize = docMedia;
                        pd.PrintTicket = t;
                    }
                    if (pd.PrintableAreaWidth > 0 && pd.PrintableAreaHeight > 0)
                    {
                        w = pd.PrintableAreaWidth;
                        h = pd.PrintableAreaHeight;
                    }
                }
            }
            catch { }

            if (_landscape) { if (w < h) (w, h) = (h, w); }
            else { if (w > h) (w, h) = (h, w); }

            _areaW = w;
            _areaH = h;
        }

        private double ScaleFor(int idx, double areaW, double areaH)
        {
            double actual = 1.0;
            return _scaleMode switch
            {
                1 => actual,
                2 => actual * (_customPct / 100.0),
                _ => Math.Min(areaW / _rasterW[idx], areaH / _rasterH[idx])
            };
        }

        private double OffsetH(double areaW, double imgW) => _alignH == 0 ? 0 : _alignH == 2 ? areaW - imgW : (areaW - imgW) / 2;
        private double OffsetV(double areaH, double imgH) => _alignV == 0 ? 0 : _alignV == 2 ? areaH - imgH : (areaH - imgH) / 2;

        private (int cols, int rows) NupGrid() => _nUp switch
        {
            2 => _landscape ? (2, 1) : (1, 2),
            4 => (2, 2),
            6 => _landscape ? (3, 2) : (2, 3),
            9 => (3, 3),
            _ => (1, 1)
        };

        private List<int> SelectedIndices() => ParseRange(_pagesBox.Text, _pages.Length);
        private int SheetCount() => _pages.Length == 0 ? 0 : (SelectedIndices().Count + _nUp - 1) / _nUp;

        private Grid ComposeSheet(List<int> idxs, double aw, double ah)
        {
            var sheet = new Grid
            {
                Width = aw,
                Height = ah,
                Background = Brushes.White,
                ClipToBounds = true,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true
            };
            var canvas = new Canvas();
            double m = _marginPx;

            if (_nUp <= 1)
            {
                if (idxs.Count > 0)
                {
                    int idx = idxs[0];
                    double s = ScaleFor(idx, aw - 2 * m, ah - 2 * m);
                    double iw = _rasterW[idx] * s, ih = _rasterH[idx] * s;

                    if (iw >= (aw - 2 * m) - 1.5) iw = aw - 2 * m + 1;
                    if (ih >= (ah - 2 * m) - 1.5) ih = ah - 2 * m + 1;
                    var img = new Image { Source = _pages[idx], Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + OffsetH(aw - 2 * m, iw));
                    Canvas.SetTop(img, m + OffsetV(ah - 2 * m, ih));
                    canvas.Children.Add(img);
                }
            }
            else
            {
                var (cols, rows) = NupGrid();
                const double gap = 6;
                double cellW = (aw - 2 * m) / cols, cellH = (ah - 2 * m) / rows;
                for (int i = 0; i < idxs.Count && i < cols * rows; i++)
                {
                    int idx = idxs[i];
                    int row = i / cols, col = i % cols;
                    double availW = Math.Max(1, cellW - gap), availH = Math.Max(1, cellH - gap);
                    double s = Math.Min(availW / _rasterW[idx], availH / _rasterH[idx]);
                    double iw = _rasterW[idx] * s, ih = _rasterH[idx] * s;
                    var img = new Image { Source = _pages[idx], Width = iw, Height = ih };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, m + col * cellW + (cellW - iw) / 2);
                    Canvas.SetTop(img, m + row * cellH + (cellH - ih) / 2);
                    canvas.Children.Add(img);
                }
            }

            sheet.Children.Add(canvas);
            return sheet;
        }

        private void UpdatePreview()
        {
            _previewHost.Children.Clear();
            if (_pages.Length == 0) { _pageLabel.Text = "No pages"; return; }

            var selected = SelectedIndices();
            int sheets = Math.Max(1, (selected.Count + _nUp - 1) / _nUp);
            int sheet = Math.Max(0, Math.Min(_previewIndex, sheets - 1));
            _previewIndex = sheet;

            var idxs = new List<int>();
            for (int i = sheet * _nUp; i < Math.Min(selected.Count, sheet * _nUp + _nUp); i++)
                idxs.Add(selected[i]);

            _pageLabel.Text = _nUp > 1
                ? $"Sheet {sheet + 1} of {sheets}"
                : $"Page {(idxs.Count > 0 ? idxs[0] + 1 : 1)} of {_pages.Length}";

            var paper = ComposeSheet(idxs, _areaW, _areaH);
            var vb = new Viewbox
            {
                Child = paper,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(20),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.5 }
            };
            _previewHost.Children.Add(vb);
        }

        private void DoPrint()
        {
            if (_queue == null) return;

            var indices = SelectedIndices();
            if (indices.Count == 0) return;

            int.TryParse(_copiesBox.Text?.Trim(), out int copies);
            if (copies < 1) copies = 1;

            try
            {
                var pd = new PrintDialog { PrintQueue = _queue };
                var ticket = pd.PrintTicket;
                ticket.CopyCount = 1; // AlphaPDF tự điều khiển số lượng Copy để tránh lỗi Duplex
                ticket.PageOrientation = _landscape ? PageOrientation.Landscape : PageOrientation.Portrait;
                if (_duplex) ticket.Duplexing = Duplexing.TwoSidedLongEdge;
                ticket.OutputColor = _grayscale ? OutputColor.Grayscale : OutputColor.Color;

                var docMedia = MediaSizeForDocument();
                if (docMedia != null) ticket.PageMediaSize = docMedia;
                pd.PrintTicket = ticket;

                double aw = pd.PrintableAreaWidth, ah = pd.PrintableAreaHeight;
                if (_landscape) { if (aw < ah) (aw, ah) = (ah, aw); }
                else { if (aw > ah) (aw, ah) = (ah, aw); }
                if (aw <= 0 || ah <= 0) { aw = _areaW; ah = _areaH; }

                var fixedDoc = new FixedDocument();
                int sheetsPerCopy = (indices.Count + _nUp - 1) / _nUp;
                bool padForDuplex = _duplex && copies > 1 && (sheetsPerCopy % 2 == 1);

                for (int copy = 0; copy < copies; copy++)
                {
                    for (int start = 0; start < indices.Count; start += _nUp)
                    {
                        var chunk = indices.Skip(start).Take(_nUp).ToList();

                        var fp = new FixedPage { Width = aw, Height = ah };
                        var sheet = ComposeSheet(chunk, aw, ah);
                        FixedPage.SetLeft(sheet, 0);
                        FixedPage.SetTop(sheet, 0);
                        fp.Children.Add(sheet);
                        fp.Measure(new Size(aw, ah));
                        fp.Arrange(new Rect(new Point(), new Size(aw, ah)));

                        var pc = new PageContent();
                        ((IAddChild)pc).AddChild(fp);
                        fixedDoc.Pages.Add(pc);
                    }

                    // Thêm một trang trắng nếu in Duplex và số trang lẻ để các Copy sau bắt đầu đúng trang mới
                    if (padForDuplex && copy < copies - 1)
                    {
                        var blank = new FixedPage { Width = aw, Height = ah };
                        blank.Measure(new Size(aw, ah));
                        blank.Arrange(new Rect(new Point(), new Size(aw, ah)));
                        var bpc = new PageContent();
                        ((IAddChild)bpc).AddChild(blank);
                        fixedDoc.Pages.Add(bpc);
                    }
                }

                pd.PrintDocument(fixedDoc.DocumentPaginator, "alphaPDF");
                PrintedPageCount = indices.Count;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Print failed:\n{ex.Message}", "alphaPDF", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static List<int> ParseRange(string? text, int count)
        {
            text = text?.Trim() ?? "";
            if (text.Length == 0) return [.. Enumerable.Range(0, count)];

            var set = new SortedSet<int>();
            foreach (var raw in text.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                if (part.Contains('-'))
                {
                    var seg = part.Split('-');
                    if (seg.Length == 2 &&
                        int.TryParse(seg[0].Trim(), out int a) &&
                        int.TryParse(seg[1].Trim(), out int b))
                    {
                        if (a > b) (a, b) = (b, a);
                        for (int i = a; i <= b; i++)
                            if (i >= 1 && i <= count) set.Add(i - 1);
                    }
                }
                else if (int.TryParse(part, out int v))
                {
                    if (v >= 1 && v <= count) set.Add(v - 1);
                }
            }
            return set.Count == 0 ? [.. Enumerable.Range(0, count)] : [.. set];
        }

        private static Button MakeButton(string label, bool primary)
        {
            return new Button
            {
                Content = label,
                Style = S(primary ? "StudioPrimaryButton" : "StudioToolButton"),
                Width = 80
            };
        }
    }
}