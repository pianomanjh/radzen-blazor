using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Radzen.Blazor;

namespace Radzen.FastGrid
{
    /// <summary>
    /// A column bound to a property expression, for example <c>Property="@(o =&gt; o.Customer.Name)"</c>.
    /// </summary>
    /// <typeparam name="TItem">The row type.</typeparam>
    /// <typeparam name="TProp">The property type.</typeparam>
    public sealed class PropertyColumn<TItem,
        // The column asks TProp whether it is a collection, which means asking for its interfaces. The
        // annotation is what tells a trimmer to keep them; without it the question is answered wrongly
        // rather than not at all, and a collection column would quietly render as its ToString.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] TProp> : ColumnBase<TItem>
    {
        Expression<Func<TItem, TProp>>? property;
        Expression<Func<TItem, TProp>>? sortBy;
        Expression<Func<TItem, TProp>>? filterBy;
        Func<TItem, string?>? cellText;
        Func<TItem, object?>? cellValue;
        string? format;
        string? separator;

        /// <summary>The property this column displays.</summary>
        [Parameter, EditorRequired] public Expression<Func<TItem, TProp>> Property { get; set; } = default!;

        /// <summary>
        /// The property to sort by, when it differs from the one displayed. Required to make a computed
        /// column sortable, since a computed expression has no property path.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? SortBy { get; set; }

        /// <summary>Format string applied to the value, for example <c>"C"</c> or <c>"d"</c>.</summary>
        [Parameter] public string? Format { get; set; }

        /// <summary>
        /// What separates the members of a collection-valued property in the cell. Ignored for a scalar.
        /// </summary>
        [Parameter] public string Separator { get; set; } = ", ";

        /// <summary>
        /// Whether this column is bound to a collection rather than a single value. Its cells list the
        /// members and its filter matches a row when any member matches.
        /// </summary>
        public bool IsCollection => ElementType is not null;

        string? path;
        string? displayPath;

        /// <inheritdoc />
        public override string? SortPath => path;

        /// <inheritdoc />
        /// <remarks>
        /// The displayed property, for the same reason <see cref="HeaderText" /> uses it: a column
        /// showing First and sorting by Last is a column of first names. Naming it "Last" is what gave
        /// it the same identity as the column that really is Last.
        /// </remarks>
        internal override string? DisplayPath => displayPath;

        /// <summary>
        /// The property to filter by, when it differs from the one displayed. Must be of the same type;
        /// a column filtered on an unrelated property is a different column.
        /// </summary>
        [Parameter] public Expression<Func<TItem, TProp>>? FilterBy { get; set; }

        string? filterPath;

        // Once per closed generic type, not once per column: the interface walk allocates, and the
        // answer depends only on TProp. Measured at ~240 B per column when it was computed per instance.
        static readonly Type? ElementType = CollectionElementType(typeof(TProp));

        /// <inheritdoc />
        /// <remarks>
        /// The displayed property, not the sort key: a column showing First and sorting by Last is a
        /// column of first names, and heading it "Last" describes the ordering rather than the cells.
        /// </remarks>
        public override string? HeaderText => Title ?? displayPath;

        /// <inheritdoc />
        public override string? FilterPropertyPath => filterPath;

        /// <inheritdoc />
        public override Type FilterPropertyType => typeof(TProp);

        /// <inheritdoc />
        public override Type FilterElementType => ElementType ?? typeof(TProp);

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Three kinds of column get a control of their own; everything else keeps the base's text
        /// input, whose conversion is this column's own <c>FilterValueFromText</c> and therefore
        /// already reads enums, <see cref="Guid" />s and §34's relative-date tokens.
        /// </para>
        /// <para>
        /// <strong>The constraint that shapes all three is that a control must be able to show
        /// <em>nothing</em>.</strong> A filter slot with no value yet is the ordinary state of an
        /// unfiltered column, and a control that renders <c>default(TProp)</c> instead offers Apply on a
        /// value nobody chose. <c>RadzenDatePicker</c> solves it itself - its <c>HasValue</c> treats
        /// <c>default(DateTime)</c> as absent and formats it as the empty string - so the picker is
        /// closed over <typeparamref name="TProp" /> and a <see cref="DateOnly" /> column gets a
        /// <see cref="DateOnly" /> back, which is the whole reason this method is on the column.
        /// <c>RadzenNumeric</c> does not: its formatter asks <c>_value != null</c>, and a boxed zero is
        /// not null, so <c>RadzenNumeric&lt;int&gt;</c> opens showing <c>0</c>.
        /// </para>
        /// <para>
        /// So the numeric editor is closed over <c>decimal?</c> rather than over
        /// <typeparamref name="TProp" />, and converts at the seam. <strong>The build first read that
        /// constraint as a reason to leave numbers on a text box</strong> - which is what upstream's own
        /// filter UI does - and it is not: the type that cannot hold "nothing" is
        /// <typeparamref name="TProp" />, not the control. <c>decimal</c> holds every integral type
        /// exactly, and what the swap buys is a spinner, a numeric soft keyboard and a control that
        /// parses rather than a box that might not.
        /// </para>
        /// </remarks>
        internal override void RenderFilterEditor(RenderTreeBuilder builder, int sequence,
            FilterEditor editor)
        {
            var underlying = Nullable.GetUnderlyingType(typeof(TProp)) ?? typeof(TProp);

            if (FastGridRelativeDate.AppliesTo(underlying))
            {
                RenderDateEditor(builder, sequence, editor);

                return;
            }

            if (underlying == typeof(bool))
            {
                RenderBooleanEditor(builder, sequence, editor);

                return;
            }

            // TimeSpan and TimeOnly are ordered and are not decimals, so they order and range like a
            // number and edit like text.
            if (Type.GetTypeCode(underlying) is >= TypeCode.SByte and <= TypeCode.Decimal)
            {
                RenderNumericEditor(builder, sequence, editor, underlying);

                return;
            }

            base.RenderFilterEditor(builder, sequence, editor);
        }

        void RenderDateEditor(RenderTreeBuilder builder, int sequence, FilterEditor editor)
        {
            // A token in the slot is text the picker cannot hold - it is not a date, it is a rule for
            // finding one - so the box is what draws it. §34's two vocabularies meet here and the
            // control that can only speak one of them steps aside.
            if (editor.Value is FastGridRelativeDate)
            {
                base.RenderFilterEditor(builder, sequence, editor);

                return;
            }

            builder.OpenComponent<RadzenDatePicker<TProp>>(sequence);
            builder.AddAttribute(sequence + 1, nameof(RadzenDatePicker<TProp>.Value),
                editor.Value is TProp typed ? typed : default);
            builder.AddAttribute(sequence + 2, nameof(RadzenDatePicker<TProp>.Style), "width:100%");
            builder.AddAttribute(sequence + 3, nameof(RadzenDatePicker<TProp>.AllowClear), true);
            builder.AddAttribute(sequence + 4, nameof(RadzenDatePicker<TProp>.InputAttributes),
                EditorAttributes(editor));

            // ValueChanged rather than Change, and the difference is not cosmetic: Change carries a
            // DateTime? whatever TProp is, and ValueChanged carries what the picker converted it to -
            // which is the DateOnly this whole override exists to keep. It also makes the picker
            // consider itself bound, which is what makes AllowClear hand back a default rather than
            // nothing at all.
            builder.AddAttribute(sequence + 5, nameof(RadzenDatePicker<TProp>.ValueChanged),
                EventCallback.Factory.Create<TProp>(this, value => editor.Set(value)));
            builder.CloseComponent();
        }

        void RenderNumericEditor(RenderTreeBuilder builder, int sequence, FilterEditor editor,
            Type underlying)
        {
            builder.OpenComponent<RadzenNumeric<decimal?>>(sequence);
            builder.AddAttribute(sequence + 1, nameof(RadzenNumeric<decimal?>.Value), AsDecimal(editor.Value));
            builder.AddAttribute(sequence + 2, nameof(RadzenNumeric<decimal?>.Style), "width:100%");
            builder.AddAttribute(sequence + 3, nameof(RadzenNumeric<decimal?>.ShowUpDown), false);

            // The same rule as the text editor's oninput, and the browser is what said it was needed:
            // without it the control raises Change on blur only, so Enter - whose keydown arrives before
            // any change event - committed a draft that was still empty and cleared the column instead
            // of filtering it. Immediate costs a re-render of this one control per keystroke; the panel
            // is not redrawn, because the callback's receiver is the column rather than the grid.
            builder.AddAttribute(sequence + 6, nameof(RadzenNumeric<decimal?>.Immediate), true);
            builder.AddAttribute(sequence + 4, nameof(RadzenNumeric<decimal?>.InputAttributes),
                EditorAttributes(editor));
            builder.AddAttribute(sequence + 5, nameof(RadzenNumeric<decimal?>.Change),
                EventCallback.Factory.Create<decimal?>(this,
                    value => editor.Set(FromDecimal(value, underlying))));
            builder.CloseComponent();
        }

        void RenderBooleanEditor(RenderTreeBuilder builder, int sequence, FilterEditor editor)
        {
            // §31's table: a bool offers Equals true/false, so the editor is the choice between them
            // rather than a text box that can be typed wrong. Over object rather than over TProp, for
            // the reason the numeric one is over decimal?: a drop-down closed over a non-nullable bool
            // cannot show "neither", and would open on a column nobody has filtered already saying
            // false.
            builder.OpenComponent<RadzenDropDown<object>>(sequence);
            builder.AddAttribute(sequence + 1, nameof(RadzenDropDown<object>.Data), BooleanChoices);
            builder.AddAttribute(sequence + 2, nameof(RadzenDropDown<object>.Style), "width:100%");
            builder.AddAttribute(sequence + 3, nameof(RadzenDropDown<object>.AllowClear), true);
            builder.AddAttribute(sequence + 4, nameof(RadzenDropDown<object>.Value), editor.Value);
            builder.AddAttribute(sequence + 5, nameof(RadzenDropDown<object>.InputAttributes),
                EditorAttributes(editor));
            builder.AddAttribute(sequence + 6, nameof(RadzenDropDown<object>.Change),
                EventCallback.Factory.Create<object>(this, value => editor.Set(value)));
            builder.CloseComponent();
        }

        /// <summary>
        /// A drafted value as the decimal the numeric editor holds, or null where there is none or it
        /// will not fit.
        /// </summary>
        /// <remarks>
        /// <c>decimal</c> covers every integral type exactly and reaches about 7.9e28, so the only
        /// values that do not fit are <see cref="double" />s and <see cref="float" />s beyond that -
        /// which a person does not type into a filter, but a restored setting can carry. Showing an
        /// empty box is the honest answer there; throwing inside a render is not.
        /// </remarks>
        static decimal? AsDecimal(object? value)
        {
            if (value is null)
            {
                return null;
            }

            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is OverflowException or FormatException
                                                  or InvalidCastException)
            {
                return null;
            }
        }

