using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DotaUtility.App.Services;
using DotaUtility.App.ViewModels;

namespace DotaUtility.App;

public partial class MainWindow : Window
{
    private SystemTrayService? _systemTrayService;
    private bool _isExplicitExit;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void AttachSystemTray(SystemTrayService systemTrayService)
    {
        _systemTrayService = systemTrayService ?? throw new ArgumentNullException(nameof(systemTrayService));
        _systemTrayService.Initialize(this);
    }

    public void RestoreFromTray(bool selectSettingsTab = false)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SelectedTabIndex = selectSettingsTab ? 1 : 0;
        }

        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ExitApplication()
    {
        _isExplicitExit = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isExplicitExit || DataContext is not MainViewModel viewModel)
        {
            base.OnClosing(e);
            return;
        }

        CloseBehavior chosenBehavior;
        if (!viewModel.RememberCloseChoice)
        {
            if (!IsVisible)
            {
                Show();
            }

            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }

            Activate();

            var dialog = new CloseConfirmationDialog(viewModel.CloseBehavior, viewModel.RememberCloseChoice)
            {
                Owner = this,
            };

            if (dialog.ShowDialog() != true)
            {
                e.Cancel = true;
                return;
            }

            chosenBehavior = dialog.SelectedBehavior;
            viewModel.ApplyCloseDecision(chosenBehavior, dialog.RememberChoice);
        }
        else
        {
            chosenBehavior = viewModel.CloseBehavior;
        }

        if (chosenBehavior == CloseBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _systemTrayService?.ShowMinimizedNotification();
            return;
        }

        _isExplicitExit = true;
        base.OnClosing(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ResetCloseConfirmation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RememberCloseChoice = false;
        }
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
