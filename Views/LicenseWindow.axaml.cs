using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartUnrealLocalization.Localization;
using SmartUnrealLocalization.Services;

namespace SmartUnrealLocalization.Views
{
    /// <summary>
    /// Help → Licenses: the program's own license, the list of third-party
    /// components and every full license text, all embedded in the exe. The
    /// legal texts stay in English — a translation would not be the license.
    /// </summary>
    public partial class LicenseWindow : Window
    {
        /// <summary>One entry of the list: what it is called and which text it shows.</summary>
        internal sealed record Doc(string Label, string Resource)
        {
            public override string ToString() => Label;
        }

        public LicenseWindow()
        {
            InitializeComponent();

            // Labels are set once: the window is modal, so the interface language
            // cannot change underneath it (the same as About).
            var docs = new List<Doc>
            {
                new(Loc.T("license.item.app"), LegalTexts.AppLicense),
                new(Loc.T("license.item.notices"), LegalTexts.Notices),
            };
            foreach (var file in LegalTexts.LicenseFiles())
                docs.Add(new Doc(System.IO.Path.GetFileNameWithoutExtension(file.FileName), file.Resource));

            DocList.ItemsSource = docs;
            DocList.SelectedIndex = 0;
        }

        internal IReadOnlyList<Doc> Docs => (IReadOnlyList<Doc>)DocList.ItemsSource!;

        private void OnDocSelected(object? s, SelectionChangedEventArgs e)
        {
            if (DocList.SelectedItem is Doc doc)
            {
                DocText.Text = LegalTexts.Read(doc.Resource);
                DocText.CaretIndex = 0;
            }
        }

        private void OnClose(object? s, RoutedEventArgs e) => Close();
    }
}
