using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Petglare;

/// <summary>
/// Pet Workshop: turns any picture into a pet. The tools change only a mask over the
/// original (see <see cref="PetImage"/>), so everything can be undone or painted back.
/// </summary>
public partial class WorkshopWindow : Window
{
    private enum Tool { Erase, Restore, Wand, Crop }

    private const int UndoLimit = 20;
    private static readonly string[] OpenFilter =
        ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff"];

    private readonly PetInfo? editing;
    private readonly Action<PetInfo> saved;
    private readonly Preferences look;
    private readonly List<PetImage.Snapshot> undo = new();
    private readonly List<PetImage.Snapshot> redo = new();
    private readonly List<FrameworkElement> brushOnly = new();
    private readonly List<FrameworkElement> wandOnly = new();
    private readonly List<Button> needsPicture = new();
    private PetImage? image;
    private WriteableBitmap? bitmap;
    private Tool tool = Tool.Erase;
    private double brushSize = 36, hardness = 80;
    private int tolerance = 24;
    private bool contiguous = true;
    private bool dirty;
    private bool painting, panning, spaceDown;
    private Point lastImagePoint, panStart, cropStart;
    private Matrix panMatrix;
    private int group;
    private Button? undoButton, redoButton;

    /// <param name="editing">The pet to change, or null for a new one.</param>
    /// <param name="saved">Called with the stored pet after Save.</param>
    /// <param name="look">Bubble style and size for the preview.</param>
    internal WorkshopWindow(PetInfo? editing, Action<PetInfo> saved, Preferences look)
    {
        this.editing = editing;
        this.saved = saved;
        this.look = look.Clone();
        this.look.Opacity = 100;
        this.look.Size = 100;
        Confirm = AskYesNo;
        InitializeComponent();
        Picture.Stretch = Stretch.None;
        Result.Interactive = false;
        PetName.Text = editing?.Name ?? "Benim petim";
        SourceInitialized += (_, _) => UseDarkTitleBar();
        BuildTools();
        BuildBackdrops();
        WireCanvas();
        if (editing is not null && PetLibrary.LoadForEditing(editing) is { } existing) Load(existing, fresh: false);
        UpdateState();
    }

    // ───────────────────────────── Tool panel ─────────────────────────────

    private void BuildTools()
    {
        Heading("Görsel", first: true);
        AddButtons(("Görsel seç…", OpenClick, false), ("Panodan yapıştır", (_, _) => Paste(), false));

        Heading("Araç");
        var tools = new WrapPanel();
        string toolGroup = NewGroup();
        foreach (var (name, value, tip) in new[]
                 {
                     ("Silgi", Tool.Erase, "Sürükleyerek sil (E)"),
                     ("Geri getir", Tool.Restore, "Silinen yeri geri boya (R)"),
                     ("Sihirli değnek", Tool.Wand, "Tıklanan renk bölgesini sil (W)"),
                     ("Kırp", Tool.Crop, "Tutmak istediğin alanı çiz (C)")
                 })
        {
            var option = new RadioButton
            {
                Content = name, GroupName = toolGroup, IsChecked = value == tool, Style = StyleOf("Segment"),
                ToolTip = tip, Margin = new Thickness(0, 0, 4, 4), Tag = value
            };
            option.Checked += (_, _) => SelectTool(value);
            tools.Children.Add(option);
        }
        Tools.Children.Add(new Border
        {
            Background = (Brush)FindResource("Canvas"), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3, 3, 0, 0), Child = tools
        });

        brushOnly.Add(SliderRow("Fırça boyutu", 2, 200, brushSize, v => $"{v} px", v => { brushSize = v; MoveRing(); }));
        brushOnly.Add(SliderRow("Sertlik", 0, 100, hardness, v => $"%{v}", v => hardness = v));
        // Used by the wand and by edge removal, so it stays visible.
        SliderRow("Renk toleransı", 0, 100, tolerance, v => $"%{v}", v => tolerance = v);
        var adjacent = new CheckBox { Content = "Yalnız bitişik pikseller", IsChecked = contiguous,
            Foreground = (Brush)FindResource("Soft"), Margin = new Thickness(0, 8, 0, 0) };
        adjacent.Click += (_, _) => contiguous = adjacent.IsChecked == true;
        Tools.Children.Add(adjacent);
        wandOnly.Add(adjacent);

