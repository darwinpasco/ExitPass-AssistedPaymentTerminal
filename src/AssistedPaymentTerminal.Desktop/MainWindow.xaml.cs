using System.Diagnostics;
using System.Windows;

namespace AssistedPaymentTerminal.Desktop;

public partial class MainWindow : Window
{
    private readonly Uri _source;
    private readonly StartupOptions _options;

    public MainWindow(Uri source, StartupOptions options)
    {
        _source = source;
        _options = options;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Trace.TraceInformation(
                "Starting ExitPass Assisted Payment Terminal. profile={0} source={1}",
                _options.Profile,
                _source);

            await TerminalWebView.EnsureCoreWebView2Async();
            TerminalWebView.Source = _source;
        }
        catch (Exception exception)
        {
            Trace.TraceError("WebView startup failed. message={0}", exception.Message);
            MessageBox.Show(
                $"WebView2 failed to load the terminal UI. {exception.Message}",
                "ExitPass Assisted Payment Terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
