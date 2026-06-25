namespace VestiCode.Core.Conversation;

/// <summary>连续失败达到上限后“打开”，停止自动触发（防止反复失败的昂贵摘要调用）。</summary>
public sealed class CircuitBreaker(int maxFailures = 2)
{
    private int _count;

    public bool IsOpen { get; private set; }

    public void RecordFailure()
    {
        _count++;
        if (_count >= maxFailures)
        {
            IsOpen = true;
        }
    }

    public void RecordSuccess() => Reset();

    public void Reset()
    {
        _count = 0;
        IsOpen = false;
    }
}
