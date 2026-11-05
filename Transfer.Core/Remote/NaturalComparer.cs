using System.Runtime.InteropServices;

namespace Transfer.Core.Remote;

/// <summary>
/// 与资源管理器一致的名称排序；服务端和客户端共用，保证分页边界稳定。
/// 非 Windows 或调用失败时回退到忽略大小写的序数比较。
/// </summary>
public static class NaturalComparer
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string left, string right);

    public static readonly IComparer<string> Instance = Comparer<string>.Create(Compare);

    public static int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;
        if (OperatingSystem.IsWindows())
        {
            try { return StrCmpLogicalW(left, right); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return ManagedCompare(left, right);
    }

    // 逐段比较数字串，保证 2 排在 10 之前；其他字符忽略大小写。
    private static int ManagedCompare(string left, string right)
    {
        int i = 0, j = 0;
        while (i < left.Length && j < right.Length)
        {
            if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
            {
                int startLeft = i, startRight = j;
                while (i < left.Length && char.IsDigit(left[i])) i++;
                while (j < right.Length && char.IsDigit(right[j])) j++;
                ReadOnlySpan<char> numberLeft = left.AsSpan(startLeft, i - startLeft).TrimStart('0');
                ReadOnlySpan<char> numberRight = right.AsSpan(startRight, j - startRight).TrimStart('0');
                if (numberLeft.Length != numberRight.Length) return numberLeft.Length.CompareTo(numberRight.Length);
                int result = numberLeft.CompareTo(numberRight, StringComparison.Ordinal);
                if (result != 0) return result;
                continue;
            }
            char a = char.ToUpperInvariant(left[i]);
            char b = char.ToUpperInvariant(right[j]);
            if (a != b) return a.CompareTo(b);
            i++; j++;
        }
        return (left.Length - i).CompareTo(right.Length - j);
    }
}