        /// <summary>What the numeric editor holds, back in the type this column filters by.</summary>
        static object? FromDecimal(decimal? value, Type underlying)
        {
            if (value is not { } number)
            {
                return null;
            }

            try
            {
                return Convert.ChangeType(number, underlying, CultureInfo.InvariantCulture);
            }
            catch (Exception exception) when (exception is OverflowException or FormatException
                                                  or InvalidCastException)
            {
                // Out of the column's range is not a filter, and it is exactly what a half-typed one
                // looks like. Null is what IsPresent reads as "nothing chosen".
                return null;
            }
        }

        /// <summary>
        /// The editor's accessible name, as the splat every Radzen input takes.
        /// </summary>
        /// <remarks>
        /// Held per column rather than built per render. The panel is never removed from the render tree
        /// once built, so a fresh dictionary here is a new parameter identity on every render of the
        /// grid - which re-renders the control for a label that has not changed.
        /// </remarks>
        Dictionary<string, object> EditorAttributes(FilterEditor editor)
        {
            if (editorAttributes is null || !string.Equals(editorAttributesLabel, editor.AriaLabel,
                    StringComparison.Ordinal))
            {
                editorAttributesLabel = editor.AriaLabel;
                editorAttributes = new Dictionary<string, object> { ["aria-label"] = editor.AriaLabel };
            }

            return editorAttributes;
        }

