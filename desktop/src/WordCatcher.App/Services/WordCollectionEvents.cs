using System;

namespace WordCatcher.App.Services;

public sealed class WordCollectionEvents
{
    public event Action? WordSaved;

    public void NotifyWordSaved() => WordSaved?.Invoke();
}
