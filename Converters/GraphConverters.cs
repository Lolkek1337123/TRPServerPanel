using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media;

namespace TRPServerPanel.Converters
{
    public class HistoryToPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ObservableCollection<double> history && history.Any())
            {
                var points = new PointCollection();
                double width = 80; // Fixed width for sidebar mini-charts
                double height = 20; // Fixed height
                double max = history.Max() > 0 ? history.Max() * 1.2 : 100; // Auto-scale with 20% breathing room
                
                if (parameter?.ToString() == "RAM") max = Services.SystemService.TotalPhysicalRamGb; // Dynamic max for RAM
                if (parameter?.ToString() == "CPU") max = 100.0; // Fixed max for CPU (100%)

                double stepX = history.Count > 1 ? width / (history.Count - 1) : 0;

                for (int i = 0; i < history.Count; i++)
                {
                    double x = i * stepX;
                    double y = height - (history[i] / max * height);
                    if (y < 0) y = 0;
                    if (y > height) y = height;
                    points.Add(new System.Windows.Point(x, y));
                }
                return points;
            }
            return new PointCollection();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class TimeToUptimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime startTime)
            {
                var diff = DateTime.Now - startTime;
                if (diff.TotalHours >= 1)
                    return $"{(int)diff.TotalHours}h {diff.Minutes}m {diff.Seconds}s";
                return $"{diff.Minutes}m {diff.Seconds}s";
            }
            return "0m 0s";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class DateToWipeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime wipeDate)
            {
                var diff = wipeDate - DateTime.Now;
                if (diff.TotalDays >= 1) return $"{(int)diff.TotalDays}d";
                if (diff.TotalHours >= 1) return $"{(int)diff.TotalHours}h";
                return $"{diff.Minutes}m";
            }
            return "---";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
