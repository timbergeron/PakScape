using System.Windows;

namespace PakStudio.App.Helpers;

/// <summary>Marks the folder row under an active drag so item styles can highlight it.</summary>
public static class DropTarget
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(DropTarget),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) =>
        element.SetValue(IsActiveProperty, value);
}
