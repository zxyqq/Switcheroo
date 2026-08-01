using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;

namespace Switcheroo
{
    public class BoolConverter<T> : IValueConverter
    {
        public T IfTrue { get; set; }
        public T IfFalse { get; set; }

        #region IValueConverter Members

        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            return ((bool) value) ? IfTrue : IfFalse;
        }

        public object ConvertBack(object value, Type targetType, object parameter,
            System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        #endregion
    }


    public class BoolToDoubleConverter : BoolConverter<double>
    {
    }

    public class BoolToColorConverter : BoolConverter<Color>
    {
    }

    public class SelectionAwareColorConverter : IMultiValueConverter
    {
        public Color SelectedColor { get; set; }
        public Color NormalColor { get; set; }
        public Color ClosingColor { get; set; }

        #region IMultiValueConverter Members

        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var isBeingClosed = values[0] is bool && (bool) values[0];
            var isSelected = values[1] is bool && (bool) values[1];

            if (isSelected)
            {
                return SelectedColor;
            }
            return isBeingClosed ? ClosingColor : NormalColor;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}