using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CanSatGSUI.ViewModels;
using P = CanSatGSUI.Views.ChartPalette;

namespace CanSatGSUI.Views;

/// <summary>
/// Graphs tab: one large live ScottPlot chart per measurement, fed from <see cref="MainViewModel.History"/>.
/// Shows either all graphs or only the pinned ones (the cards' visibility is bound in XAML).
/// </summary>
/// <remarks>
/// New points are copied in batches by a timer (4x per second), so a fast packet rate doesn't
/// redraw 10 charts for every single packet. Hidden charts aren't redrawn.
/// </remarks>
public partial class TelemetryChartsView : UserControl
{
    private readonly List<(LiveChart Chart, Control Card, GraphCardViewModel? Graph)> _charts = [];
    private readonly DispatcherTimer _refreshTimer;
    private MainViewModel? _vm;
    private LiveChart? _altitude;
    private bool _dirty;
    private bool _forecastDirty;

    public TelemetryChartsView()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshCharts();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach(DataContext as MainViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _refreshTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _refreshTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Attach(MainViewModel? vm)
    {
        if (ReferenceEquals(vm, _vm)) return;

        if (_vm is not null)
        {
            _vm.History.Changed -= OnHistoryChanged;
            _vm.PredictedTrajectory.CollectionChanged -= OnForecastChanged;
            foreach (var g in _vm.Graphs) g.PropertyChanged -= OnGraphPropertyChanged;
        }

        _vm = vm;
        _charts.Clear();
        _altitude = null;
        if (vm is null) return;

        vm.History.Changed += OnHistoryChanged;
        vm.PredictedTrajectory.CollectionChanged += OnForecastChanged;
        foreach (var g in vm.Graphs) g.PropertyChanged += OnGraphPropertyChanged;

        // Altitude is the one "paper" chart: ink lines, dashed Kalman forecast.
        _altitude = new LiveChart(AltitudePlot, "m", mini: false, paper: true, withForecast: true,
            new SeriesDef(h => h.BaroAltitude, "Barometer", P.Ink),
            new SeriesDef(h => h.GpsAltitude, "GPS", P.InkSecondary, 1.4f));
        Add(_altitude, AltitudePlot, vm.AltitudeGraph);

        Add(Single(TemperaturePlot, "°C", h => h.Temperature), TemperaturePlot, vm.TemperatureGraph);
        Add(Single(PressurePlot, "hPa", h => h.Pressure), PressurePlot, vm.PressureGraph);
        Add(Single(HumidityPlot, "%", h => h.Humidity), HumidityPlot, vm.HumidityGraph);
        Add(new LiveChart(AccelerationPlot, "m/s²", false, false, false,
            new SeriesDef(h => h.AccelMagnitude, "|a|", P.Data),
            new SeriesDef(h => h.AccelX, "X", P.Text, 1.2f),
            new SeriesDef(h => h.AccelY, "Y", P.Muted, 1.2f),
            new SeriesDef(h => h.AccelZ, "Z", P.Dim, 1.2f)), AccelerationPlot, vm.AccelerationGraph);
        Add(new LiveChart(AngularVelocityPlot, "°/s", false, false, false,
            new SeriesDef(h => h.OmegaZ, "Z", P.Data),
            new SeriesDef(h => h.OmegaX, "X", P.Text, 1.2f),
            new SeriesDef(h => h.OmegaY, "Y", P.Muted, 1.2f)), AngularVelocityPlot, vm.AngularVelocityGraph);
        Add(new LiveChart(MagneticFieldPlot, "µT", false, false, false,
            new SeriesDef(h => h.MagZ, "Z", P.Data),
            new SeriesDef(h => h.MagX, "X", P.Text, 1.2f),
            new SeriesDef(h => h.MagY, "Y", P.Muted, 1.2f)), MagneticFieldPlot, vm.MagneticFieldGraph);
        Add(Single(AzimuthPlot, "°", h => h.Theta), AzimuthPlot, vm.AzimuthGraph);
        Add(Single(RssiPlot, "dBm", h => h.Rssi), RssiPlot, vm.RssiGraph);
        Add(Single(SnrPlot, "dB", h => h.Snr), SnrPlot, vm.SnrGraph);

        _dirty = true;
        _forecastDirty = true;
        RefreshCharts();
    }

    private static LiveChart Single(ScottPlot.Avalonia.AvaPlot view, string unit, Func<TelemetryHistory, List<double>> source)
        => new(view, unit, false, false, false, new SeriesDef(source, null, P.Data));

    private void Add(LiveChart chart, Control plot, GraphCardViewModel graph)
    {
        // The card is the Border around the chart (Border > StackPanel > AvaPlot);
        // its IsVisible is bound to the Pinned / All view.
        var card = (plot.Parent as Control)?.Parent as Control ?? plot;
        _charts.Add((chart, card, graph));
        chart.SetAlert(graph.IsAlert);
    }

    private void OnHistoryChanged() => _dirty = true;

    private void OnForecastChanged(object? sender, NotifyCollectionChangedEventArgs e) => _forecastDirty = true;

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GraphCardViewModel graph) return;

        if (e.PropertyName == nameof(GraphCardViewModel.IsAlert))
        {
            foreach (var c in _charts)
            {
                if (ReferenceEquals(c.Graph, graph)) c.Chart.SetAlert(graph.IsAlert);
            }
        }
        else if (e.PropertyName == nameof(GraphCardViewModel.IsShownInGraphsTab))
        {
            // A chart that was hidden missed updates: redraw on the next tick.
            _dirty = true;
        }
    }

    private void RefreshCharts()
    {
        if (_vm is null || !IsEffectivelyVisible) return;

        if (_forecastDirty && _altitude is not null)
        {
            _forecastDirty = false;
            var xs = new List<double>();
            var ys = new List<double>();
            if (_vm.HasTrajectoryForecast && _vm.History.Count > 0)
            {
                // The forecast starts at the latest GPS point.
                var now = _vm.History.Time[^1];
                xs.Add(now);
                ys.Add(_vm.History.GpsAltitude[^1]);
                foreach (var p in _vm.PredictedTrajectory)
                {
                    xs.Add(now + p.DeltaSeconds);
                    ys.Add(p.Altitude);
                }
            }

            _altitude.SetForecast(xs, ys);
        }

        if (!_dirty) return;
        _dirty = false;

        var follow = FollowLiveCheckBox.IsChecked == true;
        foreach (var c in _charts)
        {
            if (c.Card.IsVisible) c.Chart.Update(_vm.History, autoScale: follow);
            else _dirty = true; // catch up when it becomes visible
        }

        // Only hidden charts left behind: no need to keep re-running every tick.
        if (_dirty && _charts.TrueForAll(c => !c.Card.IsVisible)) _dirty = false;
    }

    private void OnFitAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var c in _charts) c.Chart.FitAll();
    }
}
