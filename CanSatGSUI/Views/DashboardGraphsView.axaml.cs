using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CanSatGSUI.ViewModels;
using P = CanSatGSUI.Views.ChartPalette;

namespace CanSatGSUI.Views;

/// <summary>
/// Dashboard, Graphs view: a small live chart (last 60 s, no axes) for every measurement.
/// Same data as the Graphs tab; redrawn 4x per second and only while visible.
/// </summary>
public partial class DashboardGraphsView : UserControl
{
    private readonly List<(LiveChart Chart, GraphCardViewModel Graph)> _charts = [];
    private readonly DispatcherTimer _refreshTimer;
    private MainViewModel? _vm;
    private bool _dirty;

    public DashboardGraphsView()
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
            foreach (var g in _vm.Graphs) g.PropertyChanged -= OnGraphPropertyChanged;
        }

        _vm = vm;
        _charts.Clear();
        if (vm is null) return;

        vm.History.Changed += OnHistoryChanged;
        foreach (var g in vm.Graphs) g.PropertyChanged += OnGraphPropertyChanged;

        Add(Mini(MiniAltitudePlot,
            new SeriesDef(h => h.BaroAltitude, "Baro", P.Data),
            new SeriesDef(h => h.GpsAltitude, "GPS", P.Text, 1.2f)), vm.AltitudeGraph);
        Add(Mini(MiniTemperaturePlot, new SeriesDef(h => h.Temperature, null, P.Data)), vm.TemperatureGraph);
        Add(Mini(MiniPressurePlot, new SeriesDef(h => h.Pressure, null, P.Data)), vm.PressureGraph);
        Add(Mini(MiniHumidityPlot, new SeriesDef(h => h.Humidity, null, P.Data)), vm.HumidityGraph);
        Add(Mini(MiniAccelerationPlot,
            new SeriesDef(h => h.AccelMagnitude, "|a|", P.Data),
            new SeriesDef(h => h.AccelX, "X", P.Text, 1.1f),
            new SeriesDef(h => h.AccelY, "Y", P.Muted, 1.1f),
            new SeriesDef(h => h.AccelZ, "Z", P.Dim, 1.1f)), vm.AccelerationGraph);
        Add(Mini(MiniAngularVelocityPlot,
            new SeriesDef(h => h.OmegaZ, "Z", P.Data),
            new SeriesDef(h => h.OmegaX, "X", P.Text, 1.1f),
            new SeriesDef(h => h.OmegaY, "Y", P.Muted, 1.1f)), vm.AngularVelocityGraph);
        Add(Mini(MiniMagneticFieldPlot,
            new SeriesDef(h => h.MagZ, "Z", P.Data),
            new SeriesDef(h => h.MagX, "X", P.Text, 1.1f),
            new SeriesDef(h => h.MagY, "Y", P.Muted, 1.1f)), vm.MagneticFieldGraph);
        Add(Mini(MiniAzimuthPlot, new SeriesDef(h => h.Theta, null, P.Data)), vm.AzimuthGraph);
        Add(Mini(MiniRssiPlot, new SeriesDef(h => h.Rssi, null, P.Data)), vm.RssiGraph);
        Add(Mini(MiniSnrPlot, new SeriesDef(h => h.Snr, null, P.Data)), vm.SnrGraph);

        _dirty = true;
        RefreshCharts();
    }

    private static LiveChart Mini(ScottPlot.Avalonia.AvaPlot view, params SeriesDef[] series)
        => new(view, string.Empty, mini: true, paper: false, withForecast: false, series);

    private void Add(LiveChart chart, GraphCardViewModel graph)
    {
        _charts.Add((chart, graph));
        chart.SetAlert(graph.IsAlert);
    }

    private void OnHistoryChanged() => _dirty = true;

    private void OnGraphPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not GraphCardViewModel graph || e.PropertyName != nameof(GraphCardViewModel.IsAlert)) return;
        foreach (var c in _charts)
        {
            if (ReferenceEquals(c.Graph, graph)) c.Chart.SetAlert(graph.IsAlert);
        }
    }

    private void RefreshCharts()
    {
        // Keep _dirty set while hidden, so the charts catch up as soon as the view is shown.
        if (!_dirty || _vm is null || !IsEffectivelyVisible) return;
        _dirty = false;
        foreach (var c in _charts) c.Chart.Update(_vm.History, autoScale: true);
    }
}
