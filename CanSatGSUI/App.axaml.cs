using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CanSatGSUI.ViewModels;
using CanSatGSUI.Views;
using CanSatGroundStation;

namespace CanSatGSUI;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = AppSettings.Load();
            var engine = new TelemetryEngine(settings.LocalEthIp, settings.MulticastGroup, settings.Port, settings.LogFile);
            var groundStation = new CanSatGroundStation.CanSatGroundStation(engine, deltaT: settings.DeltaT, measStd: settings.MeasStd, accelStd: settings.AccelStd, stepsToPredict: settings.StepsToPredict);

            var vm = new MainViewModel(groundStation, settings);

            desktop.MainWindow = new MainWindow
            {
                DataContext = vm,
            };
            desktop.Exit += (_, _) => vm.Dispose();
            vm.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
