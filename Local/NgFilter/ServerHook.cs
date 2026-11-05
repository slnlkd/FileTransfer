using Transfer.Core.Remote;

namespace RemoteFiles.Server;

public partial class MainWindow
{
    partial void ConfigureService(RemoteFileService service)
    {
        var query = OracleNgFolderQuery.Load();
        if (query is not null) service.ListFilter = query.ApplyAsync;
    }
}
