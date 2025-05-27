using System.Windows;
using Microsoft.CodeAnalysis;

namespace CodeInitializer.DialogBoxes.IncludeGenerics
{
    public partial class GenericOptionDialog : Window
    {
        public bool IncludeGenerics => IncludeGenericsCheckBox.IsChecked == true;

        public GenericOptionDialog(INamedTypeSymbol classSymbol)
        {
            InitializeComponent();
            this.Title = $"Generate Interface for {classSymbol.Name}";
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => this.DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => this.DialogResult = false;
    }
}
