using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Controls
{
    /// <summary>One row of the review: a planned change and whether to apply it.</summary>
    public partial class FixReviewItem : ObservableObject
    {
        public FixReviewItem(PlannedFix plan) => Plan = plan;

        public PlannedFix Plan { get; }
        public string Key => Plan.Row.Key ?? "";
        public string Labels => string.Join(" · ", Plan.Labels);
        public string Before => Plan.Before;
        public string After => Plan.After;

        [ObservableProperty] private bool _include = true;
    }

    /// <summary>
    /// Shows every change the validator's fixes would make — both sides, changed
    /// words highlighted, invisible whitespace drawn — and returns the ones the
    /// user kept ticked; null when cancelled. Nothing is written here.
    /// </summary>
    public partial class FixReviewDialog : Window
    {
        private readonly List<FixReviewItem> _items = new();

        public FixReviewDialog() => AvaloniaXamlLoader.Load(this);

        public FixReviewDialog(IReadOnlyList<PlannedFix> fixes, int refused) : this()
        {
            _items.AddRange(fixes.Select(f => new FixReviewItem(f)));
            foreach (var i in _items) i.PropertyChanged += (_, _) => UpdateApply();

            Title = Loc.T("fixReview.title");
            if (this.FindControl<DialogTitleBar>("TitleBar") is { } tb) tb.Title = Title;
            this.FindControl<ListBox>("ItemsList")!.ItemsSource = _items;
            this.FindControl<TextBlock>("CountText")!.Text = string.Format(Loc.T("fixReview.count"), _items.Count);
            var refusedText = this.FindControl<TextBlock>("RefusedText")!;
            refusedText.IsVisible = refused > 0;
            refusedText.Text = string.Format(Loc.T("fixReview.refused"), refused);
            UpdateApply();
        }

        private void UpdateApply()
        {
            int n = _items.Count(i => i.Include);
            var apply = this.FindControl<Button>("BtnApply")!;
            apply.Content = string.Format(Loc.T("fixReview.apply"), n);
            apply.IsEnabled = n > 0;
        }

        private void OnSelectAll(object? s, RoutedEventArgs e) { foreach (var i in _items) i.Include = true; }
        private void OnSelectNone(object? s, RoutedEventArgs e) { foreach (var i in _items) i.Include = false; }
        private void OnCancel(object? s, RoutedEventArgs e) => Close(null);
        private void OnApply(object? s, RoutedEventArgs e)
            => Close(_items.Where(i => i.Include).Select(i => i.Plan).ToList());

        /// <summary>Show modally; the kept fixes, or null when cancelled.</summary>
        public static Task<List<PlannedFix>?> ShowAsync(Window owner, IReadOnlyList<PlannedFix> fixes, int refused)
            => new FixReviewDialog(fixes, refused).ShowDialog<List<PlannedFix>?>(owner);
    }
}
