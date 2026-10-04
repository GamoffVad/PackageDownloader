using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PackageDownloader.Models;

public class PackageItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string Id               { get; init; } = "";
    public string Version          { get; init; } = "";
    public string Description      { get; init; } = "";
    public string Authors          { get; init; } = "";
    public long   Downloads        { get; init; }
    public string DownloadsDisplay => Downloads >= 1_000_000_000 ? $"{Downloads / 1_000_000_000.0:F1}B"
                                    : Downloads >= 1_000_000     ? $"{Downloads / 1_000_000.0:F1}M"
                                    : Downloads >= 1_000         ? $"{Downloads / 1_000.0:F0}K"
                                    : Downloads.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
