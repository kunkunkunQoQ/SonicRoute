using System.Windows;
using System.Windows.Controls;
using Size = System.Windows.Size;

namespace SonicRoute
{
    internal interface IDeferredPanelRow
    {
        FrameworkElement Realize();
        void ReleaseVisual();
    }

    // ListBox recycles these small presenters. Business state lives in the item,
    // while sliders, bindings and device controls only exist around the viewport.
    public sealed class DeferredPanelRowPresenter : Decorator
    {
        private IDeferredPanelRow? _row;

        public DeferredPanelRowPresenter()
        {
            DataContextChanged += (_, _) => { Release(); InvalidateMeasure(); };
            Unloaded += (_, _) => Release();
            Loaded += (_, _) => InvalidateMeasure();
        }

        protected override Size MeasureOverride(Size constraint)
        {
            if (_row == null && DataContext is IDeferredPanelRow row)
            {
                _row = row;
                Child = row.Realize();
            }
            return base.MeasureOverride(constraint);
        }

        private void Release()
        {
            var row = _row;
            _row = null;
            Child = null;
            row?.ReleaseVisual();
        }
    }

    internal sealed class DeferredPanelRowTemplateSelector(DataTemplate template) : DataTemplateSelector
    {
        public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
            item is IDeferredPanelRow ? template : null;
    }
}
