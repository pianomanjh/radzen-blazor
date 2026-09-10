using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Where a grid keeps what its user changed, between visits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A grid given a <see cref="RadzenFastGrid{TItem}.StorageKey" /> and nothing else keeps it in the
    /// viewer's own browser, under <c>localStorage</c>. This is how that is replaced - with
    /// <c>sessionStorage</c> for a grid inside a dialog, or with a round trip for settings that should
    /// follow a user between machines. §39 argues why the destination is a service and the key is a
    /// parameter: every application wants a key, and the ones that want somewhere else to put it want
    /// somewhere different from each other.
    /// </para>
    /// <para>
    /// An implementation is asked once per grid on its first render and again on each change a user
    /// makes. It is never asked per row, per cell or per render, so it is free to be as slow as a
    /// network call - but it must not throw: a store that cannot answer should answer nothing, which
    /// the grid reads as "no settings" and draws what its markup declares.
    /// </para>
    /// </remarks>
    public interface IFastGridSettingsStore
    {
        /// <summary>
        /// What was stored under this key, or null where nothing was - which includes a store that
        /// could not read. Never throws.
        /// </summary>
        ValueTask<FastGridSettings?> ReadAsync(string key);

        /// <summary>Stores the settings under this key, replacing whatever was there. Never throws.</summary>
        ValueTask WriteAsync(string key, FastGridSettings settings);

        /// <summary>Forgets this key. Silent for one that holds nothing. Never throws.</summary>
        ValueTask RemoveAsync(string key);
    }

    /// <summary>
    /// The serializer the built-in browser store uses, source-generated rather than reflective.
    /// </summary>
    /// <remarks>
    /// Reflection-based <c>System.Text.Json</c> would take the *Trimming and Native AOT* section's
    /// claim with it: `Radzen.Blazor.FastGrid.TrimTest` publishes with warnings as errors, and a
    /// reflective serializer over a public type is exactly what the linker objects to. A generated
    /// context costs nothing at run time and keeps that claim true.
    /// <para>
    /// It is affordable at all only because of what §32 and §33 made the stored shape: strings, enums
    /// and nullable primitives, with not one <c>object</c> in it. A settings type carrying
    /// <c>object? FilterValue</c> could not be source-generated at all.
    /// </para>
    /// <para>
    /// <strong>Enums are stored by name, not by number.</strong> The default is the number, and a
    /// number is a position in a list that a later edit can move: inserting an operator into
    /// <see cref="FastGridFilterOperator" /> would silently change what every stored blob means, which
    /// is precisely the fault §39 found between this shape and <c>DataGridSettings</c> - two enums
    /// sharing one property name and parting company at 7. A name cannot drift that way, it reads in
    /// the debugger, and it makes a foreign blob's numeric operator fail to parse rather than parse
    /// into the wrong one.
    /// </para>
    /// </remarks>
    [JsonSourceGenerationOptions(UseStringEnumConverter = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(FastGridSettings))]
    internal sealed partial class FastGridSettingsJson : JsonSerializerContext
    {
    }
}