        Heading("Otomatik arka plan");
        AddButtons(("Kenardan sil (düz arka plan)", (_, _) => Apply(i => i.EdgeErase(tolerance) > 0,
            "Arka plan silindi", "Kenarda silinecek düz bir renk bulunamadı"), true));
        AddAutoTools();

        Heading("Kenar");
        AddButtons(("Kenarı 1 px daralt", (_, _) => Apply(i => { i.Shrink(); return true; }, "Kenar daraltıldı"), true),
            ("Kenarı yumuşat", (_, _) => Apply(i => { i.Feather(1); return true; }, "Kenar yumuşatıldı"), true));

        Heading("Düzen");
        AddButtons(("Boşlukları kırp", (_, _) => Apply(i => i.Trim(), "Boşluklar kırpıldı", "Kırpılacak boşluk yok"), true),
            ("Sağa döndür", (_, _) => Apply(i => { i.RotateRight(); return true; }, null), true),
            ("Yatay çevir", (_, _) => Apply(i => { i.FlipHorizontal(); return true; }, null), true));

        Heading("Geçmiş");
        var row = new WrapPanel();
        undoButton = SmallButton("Geri al", (_, _) => Undo());
        undoButton.ToolTip = "Ctrl+Z";
        redoButton = SmallButton("Yinele", (_, _) => Redo());
        redoButton.ToolTip = "Ctrl+Y";
        var reset = SmallButton("Baştan başla", (_, _) => ResetAll());
        needsPicture.Add(reset);
        row.Children.Add(undoButton);
        row.Children.Add(redoButton);
        row.Children.Add(reset);
        Tools.Children.Add(row);

