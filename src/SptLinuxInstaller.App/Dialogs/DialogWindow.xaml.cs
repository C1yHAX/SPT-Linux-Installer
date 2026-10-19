using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SptLinuxInstaller.App.Dialogs;

public partial class DialogWindow : Window
{
    private readonly MessageBoxResult _dismissResult;
    private readonly MessageBoxResult _defaultResult;
    private readonly Button _defaultButton;
    private MessageBoxResult _result;

    private DialogWindow(Window owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, MessageBoxResult defaultResult)
    {
        InitializeComponent();
        Owner = owner;
        Title = title;
        DialogTitle.Text = title;
        DialogTitle.ToolTip = title;
        MessageText.Text = message;
        AutomationProperties.SetName(this, title);

        var supported = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => [MessageBoxResult.OK, MessageBoxResult.Cancel],
            MessageBoxButton.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
            _ => throw new ArgumentOutOfRangeException(nameof(buttons), "Supported buttons: OK, OKCancel, YesNo.")
        };
        _dismissResult = supported[^1];
        _defaultResult = supported.Contains(defaultResult) ? defaultResult : _dismissResult;
        _result = _dismissResult;
        ConfigureButton(AcceptButton, supported[0]);
        if (supported.Length > 1) ConfigureButton(RejectButton, supported[1]);
        else RejectButton.Visibility = Visibility.Collapsed;
        _defaultButton = _defaultResult == supported[0] ? AcceptButton : RejectButton;
        _defaultButton.IsDefault = true;
        _defaultButton.Style = (Style)FindResource("DialogDefaultAction");
        ConfigureIcon(icon);
        ConstrainToOwnerMonitor(owner);
        ContentRendered += (_, _) =>
        {
            _defaultButton.Focus();
            Keyboard.Focus(_defaultButton);
        };
    }

    public static MessageBoxResult Show(Window owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (!owner.Dispatcher.CheckAccess())
            return owner.Dispatcher.Invoke(() => Show(owner, message, title, buttons, icon, defaultResult));
        var dialog = new DialogWindow(owner, message ?? "", title ?? "", buttons, icon, defaultResult);
        dialog.ShowDialog();
        return dialog._result;
    }

    private static void ConfigureButton(Button button, MessageBoxResult result)
    {
        var label = result switch
        {
            MessageBoxResult.OK => "_OK",
            MessageBoxResult.Cancel => "_Abbrechen",
            MessageBoxResult.Yes => "_Ja",
            MessageBoxResult.No => "_Nein",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
        button.Content = label;
        button.Tag = result;
        AutomationProperties.SetName(button, label.Replace("_", "", StringComparison.Ordinal));
    }

    private void ConfigureIcon(MessageBoxImage icon)
    {
        var presentation = icon switch
        {
            MessageBoxImage.Error => ("!", "Fehler", "#F19B9B", "#422C32"),
            MessageBoxImage.Warning => ("!", "Warnung", "#E8C980", "#3D3729"),
            MessageBoxImage.Question => ("?", "Frage", "#8DBCE6", "#263B4C"),
            MessageBoxImage.Information => ("i", "Information", "#7BD6B5", "#243B36"),
            _ => ("", "", "#7BD6B5", "#243B36")
        };
        if (presentation.Item1.Length == 0) IconPanel.Visibility = Visibility.Collapsed;
        IconText.Text = presentation.Item1;
        IconText.Foreground = Brush(presentation.Item3);
        IconPanel.Background = Brush(presentation.Item4);
        AutomationProperties.SetName(IconPanel, presentation.Item2);
    }

    private static SolidColorBrush Brush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void ConstrainToOwnerMonitor(Window owner)
    {
        var available = SystemParameters.WorkArea.Size;
        var handle = new WindowInteropHelper(owner).Handle;
        if (handle != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(handle, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(owner);
                available = new Size((info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                    (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
            }
        }
        MaxWidth = Math.Max(240, Math.Min(620, available.Width - 32));
        Width = MaxWidth;
        MaxHeight = Math.Max(150, available.Height - 32);
        MessageScroll.MaxHeight = Math.Max(24, MaxHeight - 190);
    }

    private void Finish(MessageBoxResult result)
    {
        _result = result;
        Close();
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessageBoxResult result }) Finish(result);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Finish(_dismissResult);

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(_dismissResult);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (CloseButton.IsKeyboardFocusWithin) Finish(_dismissResult);
            else if (AcceptButton.IsKeyboardFocusWithin) Finish((MessageBoxResult)AcceptButton.Tag);
            else if (RejectButton.IsKeyboardFocusWithin && RejectButton.IsVisible) Finish((MessageBoxResult)RejectButton.Tag);
            else Finish(_defaultResult);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || e.LeftButton != MouseButtonState.Pressed || CloseButton.IsMouseOver) return;
        try { DragMove(); }
        catch (InvalidOperationException) {   }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public MonitorRect Monitor, Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
