using CodexHistorySync.Core.Annotations;
using CodexHistorySync.Core.Management;
using CodexHistorySync.Core.Search;
using CodexHistorySync.Core.Viewing;

namespace CodexHistorySync.Desktop;

/// <summary>The local services used by the graphical session viewer.</summary>
public sealed record DesktopSessionServices(
    ILocalSessionCatalog Catalog,
    ISessionTraceReader Traces,
    ISessionFamilyReader Families,
    ISessionContentReader Conversations,
    ISessionAnnotationStore Annotations,
    ILocalSessionOperations? Operations = null,
    ISessionTitleSuggester? TitleSuggester = null,
    ISessionSearchIndex? SearchIndex = null,
    string BuildLabel = "development build");