        SelectTool(tool);
    }

    /// <summary>Extra automatic tools (the background model adds its button here).</summary>
    partial void AddAutoTools();

    private void Heading(string text, bool first = false) => Tools.Children.Add(new TextBlock
    {
        Text = text, Style = (Style)FindResource("ToolHeading"), Margin = new Thickness(0, first ? 4 : 18, 0, 8)
    });

    private void AddButtons(params (string Text, RoutedEventHandler Click, bool NeedsPicture)[] buttons)
    {
        var panel = new WrapPanel();
        foreach (var (text, click, needs) in buttons)
        {
            var button = SmallButton(text, click);
            if (needs) needsPicture.Add(button);
            panel.Children.Add(button);
        }
        Tools.Children.Add(panel);
    }

    private Button SmallButton(string text, RoutedEventHandler click)
    {
        var button = new Button { Content = text, Style = StyleOf("Small") };
        button.Click += click;
        return button;
    }

    private FrameworkElement SliderRow(string title, int min, int max, double value, Func<int, string> format, Action<int> set)
    {
        var label = new TextBlock { Text = format((int)value), Foreground = (Brush)FindResource("Soft"), FontSize = 12 };
        var head = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
        DockPanel.SetDock(label, Dock.Right);
        head.Children.Add(label);
        head.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold });
        var slider = new Slider { Style = StyleOf("Fine"), Minimum = min, Maximum = max, Value = value };
        System.Windows.Automation.AutomationProperties.SetName(slider, title);
        slider.ValueChanged += (_, e) =>
        {
            int v = (int)Math.Round(e.NewValue);
            label.Text = format(v);
            set(v);
        };
        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(slider);
        Tools.Children.Add(panel);
        return panel;
    }

    private void SelectTool(Tool chosen)
    {
        tool = chosen;
        foreach (var element in brushOnly) element.Visibility = chosen is Tool.Erase or Tool.Restore ? Visibility.Visible : Visibility.Collapsed;
        foreach (var element in wandOnly) element.Visibility = chosen == Tool.Wand ? Visibility.Visible : Visibility.Collapsed;
        foreach (var option in FindTools()) option.IsChecked = (Tool)option.Tag == chosen;
        Viewport.Cursor = chosen is Tool.Erase or Tool.Restore ? Cursors.None : Cursors.Cross;
        BrushRing.Visibility = Visibility.Collapsed;
        Hint.Text = chosen switch
        {
            Tool.Erase => "Sürükleyerek sil · [ ] fırça boyutu · tekerlek yakınlaştırır · sağ tık veya Boşluk ile kaydır",
            Tool.Restore => "Silinen yeri geri boya · [ ] fırça boyutu · sağ tık veya Boşluk ile kaydır",
            Tool.Wand => "Silmek istediğin renge tıkla · tolerans benzer renkleri de alır",
            _ => "Tutmak istediğin alanı sürükleyerek çiz"
        };
    }

    private IEnumerable<RadioButton> FindTools() =>
        Tools.Children.OfType<Border>().Select(b => b.Child).OfType<WrapPanel>()
            .SelectMany(p => p.Children.OfType<RadioButton>()).Where(r => r.Tag is Tool);

    private void BuildBackdrops()
    {
        string name = NewGroup();
        foreach (var (label, brush) in new[]
                 {
                     ("Dama", (Brush)FindResource("Checker")),
                     ("Koyu", (Brush)new SolidColorBrush(Color.FromRgb(0x17, 0x1A, 0x2C))),
                     ("Açık", (Brush)new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xF8)))
                 })
        {
            var option = new RadioButton { Content = label, GroupName = name, Style = StyleOf("Segment"),
                IsChecked = label == "Dama", Padding = new Thickness(10, 4, 10, 4), FontSize = 12 };
            option.Checked += (_, _) => Viewport.Background = brush;
            Backdrops.Children.Add(option);
        }
    }

    // ───────────────────────────── Opening a picture ─────────────────────────────

    private void OpenClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Pet için görsel seç",
            Filter = $"Görseller|{string.Join(";", OpenFilter)}|Tüm dosyalar|*.*"
        };
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName);
    }

    private void OpenFile(string path)
    {
        try
        {
            if (new FileInfo(path).Length > PetImage.MaxFileBytes) throw new InvalidDataException("Dosya çok büyük.");
            if (!ConfirmReplace()) return;
            Load(PetImage.FromFile(path), fresh: true);
            if (editing is null && PetName.Text == "Benim petim") PetName.Text = PetLibrary.CleanName(Path.GetFileNameWithoutExtension(path));
            ShowStatus("Görsel açıldı");
        }
        catch (InvalidDataException ex)
        {
            ShowStatus(ex.Message.Contains("büyük", StringComparison.Ordinal)
                ? ex.Message + " En çok 40 megapiksel ve 64 MB."
                : "Bu dosya görsel olarak açılamadı");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            ShowStatus("Bu dosya görsel olarak açılamadı");
        }
    }

    private void Paste()
    {
        try
        {
            if (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList() is { Count: > 0 } files)
            {
                OpenFile(files[0]!);
                return;
            }
            if (!Clipboard.ContainsImage() || Clipboard.GetImage() is not { } pasted)
            {
                ShowStatus("Panoda görsel yok");
                return;
            }
            if (!ConfirmReplace()) return;
            Load(PetImage.FromBitmap(pasted), fresh: true);
            ShowStatus("Görsel yapıştırıldı");
        }
        catch (InvalidDataException ex)
        {
            ShowStatus(ex.Message);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException or NotSupportedException)
        {
            ShowStatus("Panodaki görsel okunamadı");
        }
    }

    /// <summary>Asks a yes/no question; tests replace it so no dialog blocks them.</summary>
    internal Func<string, bool> Confirm { get; set; }

    private bool AskYesNo(string question) => MessageBox.Show(this, question, "Pet Workshop",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>True when nothing unsaved would be lost.</summary>
    internal bool HasUnsavedChanges => dirty && image is not null;

    private bool ConfirmReplace() =>
        !HasUnsavedChanges || Confirm("Mevcut düzenleme silinecek. Yeni görsel açılsın mı?");

    private void Load(PetImage picture, bool fresh)
    {
        image = picture;
        undo.Clear();
        redo.Clear();
        dirty = fresh;
        Empty.Visibility = Visibility.Collapsed;
        Rebuild();
        Dispatcher.BeginInvoke(FitView, System.Windows.Threading.DispatcherPriority.Loaded);
        UpdateState();
    }

    // ───────────────────────────── Drawing the picture ─────────────────────────────

    /// <summary>Recreates the bitmap after the picture's size changed.</summary>
    private void Rebuild()
    {
        if (image is null) return;
        bitmap = new WriteableBitmap(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null);
        Picture.Source = bitmap;
        Picture.Width = image.Width;
        Picture.Height = image.Height;
        Refresh(new Int32Rect(0, 0, image.Width, image.Height));
        UpdateResult();
    }

    /// <summary>Copies one area of the picture, mask applied, to the screen.</summary>
    private void Refresh(Int32Rect area)
    {
        if (image is null || bitmap is null) return;
        int x0 = Math.Max(0, area.X), y0 = Math.Max(0, area.Y);
        int x1 = Math.Min(image.Width, area.X + area.Width), y1 = Math.Min(image.Height, area.Y + area.Height);
        if (x1 <= x0 || y1 <= y0) return;
        int w = x1 - x0, h = y1 - y0;
        var buffer = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y0 + y) * image.Width + x0 + x, o = (y * w + x) * 4;
            buffer[o] = image.Pixels[i * 4];
            buffer[o + 1] = image.Pixels[i * 4 + 1];
            buffer[o + 2] = image.Pixels[i * 4 + 2];
            buffer[o + 3] = image.Mask[i];
        }
        bitmap.WritePixels(new Int32Rect(x0, y0, w, h), buffer, w * 4, 0);
    }

    /// <summary>Draws the finished pet in the desktop preview.</summary>
    private void UpdateResult()
    {
        ImageSource? pet = null;
        if (image is not null && !image.Bounds().IsEmpty) pet = image.ComposePet();
        Result.Visibility = pet is null ? Visibility.Hidden : Visibility.Visible;
        var size = PetView.WindowSize(look);
        Result.Width = size.Width;
        Result.Height = size.Height;
        Result.ApplyLook(look, pet);
        var name = PetLibrary.CleanName(PetName.Text);
        Result.Render(look, [new BubbleModel(name, "Claude düşünüyor", BubbleTone.Normal)], false, 1);
    }

    private void FitView()
    {
        if (image is null) return;
        double vw = Math.Max(1, Viewport.ActualWidth), vh = Math.Max(1, Viewport.ActualHeight);
        double scale = Math.Min(vw / image.Width, vh / image.Height) * 0.9;
        var m = Matrix.Identity;
        m.Scale(scale, scale);
        m.Translate((vw - image.Width * scale) / 2, (vh - image.Height * scale) / 2);
        View.Matrix = m;
        UpdateScaling();
    }

    /// <summary>Smooth when shrunk, crisp pixels when zoomed in for detail work.</summary>
    private void UpdateScaling() => RenderOptions.SetBitmapScalingMode(Picture,
        View.Matrix.M11 >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

    private void ZoomAt(Point at, double factor)
    {
        var m = View.Matrix;
        double scale = m.M11 * factor;
        if (scale < 0.05 || scale > 40) return;
        m.ScaleAt(factor, factor, at.X, at.Y);
        View.Matrix = m;
        UpdateScaling();
        MoveRing();
    }

    // ───────────────────────────── Mouse and keyboard ─────────────────────────────

    private void WireCanvas()
    {
        Viewport.MouseDown += CanvasDown;
        Viewport.MouseMove += CanvasMove;
        Viewport.MouseUp += CanvasUp;
        Viewport.MouseLeave += (_, _) => BrushRing.Visibility = Visibility.Collapsed;
        Viewport.MouseWheel += (_, e) =>
        {
            if (image is null) return;
            ZoomAt(e.GetPosition(Stage), e.Delta > 0 ? 1.15 : 1 / 1.15);
            e.Handled = true;
        };
        Viewport.SizeChanged += (_, e) => { if (e.PreviousSize.Width < 2) FitView(); };
        PreviewKeyDown += KeyPressed;
        PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) spaceDown = false; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) OpenFile(files[0]);
        };
        PetName.TextChanged += (_, _) => { if (image is not null) { dirty = true; UpdateResult(); UpdateState(); } };
        Closing += (_, e) =>
        {
            if (HasUnsavedChanges && !Confirm("Kaydedilmemiş değişiklikler kaybolacak. Kapatılsın mı?"))
                e.Cancel = true;
        };
    }

    private Point ToImage(Point stagePoint)
    {
        var inverse = View.Matrix;
        inverse.Invert();
        return inverse.Transform(stagePoint);
    }

    private void CanvasDown(object sender, MouseButtonEventArgs e)
    {
        if (image is null) return;
        Viewport.Focus();
        var at = e.GetPosition(Stage);
        if (e.ChangedButton is MouseButton.Right or MouseButton.Middle || (e.ChangedButton == MouseButton.Left && spaceDown))
        {
            panning = true;
            panStart = at;
            panMatrix = View.Matrix;
            Viewport.Cursor = Cursors.SizeAll;
            Viewport.CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        var p = ToImage(at);
        switch (tool)
        {
            case Tool.Erase or Tool.Restore:
                Remember();
                painting = true;
                lastImagePoint = p;
                Paint(p, p);
                Viewport.CaptureMouse();
                break;
            case Tool.Wand:
                Apply(i => i.FloodErase((int)p.X, (int)p.Y, tolerance, contiguous) > 0, null, "Bu renkte silinecek piksel yok");
                break;
            case Tool.Crop:
                painting = true;
                cropStart = Clamp(p);
                Viewport.CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    private void CanvasMove(object sender, MouseEventArgs e)
    {
        var at = e.GetPosition(Stage);
        if (panning)
        {
            var m = panMatrix;
            m.Translate(at.X - panStart.X, at.Y - panStart.Y);
            View.Matrix = m;
            return;
        }
        MoveRing(at);
        if (!painting || image is null) return;
        var p = ToImage(at);
        if (tool is Tool.Erase or Tool.Restore)
        {
            Paint(lastImagePoint, p);
            lastImagePoint = p;
        }
        else if (tool == Tool.Crop) ShowCrop(cropStart, Clamp(p));
    }

    private void CanvasUp(object sender, MouseButtonEventArgs e)
    {
        if (panning)
        {
            panning = false;
            Viewport.ReleaseMouseCapture();
            SelectTool(tool);
            return;
        }
        if (!painting || image is null) return;
        painting = false;
        Viewport.ReleaseMouseCapture();
        if (tool == Tool.Crop)
        {
            CropBox.Visibility = Visibility.Collapsed;
            var end = Clamp(ToImage(e.GetPosition(Stage)));
            var rect = new Int32Rect((int)Math.Min(cropStart.X, end.X), (int)Math.Min(cropStart.Y, end.Y),
                (int)Math.Abs(end.X - cropStart.X), (int)Math.Abs(end.Y - cropStart.Y));
            if (rect.Width < 4 || rect.Height < 4) return;
            Apply(i => i.Crop(rect), "Kırpıldı");
            Dispatcher.BeginInvoke(FitView, System.Windows.Threading.DispatcherPriority.Loaded);
            return;
        }
        dirty = true;
        UpdateResult();
        UpdateState();
    }

    private Point Clamp(Point p) => image is null ? p
        : new Point(Math.Clamp(p.X, 0, image.Width), Math.Clamp(p.Y, 0, image.Height));

    private void Paint(Point from, Point to)
    {
        if (image is null) return;
        double radius = brushSize / 2;
        if (!image.Stroke(from, to, radius, hardness / 100.0, tool == Tool.Erase)) return;
        int pad = (int)Math.Ceiling(radius) + 2;
        Refresh(new Int32Rect((int)Math.Min(from.X, to.X) - pad, (int)Math.Min(from.Y, to.Y) - pad,
            (int)Math.Abs(to.X - from.X) + pad * 2, (int)Math.Abs(to.Y - from.Y) + pad * 2));
    }

    private void ShowCrop(Point a, Point b)
    {
        var sa = View.Matrix.Transform(a);
        var sb = View.Matrix.Transform(b);
        Canvas.SetLeft(CropBox, Math.Min(sa.X, sb.X));
        Canvas.SetTop(CropBox, Math.Min(sa.Y, sb.Y));
        CropBox.Width = Math.Abs(sb.X - sa.X);
        CropBox.Height = Math.Abs(sb.Y - sa.Y);
        CropBox.Visibility = Visibility.Visible;
    }

    private Point ringAt;

    private void MoveRing(Point? at = null)
    {
        if (at is { } point) ringAt = point;
        if (image is null || tool is not (Tool.Erase or Tool.Restore) || panning || !Viewport.IsMouseOver)
        {
            BrushRing.Visibility = Visibility.Collapsed;
            return;
        }
        double d = brushSize * View.Matrix.M11;
        BrushRing.Width = BrushRing.Height = Math.Max(4, d);
        Canvas.SetLeft(BrushRing, ringAt.X - BrushRing.Width / 2);
        Canvas.SetTop(BrushRing, ringAt.Y - BrushRing.Height / 2);
        BrushRing.Visibility = Visibility.Visible;
    }

    private void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl: Redo(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.V when ctrl: Paste(); break;
            case Key.D0 or Key.NumPad0 when ctrl: FitView(); break;
            case Key.OemOpenBrackets: brushSize = Math.Max(2, brushSize - 4); SyncBrushSlider(); break;
            case Key.OemCloseBrackets: brushSize = Math.Min(200, brushSize + 4); SyncBrushSlider(); break;
            case Key.E when !ctrl: SelectTool(Tool.Erase); break;
            case Key.R when !ctrl: SelectTool(Tool.Restore); break;
            case Key.W when !ctrl: SelectTool(Tool.Wand); break;
            case Key.C when !ctrl: SelectTool(Tool.Crop); break;
            case Key.Space: spaceDown = true; break;
            default: return;
        }
        e.Handled = true;
    }

    private void SyncBrushSlider()
    {
        if (brushOnly.FirstOrDefault() is StackPanel panel && panel.Children.OfType<Slider>().FirstOrDefault() is { } slider)
            slider.Value = brushSize;
        MoveRing();
    }

    // ───────────────────────────── Edits and history ─────────────────────────────

    private void Remember()
    {
        if (image is null) return;
        undo.Add(image.Save());
        if (undo.Count > UndoLimit) undo.RemoveAt(0);
        redo.Clear();
    }

    /// <summary>Runs one undoable edit; <paramref name="edit"/> returns whether anything changed.</summary>
    private void Apply(Func<PetImage, bool> edit, string? done, string? nothing = null)
    {
        if (image is null) return;
        var before = image.Save();
        int width = image.Width, height = image.Height;
        if (!edit(image))
        {
            if (nothing is not null) ShowStatus(nothing);
            return;
        }
        undo.Add(before);
        if (undo.Count > UndoLimit) undo.RemoveAt(0);
        redo.Clear();
        dirty = true;
        if (image.Width != width || image.Height != height) Rebuild();
        else
        {
            Refresh(new Int32Rect(0, 0, image.Width, image.Height));
            UpdateResult();
        }
        if (done is not null) ShowStatus(done);
        UpdateState();
    }

    private void Undo() => Step(undo, redo, "Geri alındı");
    private void Redo() => Step(redo, undo, "Yinelendi");

    private void Step(List<PetImage.Snapshot> from, List<PetImage.Snapshot> to, string message)
    {
        if (image is null || from.Count == 0) return;
        to.Add(image.Save());
        var snapshot = from[^1];
        from.RemoveAt(from.Count - 1);
        bool resized = snapshot.Width != image.Width || snapshot.Height != image.Height;
        image.Restore(snapshot);
        dirty = true;
        if (resized) { Rebuild(); FitView(); }
        else { Refresh(new Int32Rect(0, 0, image.Width, image.Height)); UpdateResult(); }
        ShowStatus(message);
        UpdateState();
    }

    private void ResetAll()
    {
        if (image is null || undo.Count == 0) return;
        var first = undo[0];
        Remember();
        image.Restore(first);
        dirty = true;
        Rebuild();
        FitView();
        ShowStatus("İlk haline döndü");
        UpdateState();
    }

    private void UpdateState()
    {
        bool has = image is not null;
        foreach (var button in needsPicture) button.IsEnabled = has;
        if (undoButton is not null) undoButton.IsEnabled = undo.Count > 0;
        if (redoButton is not null) redoButton.IsEnabled = redo.Count > 0;
        SaveButton.IsEnabled = has && (dirty || editing is null) && !image!.Bounds().IsEmpty;
        Title = editing is null ? "Pet Workshop · Yeni pet" : $"Pet Workshop · {editing.Name}";
    }

    // ───────────────────────────── Save ─────────────────────────────

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (image is null) return;
        try
        {
            var pet = PetLibrary.Save(editing?.Id, PetName.Text, image);
            dirty = false;
            saved(pet);
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowStatus("Kaydedilemedi: " + ex.Message);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e) => Close();

    private readonly System.Windows.Threading.DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private void ShowStatus(string text)
    {
        Status.Text = text;
        statusTimer.Stop();
        statusTimer.Tick -= ClearStatus;
        statusTimer.Tick += ClearStatus;
        statusTimer.Start();
    }

    private void ClearStatus(object? sender, EventArgs e)
    {
        statusTimer.Stop();
        Status.Text = "";
    }

    private string NewGroup() => $"w{group++}";

    private Style StyleOf(string key) => (Style)FindResource(key);

    /// <summary>Matches the title bar to the window on Windows 11.</summary>
    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        int caption = 0x002C1A17;
        DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
