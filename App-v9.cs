using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ShimmerButterflyCalculator
{
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        internal static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        internal static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        internal const int GWL_EXSTYLE = -20;
        internal const int WS_EX_TRANSPARENT = 0x20;
        internal const int WS_EX_TOOLWINDOW = 0x80;
        internal const int WS_EX_NOACTIVATE = 0x08000000;
        internal static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        internal const uint SWP_NOSIZE = 0x0001;
        internal const uint SWP_NOMOVE = 0x0002;
        internal const uint SWP_NOACTIVATE = 0x0010;
        internal const uint SWP_SHOWWINDOW = 0x0040;

        internal static double IdleSeconds()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(info);
            if (!GetLastInputInfo(ref info)) return 0;
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked(now - info.dwTime) / 1000.0;
        }
    }

    internal static class Theme
    {
        internal static Brush Brush(string value) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        internal static Color Color(string value) { return (Color)ColorConverter.ConvertFromString(value); }

        internal static TextBlock Text(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
        }

        internal static Border Card(UIElement child, string background, double radius, Thickness padding)
        {
            return new Border { Background = Brush(background), CornerRadius = new CornerRadius(radius), Padding = padding, Child = child };
        }

        internal static TextBox Input(string initial)
        {
            TextBox box = new TextBox();
            box.Text = initial;
            box.FontFamily = new FontFamily("Microsoft YaHei UI");
            box.FontSize = 15;
            box.Foreground = Brush("#F8FAFC");
            box.Background = Brush("#182238");
            box.BorderBrush = Brush("#34425E");
            box.BorderThickness = new Thickness(1);
            box.Padding = new Thickness(9, 7, 9, 7);
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.GotKeyboardFocus += delegate { box.SelectAll(); };
            return box;
        }

        internal static Button Button(string text, string background)
        {
            Button button = new Button();
            button.Content = text;
            button.FontFamily = new FontFamily("Microsoft YaHei UI");
            button.FontSize = 12;
            button.Foreground = Brush("#F8FAFC");
            button.Background = Brush(background);
            button.BorderThickness = new Thickness(0);
            button.Padding = new Thickness(10, 6, 10, 6);
            button.Cursor = Cursors.Hand;
            return button;
        }
    }

    internal sealed class RateSnapshot
    {
        public string FetchedAt { get; set; }
        public string DataDate { get; set; }
        public string Provider { get; set; }
        public Dictionary<string, double> Rates { get; set; }
    }

    internal static class RateService
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static RateSnapshot Fetch()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Exception first = null;
            try { return FetchFrankfurter(); }
            catch (Exception ex) { first = ex; }
            try { return FetchExchangeRateApi(); }
            catch (Exception ex) { throw new InvalidOperationException("两个参考汇率服务都暂时无法连接。", new AggregateException(first, ex)); }
        }

        private static string Download(string url)
        {
            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(8);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("ShimmerButterflyCalculator/1.0");
                return client.GetStringAsync(url).GetAwaiter().GetResult();
            }
        }

        private static RateSnapshot FetchFrankfurter()
        {
            string json = Download("https://api.frankfurter.app/latest?from=CNY&to=USD,SGD,MYR");
            Dictionary<string, object> root = (Dictionary<string, object>)Json.DeserializeObject(json);
            Dictionary<string, object> rates = (Dictionary<string, object>)root["rates"];
            return Build("Frankfurter 市场参考", Convert.ToString(root["date"], CultureInfo.InvariantCulture), rates);
        }

        private static RateSnapshot FetchExchangeRateApi()
        {
            string json = Download("https://open.er-api.com/v6/latest/CNY");
            Dictionary<string, object> root = (Dictionary<string, object>)Json.DeserializeObject(json);
            Dictionary<string, object> rates = (Dictionary<string, object>)root["rates"];
            string date = root.ContainsKey("time_last_update_utc") ? Convert.ToString(root["time_last_update_utc"], CultureInfo.InvariantCulture) : DateTimeOffset.Now.ToString("yyyy-MM-dd");
            return Build("公开市场参考", date, rates);
        }

        private static RateSnapshot Build(string provider, string date, Dictionary<string, object> raw)
        {
            Dictionary<string, double> values = new Dictionary<string, double>();
            foreach (string code in new[] { "USD", "SGD", "MYR" })
            {
                if (!raw.ContainsKey(code)) throw new InvalidDataException("缺少 " + code + " 汇率");
                values[code] = Convert.ToDouble(raw[code], CultureInfo.InvariantCulture);
                if (values[code] <= 0) throw new InvalidDataException(code + " 汇率无效");
            }
            return new RateSnapshot { FetchedAt = DateTimeOffset.Now.ToString("o"), DataDate = date, Provider = provider, Rates = values };
        }

        internal static RateSnapshot LoadLatest(string historyFolder)
        {
            if (!Directory.Exists(historyFolder)) return null;
            string[] files = Directory.GetFiles(historyFolder, "rates-*.json");
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = files.Length - 1; i >= 0; i--)
            {
                try
                {
                    RateSnapshot item = Json.Deserialize<RateSnapshot>(File.ReadAllText(files[i]));
                    if (item != null && item.Rates != null && item.Rates.ContainsKey("USD") && item.Rates.ContainsKey("SGD") && item.Rates.ContainsKey("MYR")) return item;
                }
                catch { }
            }
            return null;
        }

        internal static string SaveNew(string historyFolder, RateSnapshot snapshot)
        {
            Directory.CreateDirectory(historyFolder);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string path = Path.Combine(historyFolder, "rates-" + stamp + ".json");
            if (File.Exists(path)) path = Path.Combine(historyFolder, "rates-" + stamp + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, Json.Serialize(snapshot));
            return path;
        }
    }

    internal sealed class CalculatorWindow : Window
    {
        private readonly string historyFolder;
        private readonly TextBox costBox;
        private readonly TextBox tipBox;
        private readonly TextBox peopleBox;
        private readonly TextBox profitBox;
        private readonly TextBlock errorText;
        private readonly TextBlock rmbOutput;
        private readonly TextBox usdOutput;
        private readonly TextBox sgdOutput;
        private readonly TextBox myrOutput;
        private readonly TextBlock tipPerPersonText;
        private readonly TextBlock totalCostText;
        private readonly TextBlock providerText;
        private readonly Button refreshButton;
        private readonly TextBox calcDisplay;
        private readonly Dictionary<string, TextBox> rateBoxes = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, TextBlock> rateStatuses = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, double> referenceRates = new Dictionary<string, double>();
        private readonly HashSet<string> manualRates = new HashSet<string>();
        private bool applyingRate;
        private RateSnapshot latestSnapshot;
        private DispatcherTimer refreshTimer;
        private bool allowClose;
        private decimal calcAccumulator;
        private string calcPendingOperator;
        private bool calcHasAccumulator;
        private bool calcStartNewEntry = true;
        private bool calcError;
        private bool calcProgrammaticChange;
        private string editingCurrencyCode;
        private string editingCurrencyOriginalText;

        internal event EventHandler HiddenByUser;

        internal CalculatorWindow(string appFolder)
        {
            historyFolder = Path.Combine(appFolder, "rate-history");
            Title = "闪蝶售价计算器";
            Width = 458;
            Height = 690;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            FontFamily = new FontFamily("Microsoft YaHei UI");

            Grid shell = new Grid();
            Border outer = new Border { Background = Theme.Brush("#F5121A2B"), BorderBrush = Theme.Brush("#4F5F7D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(18), Child = shell };
            Content = outer;
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
            shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Border title = new Border { Background = Theme.Brush("#171E30"), CornerRadius = new CornerRadius(18, 18, 0, 0) };
            Grid.SetRow(title, 0);
            shell.Children.Add(title);
            Grid titleGrid = new Grid { Margin = new Thickness(16, 0, 10, 0) };
            title.Child = titleGrid;
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            StackPanel heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            heading.Children.Add(Theme.Text("闪蝶 · 售价计算器", 16, "#F8FAFC"));
            heading.Children.Add(Theme.Text("RMB 成本 → USD / SGD / MYR 售价", 10, "#8290A8"));
            titleGrid.Children.Add(heading);
            Button hideButton = Theme.Button("收起", "#25314A");
            hideButton.Margin = new Thickness(0, 12, 0, 12);
            hideButton.Click += delegate { HidePanel(); };
            Grid.SetColumn(hideButton, 1);
            titleGrid.Children.Add(hideButton);
            title.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };

            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(18, 14, 18, 16) };
            Grid.SetRow(scroll, 1);
            shell.Children.Add(scroll);
            StackPanel panel = new StackPanel();
            scroll.Content = panel;

            Grid inputGrid = new Grid();
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            inputGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) inputGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.Children.Add(inputGrid);

            costBox = AddInput(inputGrid, "地接社每人成本（RMB）", "0", 0, 0);
            tipBox = AddInput(inputGrid, "全团小费总额（RMB）", "0", 0, 2);
            peopleBox = AddInput(inputGrid, "总人数", "1", 2, 0);
            profitBox = AddInput(inputGrid, "要加的利润（%）", "20", 2, 2);

            errorText = Theme.Text("", 11, "#FB7185");
            errorText.Margin = new Thickness(2, 8, 0, 0);
            panel.Children.Add(errorText);

            Grid breakdown = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            breakdown.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            breakdown.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            tipPerPersonText = Theme.Text("每人小费：¥0.00", 12, "#CBD5E1");
            totalCostText = Theme.Text("每人总成本：¥0.00", 12, "#CBD5E1");
            totalCostText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(totalCostText, 1);
            breakdown.Children.Add(tipPerPersonText);
            breakdown.Children.Add(totalCostText);
            panel.Children.Add(Theme.Card(breakdown, "#182238", 10, new Thickness(12, 9, 12, 9)));

            Grid rateHeader = new Grid { Margin = new Thickness(0, 15, 0, 7) };
            rateHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rateHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rateHeader.Children.Add(Theme.Text("市场参考汇率（可手动修改）", 12, "#A7B2C6"));
            refreshButton = Theme.Button("↻ 刷新", "#25314A");
            refreshButton.Click += async delegate { await RefreshRates(true); };
            Grid.SetColumn(refreshButton, 1);
            rateHeader.Children.Add(refreshButton);
            panel.Children.Add(rateHeader);

            panel.Children.Add(CreateRateRow("USD", "美金"));
            panel.Children.Add(CreateRateRow("SGD", "新币"));
            panel.Children.Add(CreateRateRow("MYR", "马币"));
            providerText = Theme.Text("等待获取市场参考汇率", 9, "#66758D");
            providerText.Margin = new Thickness(2, 5, 0, 0);
            panel.Children.Add(providerText);

            StackPanel rmbPanel = new StackPanel();
            rmbPanel.Children.Add(Theme.Text("加利润后的人民币基准价", 11, "#8FA6BD"));
            rmbOutput = Theme.Text("¥ 0.00", 28, "#F8FAFC");
            rmbOutput.FontWeight = FontWeights.Bold;
            rmbPanel.Children.Add(rmbOutput);
            Border rmbCard = Theme.Card(rmbPanel, "#1D2740", 14, new Thickness(15, 12, 15, 12));
            rmbCard.Margin = new Thickness(0, 15, 0, 10);
            panel.Children.Add(rmbCard);

            Grid outputs = new Grid();
            outputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            outputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            outputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            usdOutput = AddEditableOutput(outputs, "USD 售价", "$ 0", "USD", 0, "#60A5FA");
            sgdOutput = AddEditableOutput(outputs, "SGD 售价", "S$ 0", "SGD", 2, "#C084FC");
            myrOutput = AddEditableOutput(outputs, "MYR 售价", "RM 0", "MYR", 4, "#4ADE80");
            panel.Children.Add(outputs);
            TextBlock note = Theme.Text("双击任一外币售价即可输入新售价，并反推上方地接社每人成本", 9, "#8290A8");
            note.TextAlignment = TextAlignment.Center;
            note.Margin = new Thickness(0, 9, 0, 0);
            panel.Children.Add(note);

            Expander calcExpander = new Expander { IsExpanded = false, Foreground = Theme.Brush("#E2E8F0"), Margin = new Thickness(0, 12, 0, 0) };
            calcExpander.Header = Theme.Text("加减乘除", 13, "#F8FAFC");
            StackPanel calcPanel = new StackPanel();
            Border calcDisplayCard = Theme.Card(null, "#101827", 10, new Thickness(12, 10, 12, 10));
            calcDisplay = Theme.Input("0");
            calcDisplay.FontSize = 25;
            calcDisplay.FontWeight = FontWeights.Bold;
            calcDisplay.MaxLength = 24;
            calcDisplay.TextAlignment = TextAlignment.Right;
            calcDisplay.HorizontalContentAlignment = HorizontalAlignment.Right;
            calcDisplay.BorderThickness = new Thickness(0);
            calcDisplay.Background = Brushes.Transparent;
            calcDisplay.Padding = new Thickness(0);
            calcDisplay.PreviewTextInput += delegate(object sender, TextCompositionEventArgs e)
            {
                foreach (char c in e.Text) if (!char.IsDigit(c)) { e.Handled = true; return; }
            };
            calcDisplay.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter || e.Key == Key.Return) { CalcEquals(); e.Handled = true; }
                else if (e.Key == Key.Escape) { CalcClear(); e.Handled = true; }
            };
            calcDisplay.TextChanged += delegate
            {
                if (!calcProgrammaticChange && calcDisplay.IsKeyboardFocused)
                {
                    calcStartNewEntry = false;
                    calcError = false;
                }
            };
            calcDisplayCard.Child = calcDisplay;
            calcPanel.Children.Add(calcDisplayCard);
            Grid calcKeys = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            for (int i = 0; i < 4; i++) calcKeys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            calcKeys.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCalcButton(calcKeys, "＋", 0, 0, 1, 1, "#4338CA", delegate { CalcOperator("＋"); });
            AddCalcButton(calcKeys, "－", 0, 1, 1, 1, "#4338CA", delegate { CalcOperator("－"); });
            AddCalcButton(calcKeys, "×", 0, 2, 1, 1, "#4338CA", delegate { CalcOperator("×"); });
            AddCalcButton(calcKeys, "÷", 0, 3, 1, 1, "#4338CA", delegate { CalcOperator("÷"); });
            calcPanel.Children.Add(calcKeys);
            TextBlock calcNote = Theme.Text("键盘输入数字 · 点击运算符 · Enter 出结果 · Esc 清除", 9, "#66758D");
            calcNote.TextAlignment = TextAlignment.Center;
            calcNote.Margin = new Thickness(0, 6, 0, 0);
            calcPanel.Children.Add(calcNote);
            calcExpander.Content = Theme.Card(calcPanel, "#182238", 12, new Thickness(12, 11, 12, 11));
            panel.Children.Add(calcExpander);

            foreach (TextBox box in new[] { costBox, tipBox, peopleBox }) box.TextChanged += delegate { Recalculate(); };
            profitBox.TextChanged += delegate { Recalculate(); };

            latestSnapshot = RateService.LoadLatest(historyFolder);
            if (latestSnapshot != null) ApplyReferenceSnapshot(latestSnapshot, true);
            else
            {
                RateSnapshot defaults = new RateSnapshot { FetchedAt = DateTimeOffset.Now.ToString("o"), DataDate = "临时默认", Provider = "等待联网更新", Rates = new Dictionary<string, double> { { "USD", 0.14 }, { "SGD", 0.18 }, { "MYR", 0.65 } } };
                ApplyReferenceSnapshot(defaults, true);
            }

            Loaded += async delegate
            {
                await RefreshRates(false);
                refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
                refreshTimer.Tick += async delegate { await RefreshRates(false); };
                refreshTimer.Start();
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) { if (!allowClose) { e.Cancel = true; HidePanel(); } };
        }

        private void AddCalcButton(Grid grid, string text, int row, int column, int columnSpan, int rowSpan, string background, RoutedEventHandler click)
        {
            Button button = Theme.Button(text, background);
            button.FontSize = 16;
            button.Margin = new Thickness(3);
            button.Padding = new Thickness(8, 9, 8, 9);
            button.Click += click;
            Grid.SetRow(button, row);
            Grid.SetColumn(button, column);
            Grid.SetColumnSpan(button, columnSpan);
            Grid.SetRowSpan(button, rowSpan);
            grid.Children.Add(button);
        }

        private TextBox AddInput(Grid grid, string label, string initial, int row, int column)
        {
            StackPanel stack = new StackPanel { Margin = new Thickness(0, row == 0 ? 0 : 12, 0, 0) };
            TextBlock caption = Theme.Text(label, 11, "#A7B2C6");
            caption.Margin = new Thickness(2, 0, 0, 5);
            stack.Children.Add(caption);
            TextBox box = Theme.Input(initial);
            stack.Children.Add(box);
            Grid.SetRow(stack, row);
            Grid.SetColumn(stack, column);
            grid.Children.Add(stack);
            return box;
        }

        private Border CreateRateRow(string code, string name)
        {
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBlock label = Theme.Text("1 CNY → " + code, 12, "#E2E8F0");
            row.Children.Add(label);
            TextBox box = Theme.Input("0");
            box.Tag = code;
            box.HorizontalContentAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            TextBlock status = Theme.Text("参考", 9, "#718096");
            status.Margin = new Thickness(8, 0, 4, 0);
            Grid.SetColumn(status, 2);
            row.Children.Add(status);
            Button reset = Theme.Button("恢复", "#25314A");
            reset.Tag = code;
            reset.Padding = new Thickness(7, 5, 7, 5);
            reset.Click += delegate
            {
                manualRates.Remove(code);
                if (referenceRates.ContainsKey(code)) SetRateBox(code, referenceRates[code]);
                UpdateRateStatus(code);
                Recalculate();
            };
            Grid.SetColumn(reset, 3);
            row.Children.Add(reset);
            rateBoxes[code] = box;
            rateStatuses[code] = status;
            box.TextChanged += delegate
            {
                if (!applyingRate)
                {
                    manualRates.Add(code);
                    status.Text = "手动 · " + DateTime.Now.ToString("HH:mm");
                    status.Foreground = Theme.Brush("#FBBF24");
                }
                Recalculate();
            };
            Border card = Theme.Card(row, "#182238", 9, new Thickness(10, 7, 8, 7));
            card.Margin = new Thickness(0, 0, 0, 6);
            return card;
        }

        private TextBlock AddOutput(Grid grid, string label, string initial, int column, string color)
        {
            StackPanel panel = new StackPanel();
            TextBlock caption = Theme.Text(label, 10, "#8FA6BD");
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            TextBlock value = Theme.Text(initial, 20, color);
            value.FontWeight = FontWeights.Bold;
            value.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(caption);
            panel.Children.Add(value);
            Border card = Theme.Card(panel, "#122842", 12, new Thickness(8, 10, 8, 10));
            Grid.SetColumn(card, column);
            grid.Children.Add(card);
            return value;
        }

        private TextBox AddEditableOutput(Grid grid, string label, string initial, string code, int column, string color)
        {
            StackPanel panel = new StackPanel();
            TextBlock caption = Theme.Text(label, 10, "#8FA6BD");
            caption.HorizontalAlignment = HorizontalAlignment.Center;
            TextBox value = new TextBox
            {
                Text = initial,
                Tag = code,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = Theme.Brush(color),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                TextAlignment = TextAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = "双击输入 " + code + " 售价并反推上方成本"
            };
            value.PreviewMouseDoubleClick += delegate(object sender, MouseButtonEventArgs e)
            {
                BeginCurrencyEdit(code, value);
                e.Handled = true;
            };
            value.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter || e.Key == Key.Return) { CommitCurrencyEdit(code, value); e.Handled = true; }
                else if (e.Key == Key.Escape) { CancelCurrencyEdit(value); e.Handled = true; }
            };
            value.LostKeyboardFocus += delegate { if (editingCurrencyCode == code) CommitCurrencyEdit(code, value); };
            panel.Children.Add(caption);
            panel.Children.Add(value);
            Border card = Theme.Card(panel, "#122842", 12, new Thickness(8, 10, 8, 10));
            Grid.SetColumn(card, column);
            grid.Children.Add(card);
            return value;
        }

        private TextBox CurrencyOutput(string code)
        {
            if (code == "USD") return usdOutput;
            if (code == "SGD") return sgdOutput;
            return myrOutput;
        }

        private void BeginCurrencyEdit(string code, TextBox box)
        {
            if (editingCurrencyCode != null && editingCurrencyCode != code)
                CancelCurrencyEdit(CurrencyOutput(editingCurrencyCode));
            if (editingCurrencyCode == code) return;

            editingCurrencyCode = code;
            editingCurrencyOriginalText = box.Text;
            box.IsReadOnly = false;
            box.Cursor = Cursors.IBeam;
            box.Background = Theme.Brush("#1E293B");
            string numeric = (box.Text ?? "")
                .Replace("S$", "")
                .Replace("RM", "")
                .Replace("$", "")
                .Replace(",", "")
                .Replace("—", "")
                .Trim();
            box.Text = numeric;
            box.Focus();
            box.SelectAll();
        }

        private void CommitCurrencyEdit(string code, TextBox box)
        {
            if (editingCurrencyCode != code) return;
            string entered = box.Text;
            string original = editingCurrencyOriginalText;
            editingCurrencyCode = null;
            editingCurrencyOriginalText = null;
            box.IsReadOnly = true;
            box.Cursor = Cursors.Hand;
            box.Background = Brushes.Transparent;

            double foreignPrice;
            if (!TryForeignPrice(entered, out foreignPrice) || foreignPrice < 0 || Math.Abs(foreignPrice - Math.Round(foreignPrice)) > 0.0000001)
            {
                box.Text = original;
                errorText.Text = "请输入大于或等于0的整数售价。";
                return;
            }

            string failure;
            if (!TryReverseGroundCost(code, foreignPrice, out failure))
            {
                box.Text = original;
                errorText.Text = failure;
                return;
            }
            errorText.Text = "";
        }

        private void CancelCurrencyEdit(TextBox box)
        {
            if (editingCurrencyCode == null) return;
            box.Text = editingCurrencyOriginalText;
            editingCurrencyCode = null;
            editingCurrencyOriginalText = null;
            box.IsReadOnly = true;
            box.Cursor = Cursors.Hand;
            box.Background = Brushes.Transparent;
        }

        private bool TryForeignPrice(string text, out double value)
        {
            string cleaned = (text ?? "")
                .Replace("S$", "")
                .Replace("RM", "")
                .Replace("$", "")
                .Replace(",", "")
                .Trim();
            if (double.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out value)) return true;
            return double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        }

        private bool TryReverseGroundCost(string code, double foreignPrice, out string failure)
        {
            failure = "";
            double tip, profit, rate;
            int people;
            if (!TryNumber(tipBox, out tip) || tip < 0) { failure = "请先输入有效的小费总额。"; return false; }
            if (!int.TryParse((peopleBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out people) || people <= 0) { failure = "反推成本前，总人数必须是大于0的整数。"; return false; }
            if (!TryNumber(profitBox, out profit) || profit < 0) { failure = "反推成本前，请先输入有效的利润百分比。"; return false; }
            if (!TryNumber(rateBoxes[code], out rate) || rate <= 0) { failure = code + " 汇率必须是大于0的数字。"; return false; }

            double rmbSale = foreignPrice / rate;
            double totalCost = rmbSale / (1 + profit / 100.0);
            double groundCost = totalCost - tip / people;
            if (double.IsNaN(groundCost) || double.IsInfinity(groundCost)) { failure = "数值过大，无法反推成本。"; return false; }
            if (groundCost < -0.0000001) { failure = "反推结果小于每人小费，请检查售价、小费、人数和利润率。"; return false; }
            if (groundCost < 0) groundCost = 0;

            costBox.Text = groundCost.ToString("0.00", CultureInfo.InvariantCulture);
            return true;
        }

        private void SetForeignOutput(string code, TextBox box, string prefix, double value)
        {
            if (editingCurrencyCode == code) return;
            box.Text = prefix + value.ToString("0");
        }

        private async Task RefreshRates(bool userInitiated)
        {
            refreshButton.IsEnabled = false;
            refreshButton.Content = "获取中…";
            try
            {
                RateSnapshot snapshot = await Task.Run(delegate { return RateService.Fetch(); });
                await Task.Run(delegate { RateService.SaveNew(historyFolder, snapshot); });
                ApplyReferenceSnapshot(snapshot, false);
            }
            catch
            {
                if (latestSnapshot != null) providerText.Text = "联网失败 · 使用最近一次参考汇率 " + DisplayTime(latestSnapshot.FetchedAt);
                else providerText.Text = "联网失败 · 请手动输入汇率";
                if (userInitiated) errorText.Text = "暂时无法刷新汇率，已保留现有数值。";
            }
            finally
            {
                refreshButton.IsEnabled = true;
                refreshButton.Content = "↻ 刷新";
            }
        }

        private void ApplyReferenceSnapshot(RateSnapshot snapshot, bool loadingHistory)
        {
            latestSnapshot = snapshot;
            foreach (string code in new[] { "USD", "SGD", "MYR" })
            {
                if (!snapshot.Rates.ContainsKey(code)) continue;
                referenceRates[code] = snapshot.Rates[code];
                if (!manualRates.Contains(code)) SetRateBox(code, snapshot.Rates[code]);
                UpdateRateStatus(code);
            }
            providerText.Text = (loadingHistory ? "最近保存 · " : "已更新 · ") + snapshot.Provider + " · " + DisplayTime(snapshot.FetchedAt);
            Recalculate();
        }

        private void SetRateBox(string code, double value)
        {
            applyingRate = true;
            rateBoxes[code].Text = value.ToString("0.000000", CultureInfo.InvariantCulture);
            applyingRate = false;
        }

        private void UpdateRateStatus(string code)
        {
            if (manualRates.Contains(code))
            {
                rateStatuses[code].Text = "手动 · " + DateTime.Now.ToString("HH:mm");
                rateStatuses[code].Foreground = Theme.Brush("#FBBF24");
            }
            else
            {
                rateStatuses[code].Text = "参考 · " + (latestSnapshot == null ? "--" : DisplayTime(latestSnapshot.FetchedAt));
                rateStatuses[code].Foreground = Theme.Brush("#718096");
            }
        }

        private string DisplayTime(string value)
        {
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(value, out parsed)) return parsed.LocalDateTime.ToString("MM-dd HH:mm");
            return value ?? "--";
        }

        private bool TryNumber(TextBox box, out double value)
        {
            string text = (box.Text ?? "").Trim().Replace("¥", "").Replace("￥", "").Replace("%", "");
            if (double.TryParse(text, NumberStyles.Number | NumberStyles.AllowDecimalPoint, CultureInfo.CurrentCulture, out value)) return true;
            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value);
        }

        private void Recalculate()
        {
            double cost, tip, profit;
            int people;
            if (!TryNumber(costBox, out cost) || cost < 0) { ShowInvalid("请输入有效的每人地接成本。 "); return; }
            if (!TryNumber(tipBox, out tip) || tip < 0) { ShowInvalid("请输入有效的小费总额。 "); return; }
            if (!int.TryParse((peopleBox.Text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out people) || people <= 0) { ShowInvalid("总人数必须是大于0的整数。 "); return; }
            if (!TryNumber(profitBox, out profit) || profit < 0) { ShowInvalid("利润百分比不能小于0。 "); return; }
            double usdRate, sgdRate, myrRate;
            if (!TryNumber(rateBoxes["USD"], out usdRate) || usdRate <= 0 || !TryNumber(rateBoxes["SGD"], out sgdRate) || sgdRate <= 0 || !TryNumber(rateBoxes["MYR"], out myrRate) || myrRate <= 0)
            {
                ShowInvalid("三个参考汇率都必须是大于0的数字。 ");
                return;
            }
            errorText.Text = "";
            double perTip = tip / people;
            double totalCost = cost + perTip;
            double rmb = totalCost * (1 + profit / 100.0);
            tipPerPersonText.Text = "每人小费：¥" + perTip.ToString("N2");
            totalCostText.Text = "每人总成本：¥" + totalCost.ToString("N2");
            rmbOutput.Text = "¥ " + rmb.ToString("N2");
            SetForeignOutput("USD", usdOutput, "$ ", RoundedForeign(rmb, "USD"));
            SetForeignOutput("SGD", sgdOutput, "S$ ", RoundedForeign(rmb, "SGD"));
            SetForeignOutput("MYR", myrOutput, "RM ", RoundedForeign(rmb, "MYR"));
        }

        private void CalcDigit(string digit)
        {
            if (calcError) CalcClear();
            if (calcStartNewEntry || calcDisplay.Text == "0")
            {
                calcDisplay.Text = digit;
                calcStartNewEntry = false;
                return;
            }
            string plain = (calcDisplay.Text ?? "").Replace(",", "");
            if (plain.TrimStart('-').Length >= 24) return;
            calcDisplay.Text = plain + digit;
        }

        private void CalcOperator(string operation)
        {
            if (calcError) return;
            decimal current;
            if (!TryCalcDisplay(out current)) { CalcShowError("输入错误"); return; }
            try
            {
                if (calcHasAccumulator && calcPendingOperator != null && !calcStartNewEntry)
                    calcAccumulator = CalcApply(calcAccumulator, current, calcPendingOperator);
                else if (!calcHasAccumulator)
                {
                    calcAccumulator = current;
                    calcHasAccumulator = true;
                }
                SetCalcDisplay(FormatCalc(calcAccumulator));
                calcPendingOperator = operation;
                calcStartNewEntry = true;
                calcDisplay.Focus();
                calcDisplay.SelectAll();
            }
            catch (DivideByZeroException) { CalcShowError("不能除以0"); }
            catch (OverflowException) { CalcShowError("数值过大"); }
        }

        private void CalcEquals()
        {
            if (calcError || !calcHasAccumulator || calcPendingOperator == null || calcStartNewEntry) return;
            decimal current;
            if (!TryCalcDisplay(out current)) { CalcShowError("输入错误"); return; }
            try
            {
                decimal result = CalcApply(calcAccumulator, current, calcPendingOperator);
                SetCalcDisplay(FormatCalc(result));
                calcAccumulator = result;
                calcPendingOperator = null;
                calcHasAccumulator = false;
                calcStartNewEntry = true;
                calcDisplay.Focus();
                calcDisplay.SelectAll();
            }
            catch (DivideByZeroException) { CalcShowError("不能除以0"); }
            catch (OverflowException) { CalcShowError("数值过大"); }
        }

        private decimal CalcApply(decimal left, decimal right, string operation)
        {
            if (operation == "＋") return left + right;
            if (operation == "－") return left - right;
            if (operation == "×") return left * right;
            if (operation == "÷")
            {
                if (right == 0) throw new DivideByZeroException();
                return left / right;
            }
            return right;
        }

        private string FormatCalc(decimal value)
        {
            decimal rounded = decimal.Round(value, 0, MidpointRounding.AwayFromZero);
            return rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        private bool TryCalcDisplay(out decimal value)
        {
            return decimal.TryParse((calcDisplay.Text ?? "").Replace(",", ""), NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        private void CalcBackspace()
        {
            if (calcError) { CalcClear(); return; }
            if (calcStartNewEntry) return;
            string value = (calcDisplay.Text ?? "0").Replace(",", "");
            if (value.Length <= 1 || (value.Length == 2 && value[0] == '-')) calcDisplay.Text = "0";
            else calcDisplay.Text = value.Substring(0, value.Length - 1);
        }

        private void CalcClear()
        {
            calcAccumulator = 0;
            calcPendingOperator = null;
            calcHasAccumulator = false;
            calcStartNewEntry = true;
            calcError = false;
            SetCalcDisplay("0");
            calcDisplay.Focus();
            calcDisplay.SelectAll();
        }

        private void CalcShowError(string message)
        {
            SetCalcDisplay(message);
            calcAccumulator = 0;
            calcPendingOperator = null;
            calcHasAccumulator = false;
            calcStartNewEntry = true;
            calcError = true;
            calcDisplay.Focus();
            calcDisplay.SelectAll();
        }

        private void SetCalcDisplay(string value)
        {
            calcProgrammaticChange = true;
            calcDisplay.Text = value;
            calcProgrammaticChange = false;
        }

        private double RoundedForeign(double rmb, string code)
        {
            double rate;
            if (!TryNumber(rateBoxes[code], out rate) || rate <= 0) return 0;
            return Math.Ceiling(rmb * rate);
        }

        private void ShowInvalid(string message)
        {
            errorText.Text = message;
            tipPerPersonText.Text = "每人小费：—";
            totalCostText.Text = "每人总成本：—";
            rmbOutput.Text = "¥ —";
            if (editingCurrencyCode != "USD") usdOutput.Text = "$ —";
            if (editingCurrencyCode != "SGD") sgdOutput.Text = "S$ —";
            if (editingCurrencyCode != "MYR") myrOutput.Text = "RM —";
        }

        internal void HidePanel()
        {
            Hide();
            if (HiddenByUser != null) HiddenByUser(this, EventArgs.Empty);
        }

        internal void PrepareForShutdown() { allowClose = true; }
    }

    internal sealed class Particle
    {
        internal Ellipse Shape;
        internal Point Position;
        internal Vector Velocity;
        internal double Life;
        internal double MaxLife;
        internal double StartSize;
    }

    internal sealed class ParticleWindow : Window
    {
        private readonly Canvas canvas;
        private readonly List<Particle> particles = new List<Particle>();
        private readonly Random random = new Random();
        private readonly string[] colors = { "#FF76D8", "#7DD3FC", "#A78BFA", "#5EE7A8", "#FFFFFF" };

        internal ParticleWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            IsHitTestVisible = false;
            canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };
            Content = canvas;
            SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                int style = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, style | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            };
            Loaded += delegate { FitVirtualScreen(); };
        }

        private void FitVirtualScreen()
        {
            Drawing.Rectangle rect = WinForms.SystemInformation.VirtualScreen;
            PresentationSource source = PresentationSource.FromVisual(this);
            Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Point tl = transform.Transform(new Point(rect.Left, rect.Top));
            Point br = transform.Transform(new Point(rect.Right, rect.Bottom));
            Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
        }

        internal void Emit(Point screenPoint, bool flying)
        {
            if (particles.Count >= 80) return;
            Point local;
            try { local = PointFromScreen(screenPoint); }
            catch { return; }
            double size = flying ? 4 + random.NextDouble() * 6 : 3 + random.NextDouble() * 4;
            Ellipse shape = new Ellipse { Width = size, Height = size, Fill = Theme.Brush(colors[random.Next(colors.Length)]), IsHitTestVisible = false };
            shape.Effect = new System.Windows.Media.Effects.BlurEffect { Radius = flying ? 3 : 2 };
            canvas.Children.Add(shape);
            Particle particle = new Particle
            {
                Shape = shape,
                Position = new Point(local.X + random.NextDouble() * 12 - 6, local.Y + random.NextDouble() * 12 - 6),
                Velocity = new Vector(random.NextDouble() * 20 - 10, 12 + random.NextDouble() * 22),
                MaxLife = 0.8 + random.NextDouble() * 0.7,
                Life = 0,
                StartSize = size
            };
            particles.Add(particle);
        }

        internal void Update(double dt)
        {
            for (int i = particles.Count - 1; i >= 0; i--)
            {
                Particle p = particles[i];
                p.Life += dt;
                if (p.Life >= p.MaxLife)
                {
                    canvas.Children.Remove(p.Shape);
                    particles.RemoveAt(i);
                    continue;
                }
                p.Velocity += new Vector(0, 8 * dt);
                p.Position += p.Velocity * dt;
                double remain = 1 - p.Life / p.MaxLife;
                p.Shape.Opacity = remain;
                p.Shape.Width = p.Shape.Height = Math.Max(0.8, p.StartSize * remain);
                Canvas.SetLeft(p.Shape, p.Position.X);
                Canvas.SetTop(p.Shape, p.Position.Y);
            }
        }
    }

    internal sealed class PetWindow : Window
    {
        private readonly Image sprite;
        private readonly Canvas fallback;
        private readonly RotateTransform rotate;
        private readonly Dictionary<string, List<ImageSource>> frames = new Dictionary<string, List<ImageSource>>();
        private bool dragCandidate;
        private bool isDragging;
        private Native.POINT dragStart;
        private double windowStartLeft;
        private double windowStartTop;
        private IntPtr windowHandle;

        internal event EventHandler ToggleRequested;
        internal event EventHandler ResetRequested;
        internal event EventHandler ExitRequested;
        internal event EventHandler DragStarted;
        internal event EventHandler DragEnded;

        internal PetWindow(string appFolder)
        {
            Width = 112;
            Height = 118;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            Cursor = Cursors.Hand;
            Grid root = new Grid { Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 0.5) };
            rotate = new RotateTransform(0);
            root.RenderTransform = rotate;
            Content = root;
            fallback = CreateFallbackButterfly();
            root.Children.Add(fallback);
            sprite = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(2), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            root.Children.Add(sprite);
            LoadAtlas(Path.Combine(appFolder, "assets", "spritesheet-extended.png"));

            ContextMenu menu = new ContextMenu();
            MenuItem reset = new MenuItem { Header = "回到左下角" };
            MenuItem exit = new MenuItem { Header = "退出闪蝶" };
            reset.Click += delegate { if (ResetRequested != null) ResetRequested(this, EventArgs.Empty); };
            exit.Click += delegate { if (ExitRequested != null) ExitRequested(this, EventArgs.Empty); };
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            ContextMenu = menu;

            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += delegate { dragCandidate = false; };
            SourceInitialized += delegate
            {
                windowHandle = new WindowInteropHelper(this).Handle;
                int style = Native.GetWindowLong(windowHandle, Native.GWL_EXSTYLE);
                Native.SetWindowLong(windowHandle, Native.GWL_EXSTYLE, style | Native.WS_EX_TOOLWINDOW);
                EnsureAlwaysOnTop();
            };
        }

        internal void EnsureAlwaysOnTop()
        {
            if (windowHandle == IntPtr.Zero) windowHandle = new WindowInteropHelper(this).Handle;
            if (windowHandle == IntPtr.Zero) return;
            Topmost = true;
            Native.SetWindowPos(
                windowHandle,
                Native.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        private void LoadAtlas(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                BitmapImage atlas = new BitmapImage();
                atlas.BeginInit();
                atlas.CacheOption = BitmapCacheOption.OnLoad;
                atlas.UriSource = new Uri(path, UriKind.Absolute);
                atlas.EndInit();
                atlas.Freeze();
                frames["idle"] = CropRow(atlas, 0, 6);
                frames["right"] = CropRow(atlas, 1, 8);
                frames["left"] = CropRow(atlas, 2, 8);
                frames["wave"] = CropRow(atlas, 3, 4);
                fallback.Visibility = Visibility.Collapsed;
                sprite.Visibility = Visibility.Visible;
                sprite.Source = frames["idle"][0];
            }
            catch { }
        }

        private List<ImageSource> CropRow(BitmapSource atlas, int row, int count)
        {
            List<ImageSource> result = new List<ImageSource>();
            for (int i = 0; i < count; i++)
            {
                CroppedBitmap crop = new CroppedBitmap(atlas, new Int32Rect(i * 192, row * 208, 192, 208));
                crop.Freeze();
                result.Add(crop);
            }
            return result;
        }

        private Canvas CreateFallbackButterfly()
        {
            Canvas canvas = new Canvas { Width = 108, Height = 112, IsHitTestVisible = false };
            LinearGradientBrush leftBrush = new LinearGradientBrush(Theme.Color("#FF71CE"), Theme.Color("#65E6B4"), 35);
            LinearGradientBrush rightBrush = new LinearGradientBrush(Theme.Color("#7DD3FC"), Theme.Color("#A78BFA"), 145);
            ShapePath leftTop = Wing("M54,53 C38,12 5,10 13,45 C17,63 35,66 54,58 Z", leftBrush);
            ShapePath rightTop = Wing("M54,53 C70,12 103,10 95,45 C91,63 73,66 54,58 Z", rightBrush);
            ShapePath leftBottom = Wing("M53,57 C30,58 15,72 26,96 C39,90 49,74 55,61 Z", new LinearGradientBrush(Theme.Color("#A78BFA"), Theme.Color("#FF71CE"), 70));
            ShapePath rightBottom = Wing("M55,57 C78,58 93,72 82,96 C69,90 59,74 53,61 Z", new LinearGradientBrush(Theme.Color("#5EE7A8"), Theme.Color("#7DD3FC"), 110));
            foreach (ShapePath p in new[] { leftTop, rightTop, leftBottom, rightBottom }) canvas.Children.Add(p);
            Ellipse body = new Ellipse { Width = 13, Height = 48, Fill = Theme.Brush("#4C1D95"), Stroke = Theme.Brush("#FDE68A"), StrokeThickness = 2 };
            Canvas.SetLeft(body, 47.5); Canvas.SetTop(body, 40); canvas.Children.Add(body);
            Ellipse head = new Ellipse { Width = 19, Height = 19, Fill = Theme.Brush("#6D28D9"), Stroke = Theme.Brush("#FDE68A"), StrokeThickness = 2 };
            Canvas.SetLeft(head, 44.5); Canvas.SetTop(head, 31); canvas.Children.Add(head);
            Ellipse eye1 = new Ellipse { Width = 3, Height = 4, Fill = Brushes.White }; Canvas.SetLeft(eye1, 50); Canvas.SetTop(eye1, 38); canvas.Children.Add(eye1);
            Ellipse eye2 = new Ellipse { Width = 3, Height = 4, Fill = Brushes.White }; Canvas.SetLeft(eye2, 56); Canvas.SetTop(eye2, 38); canvas.Children.Add(eye2);
            return canvas;
        }

        private ShapePath Wing(string data, Brush fill)
        {
            return new ShapePath { Data = Geometry.Parse(data), Fill = fill, Stroke = Theme.Brush("#4C1D95"), StrokeThickness = 2.5 };
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                dragCandidate = false;
                if (ToggleRequested != null) ToggleRequested(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
            dragCandidate = true;
            Native.GetCursorPos(out dragStart);
            windowStartLeft = Left;
            windowStartTop = Top;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!dragCandidate || e.LeftButton != MouseButtonState.Pressed || isDragging) return;
            Native.POINT now;
            Native.GetCursorPos(out now);
            if (Math.Abs(now.X - dragStart.X) + Math.Abs(now.Y - dragStart.Y) < 7) return;
            isDragging = true;
            if (DragStarted != null) DragStarted(this, EventArgs.Empty);
            try { DragMove(); } catch { }
            isDragging = false;
            dragCandidate = false;
            if (DragEnded != null) DragEnded(this, EventArgs.Empty);
        }

        internal void SetVisual(string mode, int frame, double angle)
        {
            rotate.Angle = angle;
            if (sprite.Visibility != Visibility.Visible || !frames.ContainsKey(mode)) return;
            List<ImageSource> row = frames[mode];
            sprite.Source = row[Math.Abs(frame) % row.Count];
        }
    }

    internal sealed class PetController
    {
        private readonly string appFolder;
        private readonly PetWindow pet;
        private readonly ParticleWindow particles;
        private readonly CalculatorWindow calculator;
        private readonly DispatcherTimer timer;
        private readonly Random random = new Random();
        private DateTime lastTick;
        private DateTime lastEmission;
        private DateTime nextFlightSpin;
        private DateTime nextTopmostCheck;
        private double homeX;
        private double homeY;
        private bool calculatorOpen;
        private bool near;
        private bool dragging;
        private bool flying;
        private bool returning;
        private bool spinActive;
        private bool spinBlocksMotion;
        private double spinElapsed;
        private double spinDuration;
        private double spinOffset;
        private double pathElapsed;
        private double pathDuration;
        private Point pathStart;
        private Point pathControl1;
        private Point pathControl2;
        private Point pathEnd;
        private double heading;
        private double animationClock;
        private bool cursorInitialized;
        private Native.POINT lastCursor;

        internal PetController(string folder)
        {
            appFolder = folder;
            particles = new ParticleWindow();
            pet = new PetWindow(folder);
            calculator = new CalculatorWindow(folder);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += Tick;
            pet.ToggleRequested += delegate { ToggleCalculator(); };
            pet.ResetRequested += delegate { ResetHome(); };
            pet.ExitRequested += delegate { calculator.PrepareForShutdown(); Application.Current.Shutdown(); };
            pet.DragStarted += delegate { dragging = true; flying = false; returning = false; };
            pet.DragEnded += delegate { dragging = false; homeX = pet.Left; homeY = pet.Top; };
            calculator.HiddenByUser += delegate { calculatorOpen = false; };
        }

        internal void Start()
        {
            particles.Show();
            pet.Show();
            ResetHome();
            lastTick = DateTime.Now;
            lastEmission = DateTime.Now;
            nextFlightSpin = DateTime.Now.AddSeconds(10);
            nextTopmostCheck = DateTime.MinValue;
            cursorInitialized = Native.GetCursorPos(out lastCursor);
            pet.EnsureAlwaysOnTop();
            timer.Start();
        }

        private Rect CurrentWorkArea()
        {
            Point center;
            try { center = pet.PointToScreen(new Point(pet.Width / 2, pet.Height / 2)); }
            catch { center = new Point(0, 0); }
            WinForms.Screen screen = WinForms.Screen.FromPoint(new Drawing.Point((int)center.X, (int)center.Y));
            Drawing.Rectangle area = screen.WorkingArea;
            PresentationSource source = PresentationSource.FromVisual(pet);
            Matrix transform = source != null && source.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice : Matrix.Identity;
            Point tl = transform.Transform(new Point(area.Left, area.Top));
            Point br = transform.Transform(new Point(area.Right, area.Bottom));
            return new Rect(tl, br);
        }

        private void ResetHome()
        {
            Rect area = CurrentWorkArea();
            homeX = area.Left + 14;
            homeY = area.Bottom - pet.Height - 14;
            pet.Left = homeX;
            pet.Top = homeY;
            flying = false;
            returning = false;
        }

        private void ToggleCalculator()
        {
            calculatorOpen = !calculatorOpen;
            if (calculatorOpen)
            {
                flying = false;
                returning = false;
                pet.Left = homeX;
                pet.Top = homeY;
                Rect area = CurrentWorkArea();
                calculator.Left = Math.Min(area.Right - calculator.Width - 14, homeX + pet.Width - 4);
                calculator.Top = Math.Max(area.Top + 14, area.Bottom - calculator.Height - 14);
                calculator.Show();
                calculator.Activate();
            }
            else calculator.HidePanel();
        }

        private void Tick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            double dt = Math.Min(0.05, Math.Max(0.001, (now - lastTick).TotalSeconds));
            lastTick = now;
            if (now >= nextTopmostCheck)
            {
                pet.EnsureAlwaysOnTop();
                nextTopmostCheck = now.AddMilliseconds(750);
            }
            particles.Update(dt);
            if (dragging) return;

            Point petScreen;
            try { petScreen = pet.PointToScreen(new Point(pet.Width / 2, pet.Height / 2)); }
            catch { return; }
            Native.POINT cursor;
            Native.GetCursorPos(out cursor);
            bool mouseMoved = cursorInitialized && (cursor.X != lastCursor.X || cursor.Y != lastCursor.Y);
            lastCursor = cursor;
            cursorInitialized = true;

            // While free-flying, any mouse movement immediately starts the smooth trip home.
            // Cancel an optional decorative spin so the return is never delayed.
            if (mouseMoved && flying)
            {
                spinActive = false;
                spinBlocksMotion = false;
                spinOffset = 0;
                RecordMouseReturn();
                BeginReturn();
            }

            double dx = cursor.X - petScreen.X;
            double dy = cursor.Y - petScreen.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (!near && distance <= 110)
            {
                near = true;
            }
            else if (near && distance >= 150)
            {
                near = false;
                BeginSpin(true);
                if (flying && Native.IdleSeconds() < 10) BeginReturn();
            }

            if (calculatorOpen)
            {
                pet.Left = homeX;
                pet.Top = homeY;
                flying = false;
                returning = false;
            }
            else if (returning)
            {
                // A return triggered by user activity must continue even if the pointer
                // crosses the butterfly's proximity radius on the way home.
                UpdateReturn(dt);
            }
            else if (!near && !spinBlocksMotion)
            {
                double idle = Native.IdleSeconds();
                if (idle >= 10 && !flying && !returning) BeginFlight();
                if (idle < 10 && flying) BeginReturn();
                if (flying) UpdateFlight(dt);
                else if (returning) UpdateReturn(dt);
            }

            if (!near || returning) animationClock += dt;
            if (spinActive && !near) UpdateSpin(dt);
            string mode = (flying || returning) ? (Math.Cos(heading * Math.PI / 180) >= 0 ? "right" : "left") : (calculatorOpen ? "wave" : "idle");
            int frame = mode == "wave" ? (int)(animationClock * 6) % 4 : (mode == "idle" ? (int)(animationClock * 7) % 6 : (int)(animationClock * 10) % 8);
            pet.SetVisual(mode, frame, heading + spinOffset);

            if (!near || returning)
            {
                double interval = (flying || returning) ? 0.07 : 0.18;
                if ((now - lastEmission).TotalSeconds >= interval)
                {
                    particles.Emit(petScreen, flying || returning);
                    lastEmission = now;
                }
            }
        }

        private void RecordMouseReturn()
        {
            try
            {
                string folder = Path.Combine(appFolder, "behavior-history");
                Directory.CreateDirectory(folder);
                DateTime now = DateTime.Now;
                string name = "mouse-return-" + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
                string payload = "{\"event\":\"mouse-movement-return\",\"time\":\"" + now.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture) + "\",\"fromX\":" + pet.Left.ToString("0.0", CultureInfo.InvariantCulture) + ",\"fromY\":" + pet.Top.ToString("0.0", CultureInfo.InvariantCulture) + ",\"homeX\":" + homeX.ToString("0.0", CultureInfo.InvariantCulture) + ",\"homeY\":" + homeY.ToString("0.0", CultureInfo.InvariantCulture) + "}";
                File.WriteAllText(Path.Combine(folder, name), payload);
            }
            catch { }
        }

        private void BeginSpin(bool blockMotion)
        {
            spinActive = true;
            spinBlocksMotion = blockMotion;
            spinElapsed = 0;
            spinDuration = 0.65;
            spinOffset = 0;
        }

        private void UpdateSpin(double dt)
        {
            spinElapsed += dt;
            double t = Math.Min(1, spinElapsed / spinDuration);
            double eased = t < 0.5 ? 2 * t * t : -1 + (4 - 2 * t) * t;
            spinOffset = 360 * eased;
            if (t >= 1)
            {
                spinActive = false;
                spinBlocksMotion = false;
                spinOffset = 0;
                if (!calculatorOpen && Native.IdleSeconds() < 10 && (flying || returning)) BeginReturn();
            }
        }

        private void BeginFlight()
        {
            flying = true;
            returning = false;
            ChooseFlightPath();
            nextFlightSpin = DateTime.Now.AddSeconds(8 + random.NextDouble() * 7);
        }

        private void ChooseFlightPath()
        {
            Rect area = CurrentWorkArea();
            pathStart = new Point(pet.Left, pet.Top);
            double marginX = pet.Width + 24;
            double marginY = pet.Height + 24;
            pathEnd = new Point(area.Left + 24 + random.NextDouble() * Math.Max(40, area.Width - marginX), area.Top + 24 + random.NextDouble() * Math.Max(40, area.Height - marginY));
            double minX = area.Left + 12;
            double maxX = area.Right - pet.Width - 12;
            double minY = area.Top + 12;
            double maxY = area.Bottom - pet.Height - 12;
            pathControl1 = new Point(Clamp(pathStart.X + (random.NextDouble() * 360 - 180), minX, maxX), Clamp(pathStart.Y + (random.NextDouble() * 260 - 130), minY, maxY));
            pathControl2 = new Point(Clamp(pathEnd.X + (random.NextDouble() * 360 - 180), minX, maxX), Clamp(pathEnd.Y + (random.NextDouble() * 260 - 130), minY, maxY));
            pathElapsed = 0;
            pathDuration = 2.5 + random.NextDouble() * 2.5;
        }

        private void UpdateFlight(double dt)
        {
            pathElapsed += dt;
            double t = Math.Min(1, pathElapsed / pathDuration);
            Point pos = Bezier(pathStart, pathControl1, pathControl2, pathEnd, t);
            Point ahead = Bezier(pathStart, pathControl1, pathControl2, pathEnd, Math.Min(1, t + 0.01));
            heading = Math.Atan2(ahead.Y - pos.Y, ahead.X - pos.X) * 180 / Math.PI;
            pet.Left = pos.X;
            pet.Top = pos.Y;
            if (DateTime.Now >= nextFlightSpin && !spinActive)
            {
                BeginSpin(false);
                nextFlightSpin = DateTime.Now.AddSeconds(8 + random.NextDouble() * 7);
            }
            if (t >= 1) ChooseFlightPath();
        }

        private void BeginReturn()
        {
            flying = false;
            returning = true;
            pathStart = new Point(pet.Left, pet.Top);
            pathEnd = new Point(homeX, homeY);
            pathControl1 = new Point(pathStart.X, pathStart.Y - 90);
            pathControl2 = new Point(pathEnd.X + 90, pathEnd.Y - 70);
            pathElapsed = 0;
            pathDuration = 1.25;
        }

        private void UpdateReturn(double dt)
        {
            pathElapsed += dt;
            double t = Math.Min(1, pathElapsed / pathDuration);
            double smooth = t * t * (3 - 2 * t);
            Point pos = Bezier(pathStart, pathControl1, pathControl2, pathEnd, smooth);
            Point ahead = Bezier(pathStart, pathControl1, pathControl2, pathEnd, Math.Min(1, smooth + 0.01));
            heading = Math.Atan2(ahead.Y - pos.Y, ahead.X - pos.X) * 180 / Math.PI;
            pet.Left = pos.X;
            pet.Top = pos.Y;
            if (t >= 1)
            {
                returning = false;
                pet.Left = homeX;
                pet.Top = homeY;
                heading = 0;
            }
        }

        private Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
        {
            double u = 1 - t;
            double x = u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X;
            double y = u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y;
            return new Point(x, y);
        }

        private double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal static class Program
    {
        private static Mutex mutex;

        [STAThread]
        internal static void Main()
        {
            bool created;
            mutex = new Mutex(true, "Global\\ShimmerButterflyPriceCalculator", out created);
            if (!created) return;
            string folder = AppDomain.CurrentDomain.BaseDirectory;
            Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            PetController controller = new PetController(folder);
            app.Startup += delegate { controller.Start(); };
            app.Exit += delegate { if (mutex != null) { mutex.ReleaseMutex(); mutex.Dispose(); } };
            app.Run();
        }
    }
}
