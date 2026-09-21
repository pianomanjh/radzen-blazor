using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// Resolves one of the grid's strings: a registered <see cref="ILocalizer" /> first, then the
    /// consuming application's own <c>RadzenStrings</c> resources, then the ones shipped with
    /// <c>Radzen.Blazor</c>, then the key itself.
    /// </summary>
    /// <remarks>
    /// The order is <c>Radzen.Blazor</c>'s own, so an application that has already overridden one of these
    /// strings for <c>RadzenDataGrid</c> gets the same override here. Its resolver is internal, so this
    /// mirrors it over the public surface - <see cref="ILocalizer" /> and
    /// <see cref="RadzenStrings.ResourceManager" /> - rather than reaching into the package.
    /// </remarks>
    sealed class StringResolver(ILocalizer? custom)
    {
        internal static readonly StringResolver Default = new(null);

        static readonly ConcurrentDictionary<string, ResourceSet[]> appResourceSets = new();
        static readonly ResourceManager? appResources = ResolveAppResources();

        readonly ResourceManager resources = RadzenStrings.ResourceManager;

        internal string Get(string key, CultureInfo culture) =>
            custom?.Get(key, culture) ?? GetAppString(key, culture) ?? resources.GetString(key, culture) ?? key;

        /// <summary>
        /// The application's own <c>Radzen.Blazor.RadzenStrings</c> resources, or null when it has none -
        /// which is the common case, and is why this is resolved once rather than per lookup.
        /// </summary>
        static ResourceManager? ResolveAppResources()
        {
            var assembly = Assembly.GetEntryAssembly();

            return assembly == null || assembly == typeof(RadzenStrings).Assembly
                ? null
                : new ResourceManager("Radzen.Blazor.RadzenStrings", assembly);
        }

        static string? GetAppString(string key, CultureInfo culture)
        {
            if (appResources == null)
            {
                return null;
            }

            foreach (var set in appResourceSets.GetOrAdd(culture.Name, _ => GetAppResourceSets(culture)))
            {
                var value = set.GetString(key);

                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        /// <summary>
        /// The application's resource sets for a culture and each of its parents, most specific first, so
        /// a string translated only for "fr" still answers a lookup in "fr-CA".
        /// </summary>
        static ResourceSet[] GetAppResourceSets(CultureInfo culture)
        {
            var sets = new List<ResourceSet>();

            for (var current = culture; ; current = current.Parent)
            {
                try
                {
                    var set = appResources!.GetResourceSet(current, true, false);

                    if (set != null)
                    {
                        sets.Add(set);
                    }
                }
                catch (MissingManifestResourceException)
                {
                }
                catch (MissingSatelliteAssemblyException)
                {
                }

                if (Equals(current, CultureInfo.InvariantCulture))
                {
                    break;
                }
            }

            return sets.ToArray();
        }
    }
}