        Dictionary<string, object>? editorAttributes;
        string? editorAttributesLabel;

        // Built once for the type rather than per open: the two values a bool has do not move.
        static readonly object[] BooleanChoices = { true, false };

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// The column's own <see cref="Format" />, so a pill reads what the cells read - <c>$50,000</c>
        /// rather than <c>50000</c>, and a date in the column's own pattern. <see cref="Format" /> lives
        /// here and not on the base, which is why the base cannot apply it and this override exists.
        /// </para>
        /// <para>
        /// <strong>An <see cref="Enum" /> is formatted too, and the first draft excepted it.</strong>
        /// The exception was written against a column declaring <c>Format="C"</c> beside an enum
        /// property, on the reasoning that <c>C</c> is not one of the four specifiers an enum takes and
        /// would therefore throw out of the pill. It does throw - **on the cell**, several thousand
        /// renders earlier, so no pill on that grid is ever drawn and the guard defended nothing. What
        /// it did do was break this method's own purpose for the specifiers that <em>are</em> valid: a
        /// column declaring <c>Format="D"</c> drew <c>1</c> in every cell and <c>Senior engineer</c> in
        /// its pill. The mutation loop found the guard unobservable and the probe found it wrong.
        /// </para>
        /// </remarks>
        internal override string? FilterValueTextOf(object? value) =>
            value is null || Format is not { Length: > 0 } format
                ? base.FilterValueTextOf(value)
                : CellText.Of(value, format);

