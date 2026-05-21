using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml;
namespace GelBox.Controls
{
    public class QueueRowTemplateSelector : DataTemplateSelector
    {
        public DataTemplate QueueRowTemplate { get; set; }
        public DataTemplate ShowMoreRowTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is MusicPlayer.QueueItemRowModel rowModel && rowModel.IsShowMore)
            {
                return ShowMoreRowTemplate;
            }
            return QueueRowTemplate;
        }
    }
}