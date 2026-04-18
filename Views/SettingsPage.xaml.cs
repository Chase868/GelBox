using System;
using System.Threading.Tasks;
using GelBox.ViewModels;
using Windows.UI.Xaml;

namespace GelBox.Views
{
    public sealed partial class SettingsPage : BasePage
    {
        static SettingsPage()
        {
        }

        public SettingsPage() : base(typeof(SettingsPage))
        {
            InitializeComponent();
        }

        // Specify the ViewModel type for automatic initialization
        protected override Type ViewModelType => typeof(SettingsViewModel);

        // Typed property for easy access
        public SettingsViewModel TypedViewModel => (SettingsViewModel)ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            await TypedViewModel.InitializeAsync();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigationService?.GoBack();
        }
    }
}
