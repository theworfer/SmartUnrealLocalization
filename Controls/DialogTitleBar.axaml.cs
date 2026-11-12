using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace SmartUnrealLocalization.Controls
{
    public partial class DialogTitleBar : UserControl
    {
        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<DialogTitleBar, string>(nameof(Title), "");

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public DialogTitleBar()
        {
            InitializeComponent();
        }

        private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

        private void OnDragPressed(object? sender, PointerPressedEventArgs e)
        {
            // Skip drag if the click originated on an interactive child (e.g. the close button).
            if (e.Source is Control src && src.GetSelfAndVisualAncestors()
                .OfType<Control>()
                .Any(c => c is Button))
            {
                return;
            }

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
                && TopLevel.GetTopLevel(this) is Window window)
            {
                window.BeginMoveDrag(e);
            }
        }

        private void OnClose(object? sender, RoutedEventArgs e)
        {
            (TopLevel.GetTopLevel(this) as Window)?.Close();
        }
    }
}
