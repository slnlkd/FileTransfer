using Transfer.Core.Remote;

namespace RemoteFiles.Server;

public partial class MainWindow
{
    partial void ConfigureService(RemoteFileService service)
    {
        var settings = NgFilterSettings.Load();
        OracleFailureAnalysisRepository? annotations = null;
        if (settings is not null)
        {
            var query = new OracleNgFolderQuery(settings);
            service.ListFilter = query.ApplyAsync;
            service.NgBatchQuery = query.FindBatchesAsync;
            service.AnnotationRepository = annotations = new(settings);
        }
        var diagnostics = new OracleAnnotationDiagnostics(settings, annotations, service.NgBatchQuery is not null);
        service.AnnotationDiagnostics = new(diagnostics.Snapshot, diagnostics.VerifyAsync);
    }
}
