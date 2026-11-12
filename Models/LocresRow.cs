using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartUnrealLocalization.Models
{
    /// <summary>
    /// Represents a single locres row with virtualization-friendly property change notification.
    /// Key, Source, Target columns.
    /// </summary>
    public partial class LocresRow : ObservableObject
    {
        [ObservableProperty] private string _key = "";
        [ObservableProperty] private string _source = "";
        [ObservableProperty] private string _target = "";
        [ObservableProperty] private bool _isNewKey;
    }
}
