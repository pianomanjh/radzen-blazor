using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Wraps a handler so Blazor does not re-render the component after it runs.
    /// </summary>
    /// <remarks>
    /// <see cref="ComponentBase" /> implements <see cref="IHandleEvent" /> by calling
    /// <c>StateHasChanged</c> around every event, which for a handler that already decides when to render
    /// is a wasted render per keystroke. Giving the callback a different receiver - one whose
    /// <see cref="IHandleEvent" /> just invokes the work - opts that single handler out.
    /// <para>
    /// <c>Radzen.Blazor</c> has the same helper, and it is internal, so this is the standalone equivalent.
    /// </para>
    /// </remarks>
    static class NonRenderingHandler
    {
        internal static Func<TValue, Task> Wrap<TValue>(Func<TValue, Task> callback) =>
            new Receiver<TValue>(callback).Invoke;

        sealed record Receiver<TValue>(Func<TValue, Task> Callback) : IHandleEvent
        {
            internal Task Invoke(TValue arg) => Callback(arg);

            public Task HandleEventAsync(EventCallbackWorkItem item, object? arg) => item.InvokeAsync(arg);
        }
    }
}
