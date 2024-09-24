using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using HotChocolate.Execution.Processing;
using HotChocolate.Language;
using HotChocolate.Types;
using HotChocolate.Types.Introspection;
using HotChocolate.Utilities;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.Primitives;
using IHasDirectives = HotChocolate.Types.IHasDirectives;

namespace HotChocolate.Caching;

/// <summary>
/// Computes the cache control constraints for an operation during compilation.
/// </summary>
internal sealed class CacheControlConstraintsOptimizer : IOperationOptimizer
{
    public void OptimizeOperation(OperationOptimizerContext context)
    {
        if (context.Definition.Operation is not OperationType.Query ||
            context.HasIncrementalParts ||
            ContainsIntrospectionFields(context))
        {
            // if this is an introspection query we will not cache it.
            return;
        }

        var constraints = ComputeCacheControlConstraints(context.CreateOperation());

        if (constraints.MaxAge is not null || constraints.SharedMaxAge is not null)
        {
            var headerValue = new CacheControlHeaderValue
            {
                Private = constraints.Scope == CacheControlScope.Private,
                MaxAge = constraints.MaxAge is not null ? TimeSpan.FromSeconds(constraints.MaxAge.Value) : null,
                SharedMaxAge = constraints.SharedMaxAge is not null ? TimeSpan.FromSeconds(constraints.SharedMaxAge.Value) : null,
            };

            context.ContextData.Add(
                WellKnownContextData.CacheControlHeaderValue,
                headerValue.ToString());
        }

        if (constraints.Vary is { Length: > 0 })
        {
            context.ContextData.Add(
                WellKnownContextData.VaryHeaderValue,
                string.Join(", ", constraints.Vary));
        }
    }

    private static CacheControlConstraints ComputeCacheControlConstraints(
        IOperation operation)
    {
        var constraints = new CacheControlConstraints();
        var rootSelections = operation.RootSelectionSet.Selections;

        foreach (var rootSelection in rootSelections)
        {
            ProcessSelection(rootSelection, constraints, operation);
        }

        return constraints;
    }

    private static void ProcessSelection(
        ISelection selection,
        CacheControlConstraints constraints,
        IOperation operation)
    {
        var field = selection.Field;
        var maxAgeSet = false;
        var sharedMaxAgeSet = false;
        var scopeSet = false;
        var varySet = false;

        ExtractCacheControlDetailsFromDirectives(field.Directives);

        if (!maxAgeSet || !sharedMaxAgeSet || !scopeSet || !varySet)
        {
            // Either maxAge or scope have not been specified by the @cacheControl
            // directive on the field, so we try to infer these details
            // from the type of the field.

            if (field.Type is IHasDirectives type)
            {
                // The type of the field is complex and can therefore be
                // annotated with a @cacheControl directive.
                ExtractCacheControlDetailsFromDirectives(type.Directives);
            }
        }
        {
            // Either maxAge or scope have not been specified by the @cacheControl
            // directive on the field, so we try to infer these details
            // from the type of the field.

            if (field.Type is IHasDirectives type)
            {
                // The type of the field is complex and can therefore be
                // annotated with a @cacheControl directive.
                ExtractCacheControlDetailsFromDirectives(type.Directives);
            }
        }

        if (selection.SelectionSet is not null)
        {
            var possibleTypes = operation.GetPossibleTypes(selection);

            foreach (var type in possibleTypes)
            {
                var selectionSet = Unsafe.As<SelectionSet>(operation.GetSelectionSet(selection, type));
                var length = selectionSet.Selections.Count;
                ref var start = ref selectionSet.GetSelectionsReference();

                for (var i = 0; i < length; i++)
                {
                    ProcessSelection(Unsafe.Add(ref start, i), constraints, operation);
                }
            }
        }

        void ExtractCacheControlDetailsFromDirectives(
            IDirectiveCollection directives)
        {
            var directive = directives
                .FirstOrDefault(CacheControlDirectiveType.Names.DirectiveName)?
                .AsValue<CacheControlDirective>();

            if (directive is not null)
            {
                if (!maxAgeSet &&
                    directive.MaxAge.HasValue &&
                    (!constraints.MaxAge.HasValue || directive.MaxAge < constraints.MaxAge.Value))
                {
                    // The maxAge of the @cacheControl directive is lower
                    // than the previously lowest maxAge value.
                    constraints.MaxAge = directive.MaxAge.Value;
                    maxAgeSet = true;
                }
                else if (directive.InheritMaxAge == true)
                {
                    // If inheritMaxAge is set, we keep the
                    // computed maxAge value as is.
                    maxAgeSet = true;
                }

                if (!sharedMaxAgeSet &&
                    directive.SharedMaxAge.HasValue &&
                    (!constraints.SharedMaxAge.HasValue || directive.SharedMaxAge < constraints.SharedMaxAge.Value))
                {
                    // The maxAge of the @cacheControl directive is lower
                    // than the previously lowest maxAge value.
                    constraints.SharedMaxAge = directive.SharedMaxAge.Value;
                    sharedMaxAgeSet = true;
                }
                else if (directive.InheritMaxAge == true)
                {
                    // If inheritMaxAge is set, we keep the
                    // computed maxAge value as is.
                    sharedMaxAgeSet = true;
                }

                if (directive.Scope.HasValue &&
                    directive.Scope < constraints.Scope)
                {
                    // The scope of the @cacheControl directive is more
                    // restrictive than the computed scope.
                    constraints.Scope = directive.Scope.Value;
                    scopeSet = true;
                }

                if (directive.Vary is { Length: > 0 })
                {
                    if (constraints.Vary != null)
                    {
                        constraints.Vary = constraints.Vary.Concat(directive.Vary.Select(x => x.ToLowerInvariant())).Distinct().OrderBy(x => x).ToArray();
                    }
                    else
                    {
                        constraints.Vary = directive.Vary.Select(x => x.ToLowerInvariant()).Distinct().OrderBy(x => x).ToArray();
                    }
                    varySet = true;
                }
            }
        }
    }

    private static bool ContainsIntrospectionFields(OperationOptimizerContext context)
    {
        var length = context.RootSelectionSet.Selections.Count;
        ref var start = ref ((SelectionSet)context.RootSelectionSet).GetSelectionsReference();

        for (var i = 0; i < length; i++)
        {
            var field = Unsafe.Add(ref start, i).Field;

            if (field.IsIntrospectionField &&
                !field.Name.EqualsOrdinal(IntrospectionFields.TypeName))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CacheControlConstraints
    {
        public CacheControlScope Scope { get; set; } = CacheControlScope.Public;

        internal int? MaxAge { get; set; }

        internal int? SharedMaxAge { get; set; }

        internal string[]? Vary { get; set; }
    }
}
