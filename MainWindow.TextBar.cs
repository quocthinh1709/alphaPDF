using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using AlphaPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace AlphaPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Text tool settings bar
        // ============================================================

        private static readonly double[] TextFontSizes = [8, 10, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 64, 72];
        private FontFamily _textFontFamily = new FontFamily("Segoe UI");

        private void ApplyTextStyleToActiveBox()
        {
            /*
            if (_activeTextBox is null || _activeTextBox.Tag is TextEditContext) return;
             _activeTextBox.Foreground = new SolidColorBrush(_textColor);
            int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
            double fontCanvas = _textFontSize;
            if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
            {
                double sy = _doc.Pages[pg].Height.Point / rd.h;
                if (sy > 0) fontCanvas = _textFontSize / sy;
            }
            _activeTextBox.FontSize = fontCanvas;
            */

            // Cập nhật TextBox đang active
            if (_activeTextBox != null && !(_activeTextBox.Tag is TextEditContext))
            {
                _activeTextBox.Foreground = new SolidColorBrush(_textColor);

                // Cập nhật Font Family
                if (_textFontFamily != null)
                {
                    _activeTextBox.FontFamily = _textFontFamily;
                }

                int pg = _activeTextBox.Tag is int tp ? tp : PageList.SelectedIndex;
                double fontCanvas = _textFontSize;
                if (_doc is not null && pg >= 0 && _renderDims.TryGetValue(pg, out var rd) && rd.h > 0)
                {
                    double sy = _doc.Pages[pg].Height.Point / rd.h;
                    if (sy > 0) fontCanvas = _textFontSize / sy;
                }
                _activeTextBox.FontSize = fontCanvas;
            }

            // THÊM MỚI: Nếu đang sử dụng công cụ Select và có chọn một TextAnnotation
            else if (_selectedAnnotation is TextAnnotation ta)
            {
                ta.FontSize = _textFontSize;
                ta.SetColor(_textColor);
                // Lưu ý: Nếu model TextAnnotation của bạn có hỗ trợ thuộc tính FontFamily, hãy gán nó ở đây:
                //ta.FontFamily = _textFontFamily.Source;

                // Cần gọi lại hàm render lại canvas hiện tại để cập nhật UI ngay lập tức
                RenderTextAnnotation(ta); //hoặc RenderAllAnnotations(ta.PageIndex);
            }
        }

        private void ShowTextSettings()
        {
            HideTextSettings();

            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };

            var fontLbl = new TextBlock
            {
                Text = "Font:",
                FontFamily = (FontFamily)FindResource("FontUI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            fontLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(fontLbl);

            var fontBox = new ComboBox
            {
                Width = 120,
                Height = 24,
                Style = (Style)FindResource("DarkComboBox"),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };

            // Nạp danh sách font hệ thống
            foreach (var font in Fonts.SystemFontFamilies)
            {
                fontBox.Items.Add(font.Source);
            }

            //fontBox.SelectedItem = _textFontFamily.Source;
            fontBox.Text = _textFontFamily.Source;
            fontBox.SelectionChanged += (_, _) =>
            {
                if (fontBox.SelectedItem is string fontName)
                {
                    _textFontFamily = new FontFamily(fontName);
                    ApplyTextStyleToActiveBox();
                }
            };

            fontBox.LostFocus += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(fontBox.Text))
                {
                    _textFontFamily = new FontFamily(fontBox.Text);
                    ApplyTextStyleToActiveBox();
                }
            };
            panel.Children.Add(fontBox);




            // Font size label
            var sizeLbl = new TextBlock
            {
                Text = "Size:",
                FontFamily = (FontFamily)FindResource("FontUI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(sizeLbl);

            // Font size dropdown
            var sizeBox = new ComboBox
            {
                Width = 64, Height = 24,
                SelectedItem = 12,
                Style = (Style)FindResource("DarkComboBox"),
                IsEditable = true,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            foreach (var size in TextFontSizes)
                sizeBox.Items.Add(size.ToString("0"));
            sizeBox.Text = _textFontSize.ToString("0");
            sizeBox.SelectionChanged += (_, _) =>
            {
                if (sizeBox.SelectedItem is string s && double.TryParse(s, out double v) && v > 0)
                {
                    _textFontSize = v;
                    ApplyTextStyleToActiveBox();
                }
            };
            sizeBox.LostFocus += (_, _) =>
            {
                if (double.TryParse(sizeBox.Text, out double v) && v > 0)
                {
                    _textFontSize = v;
                    ApplyTextStyleToActiveBox();
                }
            };
            panel.Children.Add(sizeBox);

            // Separator
            var sep = new Rectangle { Width = 1, Margin = new Thickness(8, 2, 8, 2) };
            sep.SetResourceReference(Rectangle.FillProperty, "BorderDim");
            panel.Children.Add(sep);

            // Color label
            var colorLbl = new TextBlock
            {
                Text = "Color:",
                FontFamily = (FontFamily)FindResource("FontUI"), FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            panel.Children.Add(colorLbl);

            // Color swatches (reuse same palette as draw tool)
            foreach (var color in SwatchColors)
            {
                var c = color;
                bool isActive = c.R == _textColor.R && c.G == _textColor.G && c.B == _textColor.B;
                var swatch = new Border
                {
                    Width = 18, Height = 18,
                    Background = new SolidColorBrush(c),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand
                };
                if (isActive)
                    swatch.SetResourceReference(Border.BorderBrushProperty, "Accent");
                else
                    swatch.SetResourceReference(Border.BorderBrushProperty, "BorderDim");
                swatch.MouseLeftButtonDown += (_, _) => { _textColor = c; ApplyTextStyleToActiveBox(); ShowTextSettings(); };
                panel.Children.Add(swatch);
            }

            // Custom-color picker affordance ("+"): opens the RGB/eyedropper dialog.
            panel.Children.Add(MakeCustomColorButton(_textColor, picked =>
            {
                _textColor = Color.FromRgb(picked.R, picked.G, picked.B);
                ApplyTextStyleToActiveBox();
                ShowTextSettings();
            }));

            _textSettingsBar = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(4),
                Child = panel,
                Margin = new Thickness(0, 0, 8, 0)
            };
            _textSettingsBar.SetResourceReference(Border.BackgroundProperty,  "BgPanel");
            _textSettingsBar.SetResourceReference(Border.BorderBrushProperty, "BorderDim");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_textSettingsBar, 100);
                previewArea.Children.Add(_textSettingsBar);
            }
        }

        private void HideTextSettings()
        {
            if (_textSettingsBar is not null)
            {
                (PagePreviewPanel.Parent as Grid)?.Children.Remove(_textSettingsBar);
                _textSettingsBar = null;
            }
        }

    }
}
