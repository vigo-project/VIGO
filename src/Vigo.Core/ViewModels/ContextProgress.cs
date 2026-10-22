using Vigo.Core.Models;

namespace Vigo.Core.ViewModels;

/// <summary>Posts progress reports to the context that started the operation; inline when there is none.</summary>
internal sealed class ContextProgress(SynchronizationContext? context, Action<GitProgress> handler) : IProgress<GitProgress>
{
    public void Report(GitProgress value)
    {
        if (context is null) handler(value);
        else context.Post(static state => ((Action)state!)(), (Action)(() => handler(value)));
    }
}
