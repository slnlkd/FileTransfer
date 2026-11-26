using System.ComponentModel;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

public sealed class NgDayRow(RemoteNgDay day, RemoteNgBatch[] visibleBatches, bool expanded) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTime Date => day.Date;
    private int Total => day.Batches.Length;
    private int Annotated => day.Batches.Count(batch => batch.Annotated);
    public bool IsComplete => Annotated == Total;
    public string Progress => $"{Annotated}/{Total}";
    public string ProgressDetails => $"NG {Total} · 已标注 {Annotated} · 未标注 {Total - Annotated}";
    public System.Collections.ObjectModel.ObservableCollection<RemoteNgBatch> VisibleBatches { get; } = new(visibleBatches);
    public bool IsExpanded { get; set; } = expanded;
    public void Update(RemoteNgDay updated, RemoteNgBatch[] visible)
    {
        bool countsChanged = Total != updated.Batches.Length || Annotated != updated.Batches.Count(batch => batch.Annotated);
        day = updated;
        for (int index = 0; index < visible.Length; index++)
        {
            var batch = visible[index];
            if (index >= VisibleBatches.Count) VisibleBatches.Add(batch);
            else if (VisibleBatches[index].Path != batch.Path)
            {
                var existing = VisibleBatches.FirstOrDefault(item => item.Path == batch.Path);
                if (existing is null) VisibleBatches.Insert(index, batch);
                else VisibleBatches.Move(VisibleBatches.IndexOf(existing), index);
            }
            if (VisibleBatches[index] != batch) VisibleBatches[index] = batch;
        }
        while (VisibleBatches.Count > visible.Length) VisibleBatches.RemoveAt(VisibleBatches.Count - 1);
        if (countsChanged)
            foreach (string property in new[] { nameof(IsComplete), nameof(Progress), nameof(ProgressDetails) })
                PropertyChanged?.Invoke(this, new(property));
    }
}

