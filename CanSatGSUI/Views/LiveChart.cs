using System;
using System.Collections.Generic;
using CanSatGSUI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Plottables;
using SpColor = ScottPlot.Color;

namespace CanSatGSUI.Views;

/// <summary>CanTauri console palette for ScottPlot (same values as the Ct.* brushes in App.axaml).</summary>
internal static class ChartPalette
{
    public static readonly SpColor Panel = SpColor.FromHex("#181614");
    public static readonly SpColor Grid = SpColor.FromHex("#2A2622");
    public static readonly SpColor Text = SpColor.FromHex("#F2EDE3");
    public static readonly SpColor Muted = SpColor.FromHex("#A9A299");
    public static readonly SpColor Dim = SpColor.FromHex("#6F6960");
    public static readonly SpColor Data = SpColor.FromHex("#E3B04B");
    public static readonly SpColor Alert = SpColor.FromHex("#E05A60");

    // The one "paper" chart (Altitude on the Graphs tab).
    public static readonly SpColor Paper = SpColor.FromHex("#EFE9DC");
    public static readonly SpColor Ink = SpColor.FromHex("#171513");
    public static readonly SpColor InkMuted = SpColor.FromHex("#5A544C");
    public static readonly SpColor InkGrid = SpColor.FromHex("#D6D0C4");
    public static readonly SpColor InkSecondary = SpColor.FromHex("#8A8172");
    public static readonly SpColor MissionRed = SpColor.FromHex("#C1272D");
}

/// <summary>One series of a chart: where its values come from in <see cref="TelemetryHistory"/>.</summary>
internal sealed record SeriesDef(Func<TelemetryHistory, List<double>> Source, string? Name, SpColor Color, float Width = 2);

/// <summary>
/// One AvaPlot plus its private, render-thread-safe copy of the data.
/// </summary>
/// <remarks>
/// ScottPlot draws on Avalonia's render thread, so each chart keeps its own copy of the data and
/// only changes it while holding <c>Plot.Sync</c>.
/// </remarks>
internal sealed class LiveChart
{
    private readonly AvaPlot _view;
    private readonly bool _mini;
    private readonly List<double> _xs = [];
    private readonly (Func<TelemetryHistory, List<double>> Source, List<double> Target, Scatter Line, SpColor Color)[] _series;
    private readonly List<double> _forecastXs = [];
    private readonly List<double> _forecastYs = [];
    private int _generation = -1;
    private bool _alert;

    /// <param name="view">The AvaPlot control.</param>
    /// <param name="yLabel">Y axis label (unit). Ignored for mini charts.</param>
    /// <param name="mini">Small dashboard chart: no axes or legend, shows only the last <see cref="MiniWindowSeconds"/>.</param>
    /// <param name="paper">Light "paper" styling (only the Altitude chart on the Graphs tab).</param>
    /// <param name="withForecast">Adds a dashed line for the Kalman forecast (see <see cref="SetForecast"/>).</param>
    /// <param name="series">The data series; the first one is the main one.</param>
    public LiveChart(AvaPlot view, string yLabel, bool mini, bool paper, bool withForecast, params SeriesDef[] series)
    {
        _view = view;
        _mini = mini;
        _series = new (Func<TelemetryHistory, List<double>>, List<double>, Scatter, SpColor)[series.Length];

        var background = paper ? ChartPalette.Paper : ChartPalette.Panel;
        var axisColor = paper ? ChartPalette.InkMuted : ChartPalette.Muted;
        var gridColor = paper ? ChartPalette.InkGrid : ChartPalette.Grid;

        var plot = view.Plot;
        lock (plot.Sync)
        {
            plot.Clear();

            plot.FigureBackground.Color = background;
            plot.DataBackground.Color = background;
            plot.Axes.Color(axisColor);
            plot.Grid.MajorLineColor = gridColor;

            if (mini)
            {
                plot.Layout.Frameless();
            }
            else
            {
                plot.XLabel("Mission time (s)");
                plot.YLabel(yLabel);
            }

            for (var i = 0; i < series.Length; i++)
            {
                var target = new List<double>();
                var scatter = plot.Add.Scatter(_xs, target);
                scatter.Color = series[i].Color;
                scatter.LineWidth = series[i].Width;
                scatter.MarkerSize = 0;
                if (!mini && series[i].Name is { } name) scatter.LegendText = name;
                _series[i] = (series[i].Source, target, scatter, series[i].Color);
            }

            if (withForecast)
            {
                var forecast = plot.Add.Scatter(_forecastXs, _forecastYs);
                forecast.Color = paper ? ChartPalette.Ink : ChartPalette.Text;
                forecast.LineWidth = 1.6f;
                forecast.MarkerSize = 0;
                forecast.LinePattern = LinePattern.Dashed;
                if (!mini) forecast.LegendText = "Kalman forecast (GPS)";
            }

            if (!mini && (series.Length > 1 || withForecast))
            {
                plot.ShowLegend();
                plot.Legend.BackgroundColor = background;
                plot.Legend.FontColor = axisColor;
                plot.Legend.OutlineColor = gridColor;
            }
        }

        view.Refresh();
    }

    /// <summary>How much history the small dashboard charts show.</summary>
    public const double MiniWindowSeconds = 60;

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
                foreach (var s in _series) s.Target.AddRange(s.Source(history).GetRange(start, count));
            }

            if (_mini) ScaleToWindow();
            else if (autoScale) _view.Plot.Axes.AutoScale();
        }

        _view.Refresh();
    }

    /// <summary>Replaces the dashed forecast line (x = mission time, y = predicted value).</summary>
    public void SetForecast(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        lock (_view.Plot.Sync)
        {
            _forecastXs.Clear();
            _forecastYs.Clear();
            _forecastXs.AddRange(xs);
            _forecastYs.AddRange(ys);
        }

        _view.Refresh();
    }

    /// <summary>Draws the main series in the alert colour while a limit is tripped.</summary>
    public void SetAlert(bool alert)
    {
        if (alert == _alert) return;
        _alert = alert;
        lock (_view.Plot.Sync)
        {
            if (_series.Length > 0)
                _series[0].Line.Color = alert ? ChartPalette.Alert : _series[0].Color;
        }

        _view.Refresh();
    }

    public void FitAll()
    {
        lock (_view.Plot.Sync)
        {
            if (_mini) ScaleToWindow();
            else _view.Plot.Axes.AutoScale();
        }

        _view.Refresh();
    }

    /// <summary>Mini charts: last <see cref="MiniWindowSeconds"/> on X, Y fitted to what's visible.</summary>
    private void ScaleToWindow()
    {
        if (_xs.Count == 0) return;

        var xMax = _xs[^1];
        var xMin = xMax - MiniWindowSeconds;
        double yMin = double.PositiveInfinity, yMax = double.NegativeInfinity;

        // Walk backwards: the visible window is at the end of the lists.
        for (var i = _xs.Count - 1; i >= 0 && _xs[i] >= xMin; i--)
        {
            foreach (var s in _series)
            {
                var y = s.Target[i];
                if (double.IsNaN(y) || double.IsInfinity(y)) continue;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }
        }

        if (double.IsInfinity(yMin)) return;

        var span = yMax - yMin;
        var pad = span > 0 ? span * 0.12 : Math.Max(1, Math.Abs(yMax) * 0.05);
        _view.Plot.Axes.SetLimits(Math.Min(xMin, xMax - 1), xMax + 0.5, yMin - pad, yMax + pad);
    }
}
