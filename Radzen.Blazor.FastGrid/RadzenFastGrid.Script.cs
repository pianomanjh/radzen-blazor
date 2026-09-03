using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    // The one module both browser-facing features import. Clicks and keyboard navigation are separate
    // features with separate switches, and either can be the only one a grid uses - but importing the
    // same path twice would hand the grid two references to the same module and two things to dispose.
    public partial class RadzenFastGrid<TItem>
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        IJSObjectReference? module;

        /// <summary>
        /// The module, imported on first use. Null when there is no JS runtime to import it with, which
        /// is a test host rather than a browser - both callers have a path that works without one.
        /// </summary>
        async ValueTask<IJSObjectReference?> ModuleAsync()
        {
            if (JSRuntime is null)
            {
                return null;
            }

            return module ??= await JSRuntime.InvokeAsync<IJSObjectReference>("import", ModulePath);
        }

        /// <summary>Releases the module, after telling it to let go of the two elements it holds.</summary>
        async ValueTask DisposeScriptAsync()
        {
            if (module is null)
            {
                return;
            }

            try
            {
                if (clickAttachAttempted)
                {
                    await module.InvokeVoidAsync("detach", BodyElementId);
                }

                if (navigationAttached)
                {
                    await module.InvokeVoidAsync("detachNavigation", ViewElementId);
                }

                // A grid fitting to its container is watching that container. Nothing else releases
                // it: the observer is held by the script rather than by anything the circuit owns, so
                // a grid that went away without this would keep redistributing a table nobody is
                // looking at for as long as the page lives.
                if (AutoFitOverflow == AutoFitOverflow.Fit && AutoFitEnabled)
                {
                    await module.InvokeVoidAsync("releaseFit", TableElementId);
                }

                await module.DisposeAsync();
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // The circuit being gone already is the ordinary way this component is disposed, and
                // there is nothing to release when it is. Every exception, for the same reason as the
                // attach: teardown has no caller to report to, and an exception escaping here is
                // unhandled in the circuit rather than handled anywhere.
                //
                // Narrower did not work. JSDisconnectedException derives from Exception and not from
                // JSException, so catching the JS types missed the one case this is actually for, and
                // every navigation away from a grid logged an unhandled circuit exception.
            }

            module = null;
        }
    }
}
