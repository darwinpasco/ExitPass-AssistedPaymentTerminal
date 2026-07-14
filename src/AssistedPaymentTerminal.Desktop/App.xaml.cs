using System.Windows;

namespace AssistedPaymentTerminal.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var options = StartupOptions.FromEnvironmentAndArgs(e.Args);
        var validation = ProfileValidator.Validate(options.Profile);

        if (!validation.IsValid)
        {
            if (options.SmokeCheckOnly)
            {
                Shutdown(2);
                return;
            }

            MessageBox.Show(
                validation.Message,
                "ExitPass Assisted Payment Terminal startup refused",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        try
        {
            var source = WebViewSourceResolver.Resolve(options);
            if (options.SmokeCheckOnly)
            {
                Shutdown(0);
                return;
            }

            MainWindow = new MainWindow(source, options);
            MainWindow.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Unable to load the terminal shell. {exception.Message}",
                "ExitPass Assisted Payment Terminal startup failure",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
        }
    }
}