        /// <inheritdoc />
        protected override void OnDerive()
        {
            // Equivalent rather than ReferenceEquals: Razor hands this a freshly built expression tree on
            // every render, so reference equality never holds for a column authored in markup and the
            // column recompiled per render. Measured at 5x the render cost of a grid that did not.
            if (format == Format
                && separator == Separator
                && PropertyPathResolver.Equivalent(property, Property)
                && PropertyPathResolver.Equivalent(sortBy, SortBy)
                && PropertyPathResolver.Equivalent(filterBy, FilterBy))
            {
                return;
            }

            property = Property;
            sortBy = SortBy;
            filterBy = FilterBy;
            format = Format;

            // Separator is baked into the delegate below, so it belongs in the guard above: without it
            // a column bound to a user's choice of separator kept the first one for good.
            separator = Separator;

            // Built by a static method rather than inline: the lambdas below capture the compiled getter,
            // and a lambda capturing a local makes the compiler allocate the enclosing method's display
            // class on entry - here, on every parameter set of every column, taken branch or not.
            // A column with no Property renders empty cells rather than throwing out of the render: the
            // parameter is EditorRequired, which is a warning, not a guarantee.
            var compiled = Property?.Compile();

            cellText = compiled is null
                ? null
                : BuildCellText(compiled, Separator, Format is { Length: > 0 } ? Format : null);

            // The same compiled getter, boxed on the way out. Built here rather than on demand because
            // the alternative is compiling the expression once per exported cell.
            cellValue = compiled is null ? null : item => compiled(item);

            var propertyPath = PropertyPathResolver.For(Property);

            displayPath = propertyPath;
            path = SortBy is null ? propertyPath : PropertyPathResolver.For(SortBy);

            // Filtering follows the displayed property, not the sort key: a column that displays First
            // and sorts by Last still filters on what the reader can see.
            //
            // A computed column therefore has no filter path at all, and must not borrow the sort key
            // as one. ApplyFilter composes its predicate from the display expression while this path is
            // what the reflective route filters by, so borrowing made the column filter a different
            // member depending on which route ran - and which route runs is decided by whether some
            // other column declined. It declines to filter instead, as it already declines to sort,
            // and FilterBy is how such a column is given something to filter on.
            filterPath = FilterBy is not null ? PropertyPathResolver.For(FilterBy) : propertyPath;

            // The compiled getters are derived state like everything else here, and stale ones would
            // filter and sort by the expression the column used to have.
            filterGetter = null;
            sortGetter = null;
        }

