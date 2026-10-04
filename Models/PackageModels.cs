namespace PackageDownloader.Models;

public record PackageSearchResult(
    string Id,
    string Version,
    string Description,
    string Authors,
    long Downloads
);
