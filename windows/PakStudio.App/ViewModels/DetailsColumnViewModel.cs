using System.Windows;

namespace PakStudio.App.ViewModels;

/// <summary>
/// One resizable, reorderable column of the details view. Widths are star weights so the
/// columns keep filling the list no matter how the window is sized.
/// </summary>
public sealed class DetailsColumnViewModel : ViewModelBase
{
    private const double MinimumWeight = 0.15;

    private GridLength _width;
    private int _displayIndex;
    private string _headerText;

    public DetailsColumnViewModel(
        string key,
        string title,
        double weight,
        double minWidth,
        HorizontalAlignment contentAlignment)
    {
        Key = key;
        Title = title;
        MinWidth = minWidth;
        ContentAlignment = contentAlignment;
        _headerText = title;
        _width = new GridLength(NormalizeWeight(weight), GridUnitType.Star);
    }

    public string Key { get; }

    public string Title { get; }

    public double MinWidth { get; }

    public HorizontalAlignment ContentAlignment { get; }

    /// <summary>
    /// Two-way bound to the header grid so the splitter drag writes the new weight back here,
    /// which the row template picks up through the same source.
    /// </summary>
    public GridLength Width
    {
        get => _width;
        set
        {
            var weight = NormalizeWeight(value.IsStar ? value.Value : MinimumWeight);
            SetProperty(ref _width, new GridLength(weight, GridUnitType.Star));
        }
    }

    public double Weight => _width.Value;

    public int DisplayIndex
    {
        get => _displayIndex;
        internal set => SetProperty(ref _displayIndex, value);
    }

    public string HeaderText
    {
        get => _headerText;
        internal set => SetProperty(ref _headerText, value);
    }

    private static double NormalizeWeight(double weight) =>
        double.IsFinite(weight) && weight > MinimumWeight ? weight : MinimumWeight;
}
