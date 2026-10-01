namespace FsCopilot.Hoppie;

public sealed class SessionRole : IDisposable
{
    private readonly BehaviorSubject<bool> _isJoiner = new(false);

    public bool IsJoiner => _isJoiner.Value;

    public IObservable<bool> Changes => _isJoiner.DistinctUntilChanged();

    public void MarkJoiner() => _isJoiner.OnNext(true);

    public void Reset() => _isJoiner.OnNext(false);

    public void Dispose() => _isJoiner.Dispose();
}