        /// <summary>
        /// The cell's text, as a delegate built once per column. Compiled to a
        /// <c>Func&lt;TItem, string&gt;</c> rather than read as an object: RenderTreeBuilder has no
        /// generic AddContent, so handing it a value type binds the object overload, which boxes and then
        /// stringifies. Producing the string directly skips the box.
        /// </summary>
        static Func<TItem, string?> BuildCellText(Func<TItem, TProp> get, string separator, string? format)
        {
            // A collection is listed, not stringified: List<string>.ToString() is the type name, which is
            // why every such column needed a Template that did nothing but string.Join. TProp = object
            // cannot say statically whether the value is a collection, so there the value decides.
            if (ElementType is not null || typeof(TProp) == typeof(object))
            {
                // Built here, not per cell: Join takes how a member is rendered as a delegate, and one
                // allocated inside the cell delegate would be one allocation per cell.
                Func<object?, string?> show = value => CellText.Of(value, format);

                return item => get(item) switch
                {
                    null => null,
                    string text => CellText.Of(text, format),
                    IEnumerable sequence => CellText.Join(sequence, separator, show),
                    var value => CellText.Of(value, format),
                };
            }

            if (format is null)
            {
                return item => get(item)?.ToString();
            }

            // A value type has to be formatted through a delegate typed at the value's own type, or the
            // cast to IFormattable boxes it - once per cell, for the whole life of the grid. The generic
            // method below calls the interface under a constraint, which the JIT compiles to a direct
            // call on the struct. A reference type needs none of this: casting one is free.
            var underlying = Nullable.GetUnderlyingType(typeof(TProp));

            // Closing that generic method over the value's type is the only part of this that needs code
            // generated at run time. Under Native AOT the fall-through below still formats correctly -
            // it just boxes to reach IFormattable, which is what this whole branch exists to avoid, so
            // it is a cost paid per cell rather than a feature lost.
            if (DynamicCode.Supported)
            {
                if (underlying is not null && typeof(IFormattable).IsAssignableFrom(underlying))
                {
                    return Formatter(NullableFormatterMethod, underlying, get, format);
                }

                if (typeof(TProp).IsValueType && typeof(IFormattable).IsAssignableFrom(typeof(TProp)))
                {
                    return Formatter(ValueFormatterMethod, typeof(TProp), get, format);
                }
            }

            if (typeof(IFormattable).IsAssignableFrom(typeof(TProp)))
            {
                return item => ((IFormattable?)(object?)get(item))?.ToString(format, CultureInfo.CurrentCulture);
            }

            // TProp says nothing about whether the value can be formatted. Ask the value.
            return item => CellText.Of(get(item), format);
        }

        static readonly MethodInfo ValueFormatterMethod = typeof(PropertyColumn<TItem, TProp>)
            .GetMethod(nameof(ValueFormatter), BindingFlags.NonPublic | BindingFlags.Static)!;

        static readonly MethodInfo NullableFormatterMethod = typeof(PropertyColumn<TItem, TProp>)
            .GetMethod(nameof(NullableFormatter), BindingFlags.NonPublic | BindingFlags.Static)!;

        static Func<TItem, string?> Formatter(MethodInfo method, Type valueType, Func<TItem, TProp> get,
            string format) =>
            (Func<TItem, string?>)method.MakeGenericMethod(valueType).Invoke(null, new object[] { get, format })!;

        static Func<TItem, string?> ValueFormatter<T>(Func<TItem, T> get, string format)
            where T : struct, IFormattable =>
            item => get(item).ToString(format, CultureInfo.CurrentCulture);

        static Func<TItem, string?> NullableFormatter<T>(Func<TItem, T?> get, string format)
            where T : struct, IFormattable =>
            item => get(item) is { } value ? value.ToString(format, CultureInfo.CurrentCulture) : null;

        /// <inheritdoc />
        /// <remarks>
        /// The same condition the two filter routes refuse under, for the reason the base's remark
        /// gives: a blank is a null riding in an <c>In</c> list, and a column that hands its filter to
        /// the reflective route cannot carry one there - the null is dropped on the way, so the box
        /// ticks, commits, and narrows to no rows at all rather than to the rows with nothing in them.
        /// A collection column is refused for a second reason as well, which is
        /// <see cref="CollectionColumn{TItem, TElement}" />'s: "has no regions at all" is a different
        /// question from "has a region that is null".
        /// </remarks>
        private protected override bool OffersBlank => base.OffersBlank && ComposesItsOwnFilter;

