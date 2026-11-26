using System.Collections;
using System.ComponentModel;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public sealed class RemoteEntryComparer(string member, ListSortDirection direction) : IComparer
{
    public int Compare(object? x, object? y)
    {
        var left = (RemoteEntry)x!;
        var right = (RemoteEntry)y!;
        // 与资源管理器一致：文件夹始终排在文件前，组内按当前列排序。
        if (left.IsDirectory != right.IsDirectory) return left.IsDirectory ? -1 : 1;
        int result = member switch
        {
            "Name" => NaturalComparer.Compare(left.Name, right.Name),
            "Kind" => string.Compare(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase),
            "Size" => left.Length.CompareTo(right.Length),
            "CreatedLocal" => left.CreatedLocal.CompareTo(right.CreatedLocal),
            "ModifiedLocal" => left.ModifiedLocal.CompareTo(right.ModifiedLocal),
            "AnnotationStatus" => string.Compare(left.AnnotationStatus, right.AnnotationStatus, StringComparison.OrdinalIgnoreCase),
            _ => 0
        };
        return direction == ListSortDirection.Descending ? -Math.Sign(result) : Math.Sign(result);
    }
}
