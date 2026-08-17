using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetAudit.Core.Models;

namespace NetAudit.App;

public partial class HardwareWindow : Window
{
    private static readonly SolidColorBrush BrushAccent = new(Color.FromRgb(0x4F, 0xD9, 0x8B));
    private static readonly SolidColorBrush BrushLabel  = new(Color.FromRgb(0x5C, 0x7A, 0x69));
    private static readonly SolidColorBrush BrushValue  = new(Color.FromRgb(0xD8, 0xEA, 0xDF));
    private static readonly SolidColorBrush BrushGreen  = new(Color.FromRgb(0x86, 0xD9, 0x7A));
    private static readonly SolidColorBrush BrushDim    = new(Color.FromRgb(0x2C, 0x50, 0x39));

    public HardwareWindow(HardwareInfo? info)
    {
        InitializeComponent();
        if (info is null)
            ContentPanel.Children.Add(MakeText("Информация о системе ещё загружается...", BrushLabel));
        else
            BuildContent(info);
    }

    private void BuildContent(HardwareInfo h)
    {
        // ── Процессор ─────────────────────────────────────────────────────
        AddSection("Процессор");
        AddRow("Модель",           h.CpuName.Length > 0 ? h.CpuName : "—");
        AddRow("Ядра / потоки",    $"{h.CpuPhysicalCores} физических / {h.CpuLogicalCores} логических");
        if (h.CpuMaxMhz > 0)
            AddRow("Макс. частота", $"{h.CpuMaxMhz / 1000.0:F1} ГГц  ({h.CpuMaxMhz} МГц)");

        // ── Оперативная память ────────────────────────────────────────────
        AddSection("Оперативная память");
        AddRow("Объём",    h.RamTotalGb > 0 ? $"{h.RamTotalGb:F1} ГБ" : "—");
        AddRow("Тип",      h.RamType.Length > 0 ? h.RamType : "—");
        if (h.RamSpeedMhz > 0)
            AddRow("Скорость", $"{h.RamSpeedMhz} МГц");
        if (h.RamModules > 0)
            AddRow("Планок", h.RamModules.ToString());

        // ── Видеокарта ────────────────────────────────────────────────────
        AddSection("Видеокарта");
        AddRow("Модель", h.GpuName.Length > 0 ? h.GpuName : "—");
        if (h.GpuVramGb > 0)
        {
            string vramNote = h.GpuVramGb <= 4.01 ? "  (может быть неточно — ограничение WMI)" : "";
            AddRow("VRAM", $"{h.GpuVramGb:F0} ГБ{vramNote}");
        }

        // ── Материнская плата ─────────────────────────────────────────────
        if (h.BoardVendor.Length > 0 || h.BoardModel.Length > 0)
        {
            AddSection("Материнская плата");
            if (h.BoardVendor.Length > 0) AddRow("Производитель", h.BoardVendor);
            if (h.BoardModel.Length > 0)  AddRow("Модель",        h.BoardModel);
        }

        // ── Операционная система ──────────────────────────────────────────
        AddSection("Операционная система");
        AddRow("Система",  h.OsCaption.Length > 0 ? h.OsCaption : "—");
        AddRow("Версия",   h.OsDisplayVersion.Length > 0 ? h.OsDisplayVersion : "—");
        AddRow("Сборка",   h.OsBuild.Length > 0 ? $"Build {h.OsBuild}" : "—");
        AddRow("Архитектура", Environment.Is64BitOperatingSystem ? "64-бит" : "32-бит");

        // ── Накопители ────────────────────────────────────────────────────
        if (h.Drives.Count > 0)
        {
            AddSection("Накопители");
            foreach (var d in h.Drives)
            {
                double totalGb = d.TotalBytes / 1_073_741_824.0;
                double freeGb  = d.FreeBytes  / 1_073_741_824.0;
                double usedPct = totalGb > 0 ? (totalGb - freeGb) / totalGb * 100 : 0;
                string label   = d.Label.Length > 0 ? $" [{d.Label}]" : "";
                string media   = d.MediaType is "HDD" or "SSD" or "SCM" ? $"  ·  {d.MediaType}" : "";
                AddRow(
                    $"{d.Name}{label}",
                    $"{freeGb:F0} ГБ свободно из {totalGb:F0} ГБ  ·  {usedPct:F0}% занято  ·  {d.FileSystem}{media}",
                    usedPct > 90 ? new SolidColorBrush(Color.FromRgb(0xDF, 0xC4, 0x6A)) : BrushValue);
            }
        }

        // ── Сетевые адаптеры ─────────────────────────────────────────────
        if (h.Adapters.Count > 0)
        {
            AddSection("Сетевые адаптеры");
            foreach (var a in h.Adapters)
            {
                string ips   = a.IpAddresses.Count > 0 ? string.Join(", ", a.IpAddresses) : "нет IP";
                string speed = a.SpeedMbps > 0 ? $"  ·  {FormatSpeed(a.SpeedMbps)}" : "";
                string mac   = a.MacAddress.Length > 0 ? $"  ·  MAC: {a.MacAddress}" : "";
                string status = a.IsConnected ? "✓" : "✗";
                SolidColorBrush valColor = a.IsConnected ? BrushGreen : BrushDim;

                AddRow(
                    $"{status} {a.Name}",
                    $"{ips}{speed}{mac}",
                    valColor);
                AddSubRow(a.Description);
            }
        }
    }

    // ── UI-хелперы ────────────────────────────────────────────────────────

    private void AddSection(string title)
    {
        if (ContentPanel.Children.Count > 0)
            ContentPanel.Children.Add(new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(0x23, 0x40, 0x2F)),
                Margin = new Thickness(0, 8, 0, 8)
            });

        ContentPanel.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = BrushAccent,
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Margin     = new Thickness(0, 0, 0, 6)
        });
    }

    private void AddRow(string label, string value, SolidColorBrush? valueBrush = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = MakeText(label, BrushLabel, 11);
        Grid.SetColumn(lbl, 0);

        var val = MakeText(value, valueBrush ?? BrushValue, 11);
        val.TextWrapping = TextWrapping.Wrap;
        Grid.SetColumn(val, 1);

        grid.Children.Add(lbl);
        grid.Children.Add(val);
        ContentPanel.Children.Add(grid);
    }

    private void AddSubRow(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ContentPanel.Children.Add(new TextBlock
        {
            Text       = $"     {text}",
            Foreground = BrushDim,
            FontSize   = 10,
            Margin     = new Thickness(180, 0, 0, 2),
            TextWrapping = TextWrapping.Wrap
        });
    }

    private static TextBlock MakeText(string text, SolidColorBrush color, double size = 12) =>
        new() { Text = text, Foreground = color, FontSize = size, VerticalAlignment = VerticalAlignment.Top };

    private static string FormatSpeed(long mbps) =>
        mbps >= 1000 ? $"{mbps / 1000.0:F0} Гбит/с" : $"{mbps} Мбит/с";

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
