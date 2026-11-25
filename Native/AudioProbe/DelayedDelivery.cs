using System.Runtime.InteropServices.Marshalling;
using SonicRoute.Native.Audio;

// Only the feasibility test gates a native completion. Production completion has no gate.
[GeneratedComClass]
internal sealed partial class DelayedDelivery(LoopbackCompletion target) : ICompletion, IAgile
{
    internal readonly TaskCompletionSource Arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ActivateCompleted(nint operation)
    {
        Arrived.TrySetResult();
        if (!Resume.Task.Wait(TimeSpan.FromSeconds(6))) return unchecked((int)0x800705B4);
        return target.ActivateCompleted(operation);
    }
}
