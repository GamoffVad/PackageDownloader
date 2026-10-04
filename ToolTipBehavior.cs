using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PackageDownloader;

/// <summary>
/// Прикреплённое поведение: всплывающая подсказка (поповер) закрывается сразу,
/// как только курсор покидает элемент-владелец, не дожидаясь ShowDuration.
/// Стандартное закрытие WPF по MouseLeave при кастомном шаблоне ToolTip и большой
/// длительности срабатывает ненадёжно, поэтому подсказку закрываем принудительно.
/// </summary>
public static class ToolTipBehavior
{
    public static readonly DependencyProperty CloseOnLeaveProperty =
        DependencyProperty.RegisterAttached(
            "CloseOnLeave", typeof(bool), typeof(ToolTipBehavior),
            new PropertyMetadata(false, OnCloseOnLeaveChanged));

    public static bool GetCloseOnLeave(DependencyObject o) => (bool)o.GetValue(CloseOnLeaveProperty);
    public static void SetCloseOnLeave(DependencyObject o, bool v) => o.SetValue(CloseOnLeaveProperty, v);

    private static void OnCloseOnLeaveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue) fe.MouseLeave += OnMouseLeave;
        else                  fe.MouseLeave -= OnMouseLeave;
    }

    private static void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;

        // ToolTipService.ToolTip может хранить сам ToolTip либо произвольный контент,
        // который WPF при показе оборачивает в ToolTip (тогда контент — логический
        // потомок этого ToolTip).
        switch (fe.ToolTip)
        {
            case ToolTip tt:
                tt.IsOpen = false;
                break;
            case DependencyObject content when LogicalTreeHelper.GetParent(content) is ToolTip wrap:
                wrap.IsOpen = false;
                break;
        }
    }
}
