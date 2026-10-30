using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CanSatGSUI.ViewModels;
using ScottPlot.Avalonia;
using SpColor = ScottPlot.Color;

namespace CanSatGSUI.Views;

/// <summary>
/// Graphs tab: one live ScottPlot chart per measurement, fed from <see cref="MainViewModel.History"/>.
/// </summary>
/// <remarks>
/// ScottPlot draws on Avalonia's render thread, so each chart keeps its own copy of the data and
/// only changes it while holding <c>Plot.Sync</c>. New points are copied in batches by a timer
/// (4x per second) so a fast packet rate doesn't redraw 10 charts for every single packet.
/// </remarks>
public partial class TelemetryChartsView : UserControl
{
    // Colours match the dark palette in App.axaml.
    private static readonly SpColor FigureBackground = SpColor.FromHex("#241A2C"); // App.CardBackground
    private static readonly SpColor DataBackground = SpColor.FromHex("#1A1320");
    private static readonly SpColor Foreground = SpColor.FromHex("#FFF0B3"); // App.ForegroundBrush
    private static readonly SpColor GridLines = SpColor.FromHex("#3D2E4A");

    private static readonly SpColor Red = SpColor.FromHex("#FF6B6B");
    private static readonly SpColor Green = SpColor.FromHex("#6BCB77");
    private static readonly SpColor Blue = SpColor.FromHex("#4D96FF");
    private static readonly SpColor Gold = SpColor.FromHex("#FFC857");

    private readonly List<Chart> _charts = [];
    private readonly DispatcherTimer _refreshTimer;
    private TelemetryHistory? _history;
    private bool _dirty;

    public TelemetryChartsView()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => RefreshCharts();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Attach((DataContext as MainViewModel)?.History);
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

    private void Attach(TelemetryHistory? history)
    {
        if (ReferenceEquals(history, _history)) return;

        if (_history is not null) _history.Changed -= OnHistoryChanged;
        _history = history;
        _charts.Clear();
        if (history is null) return;

        history.Changed += OnHistoryChanged;

        _charts.Add(new Chart(AltitudePlot, "m",
            (history.GpsAltitude, "GPS", Blue),
            (history.BaroAltitude, "Barometer", Gold)));
        _charts.Add(new Chart(TemperaturePlot, "°C", (history.Temperature, null, Gold)));
        _charts.Add(new Chart(PressurePlot, "hPa", (history.Pressure, null, Gold)));
        _charts.Add(new Chart(HumidityPlot, "%", (history.Humidity, null, Gold)));
        _charts.Add(new Chart(AccelerationPlot, "m/s²",
            (history.AccelX, "X", Red),
            (history.AccelY, "Y", Green),
            (history.AccelZ, "Z", Blue),
            (history.AccelMagnitude, "|a|", Gold)));
        _charts.Add(new Chart(AngularVelocityPlot, "°/s",
            (history.OmegaX, "X", Red),
            (history.OmegaY, "Y", Green),
            (history.OmegaZ, "Z", Blue)));
        _charts.Add(new Chart(MagneticFieldPlot, "µT",
            (history.MagX, "X", Red),
            (history.MagY, "Y", Green),
            (history.MagZ, "Z", Blue)));
        _charts.Add(new Chart(AzimuthPlot, "°", (history.Theta, null, Gold)));
        _charts.Add(new Chart(RssiPlot, "dBm", (history.Rssi, null, Gold)));
        _charts.Add(new Chart(SnrPlot, "dB", (history.Snr, null, Gold)));

        _dirty = true;
        RefreshCharts();
    }

    private void OnHistoryChanged() => _dirty = true;

    private void RefreshCharts()
    {
        if (!_dirty || _history is null) return;
        _dirty = false;

        var follow = FollowLiveCheckBox.IsChecked == true;
        foreach (var chart in _charts) chart.Update(_history, autoScale: follow);
    }

    private void OnFitAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var chart in _charts) chart.FitAll();
    }

    /// <summary>One AvaPlot plus its private, render-thread-safe copy of the data.</summary>
    private sealed class Chart
    {
        private readonly AvaPlot _view;
        private readonly List<double> _xs = [];
        private readonly (List<double> Source, List<double> Target)[] _series;
        private int _generation = -1;

        public Chart(AvaPlot view, string yLabel, params (List<double> Source, string? Name, SpColor Color)[] series)
        {
            _view = view;
            _series = new (List<double>, List<double>)[series.Length];

            var plot = view.Plot;
            lock (plot.Sync)
            {
                plot.Clear();

                plot.FigureBackground.Color = FigureBackground;
                plot.DataBackground.Color = DataBackground;
                plot.Axes.Color(Foreground);
                plot.Grid.MajorLineColor = GridLines;
                plot.XLabel("Mission time (s)");
                plot.YLabel(yLabel);

                for (var i = 0; i < series.Length; i++)
                {
                    var target = new List<double>();
                    _series[i] = (series[i].Source, target);

                    var scatter = plot.Add.Scatter(_xs, target);
                    scatter.Color = series[i].Color;
                    scatter.LineWidth = 2;
                    scatter.MarkerSize = 0;
                    if (series[i].Name is { } name) scatter.LegendText = name;
                }

                if (series.Length > 1)
                {
                    plot.ShowLegend();
                    plot.Legend.BackgroundColor = FigureBackground;
                    plot.Legend.FontColor = Foreground;
                    plot.Legend.OutlineColor = GridLines;
                }
            }

            view.Refresh();
        }

        /// <summary>Copies points that are new since the last update into the chart and redraws it.</summary>
        public void Update(TelemetryHistory history, bool autoScale)
        {
            lock (_view.Plot.Sync)
            {
                // Points were removed (Clear / trimming): start over.
                if (_generation != history.Generation)
                {
                    _xs.Clear();
                    foreach (var s in _series) s.Target.Clear();
                    _generation = history.Generation;
                }

                var start = _xs.Count;
                var count = history.Count - start;
                if (count > 0)
                {
                    _xs.AddRange(history.Time.GetRange(start, count));
                    foreach (var s in _series) s.Target.AddRange(s.Source.GetRange(start, count));
                }

                if (autoScale) _view.Plot.Axes.AutoScale();
            }

            _view.Refresh();
        }

        public void FitAll()
        {
            lock (_view.Plot.Sync)
            {
                _view.Plot.Axes.AutoScale();
            }

            _view.Refresh();
        }
    }
}