        /// <summary>
        /// Whether this column builds its own filter rather than handing the composition over.
        /// </summary>
        /// <remarks>
        /// Both filter routes asked this as the same four-clause condition written twice, which is the
        /// shape §34's and §35's reviews each found once and §36 found again in
        /// <c>FastGridFilterOperators</c>. Naming it is what let <see cref="OffersBlank" /> ask it as
        /// well without becoming a third copy.
        /// </remarks>
        bool ComposesItsOwnFilter =>
            !IsCollection && typeof(TProp) != typeof(object) && FilterMemberPath is null;

        /// <inheritdoc />
        /// <remarks>
        /// Composed rather than enumerated, so an Entity Framework source runs SELECT DISTINCT rather
        /// than pulling every row across the wire. A collection column offers its members.
        /// </remarks>
        public override IQueryable? DistinctValues(IQueryable<TItem> source)
        {
            if (source is null || Property is null)
            {
                return null;
            }

            // A non-collection column projects through its own typed expression below; only the
            // collection branch has to close SelectMany over an element type known at run time.
            if (IsCollection && !DynamicCode.Supported)
            {
                return null;
            }

            // FilterBy, not Property: the values offered have to be the ones the filter compares, or
            // the list shows one column's values and every choice filters another column by them.
            var selector = FilterBy ?? Property;

            if (!IsCollection)
            {
                // Queryable.Distinct by its full name, not the extension-method form. Radzen's own
                // Distinct(this IQueryable) is non-generic, C# prefers a non-generic candidate to a
                // generic one, and it therefore won - so this typed projection was going through the
                // reflective distinct and composing Cast nodes a provider then had to translate. Naming
                // the generic one keeps the element type, and keeps this off the reflective path.
                return Queryable.Distinct(source.Select(selector));
            }

            // TProp is the collection, so the element type is not a type parameter here and SelectMany
            // has to be built by hand. CollectionColumn<TItem, TElement> has it as a parameter and does
            // this as an ordinary generic call.
            return Projection
                .SelectMany(source, typeof(TItem), ElementType!, AsSequenceSelector(selector))
                .Distinct();
        }

        /// <summary>
        /// The property expression retyped as returning <c>IEnumerable&lt;TElement&gt;</c>, which is what
        /// SelectMany's signature demands - a lambda returning <c>List&lt;T&gt;</c> is not the same
        /// delegate type, however assignable the values are.
        /// </summary>
        [RequiresDynamicCode("Closes IEnumerable<> and Func<,> over an element type known at run time.")]
        static LambdaExpression AsSequenceSelector(Expression<Func<TItem, TProp>> selector)
        {
            var sequenceType = typeof(IEnumerable<>).MakeGenericType(ElementType!);

            if (typeof(TProp) == sequenceType)
            {
                return selector;
            }

            // A widening reference conversion, which every provider strips before translating.
            return Expression.Lambda(
                typeof(Func<,>).MakeGenericType(typeof(TItem), sequenceType),
                Expression.Convert(selector.Body, sequenceType),
                selector.Parameters);
        }

        /// <inheritdoc />
        public override string? CellTextOf(TItem item) => cellText?.Invoke(item);

        /// <summary>
        /// The property's value, boxed - this being the one column whose text is a formatting of a
        /// value rather than the value itself.
        /// </summary>
        /// <remarks>
        /// Off <c>cellValue</c> rather than off <see cref="Property" />, for the reason the field beside
        /// it exists: compiling the expression per call would compile it per cell of an export.
        /// </remarks>
        public override object? CellValueOf(TItem item) => cellValue?.Invoke(item);

        /// <inheritdoc />
        public override string? CellFormat => Format;

