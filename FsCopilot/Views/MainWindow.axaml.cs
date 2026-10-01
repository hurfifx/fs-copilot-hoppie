namespace FsCopilot.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using ViewModels;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _hoppieRestartDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AttachViewModel();
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachViewModel();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();

        base.OnClosed(e);
    }

    private void AttachViewModel()
    {
        DetachViewModel();
        if (DataContext is not MainViewModel viewModel)
            return;

        _viewModel = viewModel;
        _viewModel.HoppieRestartPromptRequested += OnHoppieRestartPromptRequested;
    }

    private void DetachViewModel()
    {
        if (_viewModel == null)
            return;

        _viewModel.HoppieRestartPromptRequested -= OnHoppieRestartPromptRequested;
        _viewModel = null;
    }

    private async void OnHoppieRestartPromptRequested(HoppieRestartPrompt prompt)
    {
        if (_hoppieRestartDialogOpen)
            return;

        _hoppieRestartDialogOpen = true;
        try
        {
            await ShowHoppieRestartDialogAsync(prompt);
        }
        finally
        {
            _hoppieRestartDialogOpen = false;
        }
    }

    private async Task ShowHoppieRestartDialogAsync(HoppieRestartPrompt prompt)
    {
        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        var dialog = new Window
        {
            Title = prompt.Title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SystemDecorations = SystemDecorations.Full,
            Background = new SolidColorBrush(Color.Parse("#202026")),
            Content = new Border
            {
                Padding = new Thickness(20),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = prompt.Message,
                            FontSize = 18,
                            FontWeight = FontWeight.SemiBold,
                            Foreground = Brushes.White,
                            TextWrapping = TextWrapping.Wrap
                        },
                        new TextBlock
                        {
                            Text = prompt.Detail,
                            Foreground = new SolidColorBrush(Color.Parse("#d4d4df")),
                            TextWrapping = TextWrapping.Wrap
                        },
                        okButton
                    }
                }
            }
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }
}
