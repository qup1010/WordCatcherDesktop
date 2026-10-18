using System;

namespace WordCatcher.App.Services;

public sealed class WordCollectionEvents
{
    public event Action? WordSaved;
    public event Action? CollectionChanged;

    public void NotifyWordSaved()
    {
        WordSaved?.Invoke();
        NotifyCollectionChanged();
    }

    public void NotifyCollectionChanged() => CollectionChanged?.Invoke();
}