        /// <summary>
        /// A collection column has nothing to order by: no provider can sort rows by a list, and
        /// <see cref="SortBy" /> here is typed at <typeparamref name="TProp" />, which for such a column
        /// is the collection - so the only sort key the type parameter admits is another uncomparable
        /// one, and offering it produced a clickable header that threw on the first click. Use
        /// <see cref="CollectionColumn{TItem, TElement}" />, whose SortBy names a member instead. A
        /// column typed as <c>object</c> whose values happen to be collections cannot be recognised
        /// statically and stays sortable; give it a real type, or set <c>Sortable="false"</c>.
        /// </summary>
        public override bool CanSort => Sortable && path is not null && !IsCollection;

        /// <summary>
        /// The element type of a collection-valued property, or null when the property is a single value.
        /// </summary>
        static Type? CollectionElementType(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.Interfaces)] Type type)
        {
            // IsEnumerable excludes string, which is a sequence of characters and would otherwise be
            // listed one letter at a time. An array needs no case of its own: GetElementType has one.
            if (!QueryableExtension.IsEnumerable(type))
            {
                return null;
            }

            var element = PropertyAccess.GetElementType(type);

            // GetElementType answers with the type itself when it finds no IEnumerable<T> to read an
            // element type from - a non-generic IEnumerable, whose members are only known as objects.
            return element == type ? typeof(object) : element;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Declines - and leaves the grid to build the predicate by reflection - in the three cases
        /// where <typeparamref name="TProp" /> is not the type being compared:
        /// <list type="bullet">
        /// <item>a collection-valued column, whose filter compares an <em>element</em> whose type is not
        /// a parameter here. <see cref="CollectionColumn{TItem, TElement}" /> has it as one;</item>
        /// <item>a column declared as <c>object</c>, where the real type is only reachable through the
        /// property path - the case this class already documents as worth giving a real type;</item>
        /// <item>a filter aimed at a member of a collection's element, which is the same problem.</item>
        /// </list>
        /// </remarks>
        public override Expression<Func<TItem, bool>>? ApplyFilter(FilterCaseSensitivity caseSensitivity,
            bool inMemory)
        {
            if (!ComposesItsOwnFilter || (FilterBy ?? Property) is not { } selector)
            {
                return null;
            }

            return ActiveFilter is { } filter
                ? FilterExpression<TItem, TProp>.For(selector, filter, caseSensitivity, inMemory)
                : null;
        }

        // Compiled on first use rather than in Derive: a grid over a queryable never needs either, and
        // a compile is about 250 us - and, under Native AOT, an interpreted lambda rather than emitted
        // code. Cleared with the rest of the derived state when the expressions change.
        Func<TItem, TProp>? filterGetter;
        Func<TItem, TProp>? sortGetter;

        /// <inheritdoc />
        public override Func<TItem, bool>? ApplyFilterInMemory(FilterCaseSensitivity caseSensitivity)
        {
            if (!ComposesItsOwnFilter || (FilterBy ?? Property) is not { } selector)
            {
                return null;
            }

            return ActiveFilter is { } filter
                ? FilterExpression<TItem, TProp>.PredicateFor(filterGetter ??= selector.Compile(), filter,
                    caseSensitivity)
                : null;
        }

        /// <inheritdoc />
        public override IOrderedEnumerable<TItem>? ApplySortInMemory(IEnumerable<TItem> source,
            bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } selector)
            {
                return null;
            }

            sortGetter ??= selector.Compile();

            return descending ? source.OrderByDescending(sortGetter) : source.OrderBy(sortGetter);
        }

        /// <inheritdoc />
        public override IOrderedEnumerable<TItem>? ApplyThenByInMemory(IOrderedEnumerable<TItem> source,
            bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } selector)
            {
                return null;
            }

            sortGetter ??= selector.Compile();

            return descending ? source.ThenByDescending(sortGetter) : source.ThenBy(sortGetter);
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplySort(IQueryable<TItem> source, bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } expression)
            {
                return null;
            }

            return descending ? source.OrderByDescending(expression) : source.OrderBy(expression);
        }

        /// <inheritdoc />
        public override IOrderedQueryable<TItem>? ApplyThenBy(IOrderedQueryable<TItem> source, bool descending)
        {
            if (!CanSort || (SortBy ?? Property) is not { } expression)
            {
                return null;
            }

            return descending ? source.ThenByDescending(expression) : source.ThenBy(expression);
        }
    }
}
