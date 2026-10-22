using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DotaUtility.App.Services;

namespace DotaUtility.App;

public partial class CloseConfirmationDialog : Window
{
    private static readonly Brush SelectedCardBackground = new SolidColorBrush(Color.FromArgb(40, 124, 92, 255));
    private static readonly Brush SelectedCardBorder = new SolidColorBrush(Color.FromArgb(170, 124, 92, 255));
    private static readonly Brush UnselectedCardBackground = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
    private static readonly Brush UnselectedCardBorder = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));

    public CloseConfirmationDialog(CloseBehavior initialBehavior, bool initialRememberChoice = false)
    {
        InitializeComponent();

        MinimizeRadio.IsChecked = initialBehavior == CloseBehavior.MinimizeToTray;
        ExitRadio.IsChecked = initialBehavior == CloseBehavior.ExitApplication;
        RememberCheckBox.IsChecked = initialRememberChoice;
        UpdateCardVisuals();
    }

    public CloseBehavior SelectedBehavior => ExitRadio.IsChecked == true
        ? CloseBehavior.ExitApplication
        : CloseBehavior.MinimizeToTray;

    public bool RememberChoice => RememberCheckBox.IsChecked == true;

    private void MinimizeCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        MinimizeRadio.IsChecked = true;
        UpdateCardVisuals();
    }

    private void ExitCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ExitRadio.IsChecked = true;
        UpdateCardVisuals();
    }

    private void OptionRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
        {
            UpdateCardVisuals();
        }
    }

    private void UpdateCardVisuals()
    {
        var minimizeSelected = MinimizeRadio.IsChecked == true;
        MinimizeCard.Background = minimizeSelected ? SelectedCardBackground : UnselectedCardBackground;
        MinimizeCard.BorderBrush = minimizeSelected ? SelectedCardBorder : UnselectedCardBorder;

        ExitCard.Background = !minimizeSelected ? SelectedCardBackground : UnselectedCardBackground;
        ExitCard.BorderBrush = !minimizeSelected ? SelectedCardBorder : UnselectedCardBorder;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
