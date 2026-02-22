using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BtcTradingBot.Collections;

/// <summary>
/// BeginBatch/EndBatch 사이에서 CollectionChanged 이벤트를 억제하고,
/// EndBatch 시 단일 Reset 이벤트를 발행하여 LiveCharts 리렌더 횟수를 줄인다.
/// </summary>
public class BatchObservableCollection<T> : ObservableCollection<T>
{
    private bool _batching;

    public void BeginBatch() => _batching = true;

    public void EndBatch()
    {
        _batching = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_batching)
            base.OnCollectionChanged(e);
    }
}
