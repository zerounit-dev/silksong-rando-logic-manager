namespace Silksong_Rando_Logic_Manager.Services;

// A synchronous, operation-local call stack. Iterator state holds a suspended
// recurrence's locals; yielding a child evaluates ONLY that child before resuming.
// No scheduling, retained cache, frontier expansion or alternate evaluation path.
internal abstract class AuditEvaluationFrame
{
    internal abstract IEnumerator<AuditEvaluationFrame> Steps();

    protected void Evaluate()
    {
        var stack = new Stack<IEnumerator<AuditEvaluationFrame>>();
        stack.Push(Steps());
        try
        {
            while (stack.Count != 0)
            {
                var current = stack.Peek();
                if (current.MoveNext()) stack.Push(current.Current.Steps());
                else { stack.Pop(); current.Dispose(); }
            }
        }
        finally
        {
            // Match nested foreach unwinding on cancellation/ordinary failure.
            while (stack.TryPop(out var current)) current.Dispose();
        }
    }
}

internal sealed class AuditEvaluationFrame<T>(Func<AuditEvaluationFrame<T>, IEnumerable<AuditEvaluationFrame>> steps) : AuditEvaluationFrame
{
    internal T Result { get; set; } = default!;
    internal override IEnumerator<AuditEvaluationFrame> Steps() => steps(this).GetEnumerator();
    internal T Run() { Evaluate(); return Result; }
}
