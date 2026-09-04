using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>What a call to <see cref="Attachment{TPayload}.SyncAsync" /> did.</summary>
    internal enum AttachResult
    {
        /// <summary>Nothing to do: bound already with this payload, or not wanted and not bound.</summary>
        Unchanged,

        /// <summary>Bound, and the browser confirmed it.</summary>
        Attached,

        /// <summary>The browser answered that it did not bind. Nothing is bound.</summary>
        Declined,

        /// <summary>The call threw. Nothing is bound.</summary>
        Failed,

        /// <summary>Let go, because it was no longer wanted.</summary>
        Detached,
    }

    /// <summary>
    /// The lifetime of one listener in the browser: whether it is bound, what it was bound with, and
    /// whether the attempt has been made. The caller says what it wants and what to bind with; this
    /// works out whether that means attaching, re-attaching, letting go, or nothing at all.
    /// </summary>
    /// <remarks>
    /// This grid binds two listeners - the pointer listener on the tbody and the key guard on the view
    /// - and before this they were two copies of the same lifetime written a year apart. The copies
    /// disagreed, and the disagreement was a fault rather than a style: the pointer listener recorded
    /// itself attached only once the browser had confirmed it and had a detach for a grid that stopped
    /// delegating, while the key guard recorded itself attached <em>before</em> the call and had no
    /// detach at all. A grid whose keyboard navigation was switched off at runtime therefore stopped
    /// emitting the id the guard is looked up by, which made the guard unreachable - so it outlived the
    /// feature and went on swallowing every arrow key the user pressed.
    /// <para>
    /// The rules that live here rather than in either caller:
    /// </para>
    /// <list type="bullet">
    /// <item>Re-attach when the payload changes, and not otherwise. What the caller must supply for
    /// that to be safe is the one thing this asks of an adapter: <c>attach</c> has to be callable while
    /// something is already bound, and to replace it rather than add to it. Both scripts do that by
    /// detaching first, which is why nothing here spends a second round trip doing it.</item>
    /// <item>Record the binding once it is true of the DOM, never before the call. Recorded first, a
    /// re-attach that threw leaves the grid believing it listens for something it does not.</item>
    /// <item>Forget the attempt with the listener. Left set, a feature switched off and on again reaches
    /// the guard below with an unchanged payload and returns, having neither a listener nor whatever the
    /// caller renders instead of one.</item>
    /// <item>A failure is never reported outwards as an exception. What a listener is for is always
    /// available another way - the pointer listener falls back to per-cell handlers, the key guard to
    /// letting the browser scroll - so a caller that could act on the exception has nothing better to do
    /// than what it does anyway.</item>
    /// </list>
    /// <para>
    /// It holds no element id. The two delegates close over whichever element they bind to, which is
    /// what keeps this about lifetime and not about the DOM: the pointer listener answers with what
    /// <c>attach</c> reported, the key guard with whether the browser could find its element to measure,
    /// and neither meaning belongs here.
    /// </para>
    /// </remarks>
    /// <typeparam name="TPayload">
    /// What the listener was bound with, compared to notice that it has changed. A payload that is
    /// fixed for the life of the grid makes the comparison a formality rather than a cost.
    /// </typeparam>
    internal sealed class Attachment<TPayload>
    {
        readonly Func<TPayload, Task<bool>> attach;
        readonly Func<Task> detach;

        TPayload bound = default!;

        internal Attachment(Func<TPayload, Task<bool>> attach, Func<Task> detach)
        {
            this.attach = attach;
            this.detach = detach;
        }

        /// <summary>Whether a listener is bound right now.</summary>
        internal bool Attached { get; private set; }

        /// <summary>
        /// Whether an attempt has been made and not since let go. Distinct from <see cref="Attached" />:
        /// an attempt the browser declined is not retried, because whatever declined it will decline it
        /// again and the caller has already fallen back.
        /// </summary>
        internal bool Attempted { get; private set; }

        /// <summary>
        /// Brings the listener into line with what the caller wants, and says what that took.
        /// </summary>
        internal async Task<AttachResult> SyncAsync(bool wanted, TPayload payload)
        {
            if (!wanted)
            {
                return await ReleaseAsync().ConfigureAwait(false)
                    ? AttachResult.Detached
                    : AttachResult.Unchanged;
            }

            if (Attempted && EqualityComparer<TPayload>.Default.Equals(bound, payload))
            {
                return AttachResult.Unchanged;
            }

            Attempted = true;

            try
            {
                var confirmed = await attach(payload).ConfigureAwait(false);

                bound = payload;
                Attached = confirmed;

                return confirmed ? AttachResult.Attached : AttachResult.Declined;
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // Deliberately every exception, and the reason is the same one the pointer listener
                // recorded before this existed: the ways an interop call fails are not enumerable from
                // here. A browser that could not fetch the module raises JSException; a circuit torn
                // down mid-call raises one of several cancellation or disposal types; and bUnit's strict
                // mode - the default, so what every consumer's own test suite runs - raises a type this
                // package cannot name. Narrowing it once let that last one escape OnAfterRenderAsync and
                // fail every test that rendered a grid with a click handler.
                //
                // Released first: a re-attach that threw leaves whatever the last successful one bound,
                // and whatever the caller renders instead would then be the second answer to every
                // event.
                await ReleaseAsync().ConfigureAwait(false);

                return AttachResult.Failed;
            }
        }

        /// <summary>
        /// Lets the listener go, and answers whether there was one. Safe to call on a grid that never
        /// attached, and on one whose circuit has already gone.
        /// </summary>
        internal async Task<bool> ReleaseAsync()
        {
            if (!Attached)
            {
                return false;
            }

            Attached = false;
            Attempted = false;
            bound = default!;

            try
            {
                await detach().ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // As above. A circuit that cannot be reached has no listener left to remove.
            }

            return true;
        }
    }
}
