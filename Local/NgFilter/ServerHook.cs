using Transfer.Core.Remote;

namespace RemoteFiles.Server;

public partial class MainWindow
{
    partial void ConfigureService(RemoteFileService service)
    {
        var query = OracleNgFolderQuery.Load();
        if (query is not null) service.ListFilter = query.ApplyAsync;
        // 标注写 RCMDB.AutoLineFailureAnalysisLog；没有数据库配置时服务端自动回退到本地 JSON。
        if (OracleFailureAnalysisRepository.TryLoad() is { } annotations) service.AnnotationRepository = annotations;
    }
}
