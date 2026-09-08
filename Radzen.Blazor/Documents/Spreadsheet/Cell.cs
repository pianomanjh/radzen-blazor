using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

/// <summary>
/// Represents a cell in a spreadsheet.
/// </summary>
public class Cell
{
    /// <summary>
    /// Gets the sheet that contains this cell.
    /// </summary>
    public Worksheet Worksheet { get; private set; }

    // Most cells carry a value and nothing else. The six things only some of them have live here, so
    // a cell that has none of them costs one reference rather than six.
    private sealed class Extras
    {
        public Format? Format;
        public Hyperlink? Hyperlink;
        public string? Formula;
        public FormulaSyntaxTree? FormulaSyntaxTree;
        public IReadOnlyList<string>? ValidationErrors;
        public Action<Cell>? Changed;
    }

    private Extras? extras;

    private Extras Rare => extras ??= new Extras();

    private Format? format
    {
        get => extras?.Format;
        set
        {
            if (value is null && extras is null)
            {
                return;
            }
            Rare.Format = value;
        }
    }

    /// <summary>
    /// Gets or sets the format of the cell. Setting null clears the format.
    /// </summary>
    [AllowNull]
    public Format Format
    {
        get
        {
            var current = extras?.Format;

            if (current is null)
            {
                current = new Format();
                current.Changed += OnFormatChanged;
                Rare.Format = current;
            }
            return current;
        }

        set
        {
            var current = extras?.Format;

            if (ReferenceEquals(current, value))
            {
                return;
            }
            current?.Changed -= OnFormatChanged;

            format = value;

            value?.Changed += OnFormatChanged;

            OnFormatChanged();
        }
    }

    /// <summary>
    /// Clones the cell, creating a new instance with the same properties.
    /// </summary>

    public Cell Clone()
    {
        var clone = new Cell(Worksheet, Address)
        {
            // Share Data: it is immutable, and rebuilding from Value re-infers the type (corrupts "0123").
            Data = Data,
            QuotePrefix = QuotePrefix,
            Hyperlink = Hyperlink?.Clone(),
        };
        // Assign the formula via the backing field, not the property: the setter
        // registers the cell in the worksheet dependency graph and triggers a recalc.
        // A clone is a detached snapshot/transport copy and must stay out of the graph.
        clone.formula = formula;
        clone.FormulaSyntaxTree = FormulaSyntaxTree;
        // Not subscribed to: CopyFrom aliases the format into a live cell, and a stale
        // subscription would root the discarded clone for the format's lifetime.
        clone.format = format?.Clone();
        return clone;
    }

    /// <summary>
    /// Copies the properties from another cell to this cell.
    /// </summary>
    public void CopyFrom(Cell other) => CopyFrom(other, null);

    // The formula override replaces the source formula in the same setter call; adjusting the
    // formula on a detached clone instead would register the clone in the live dependency graph.
    internal void CopyFrom(Cell other, string? formulaOverride)
    {
        ArgumentNullException.ThrowIfNull(other);
        Data = other.Data;
        Formula = formulaOverride ?? other.Formula;
        QuotePrefix = other.QuotePrefix;

        format?.Changed -= OnFormatChanged;

        format = other.format;

        format?.Changed += OnFormatChanged;

        Hyperlink = other.Hyperlink?.Clone();

        extras?.Changed?.Invoke(this);
    }

    // Excel's full clear: contents, format, quote prefix and hyperlink. Clear Contents
    // (which keeps formats) belongs to ClearContentsCommand instead.
    internal void Clear()
    {
        Formula = null;
        Value = null;
        Format = null;
        Hyperlink = null;
        OnChanged();
    }

    internal Format? FormatOrNull => extras?.Format;

    /// <summary>
    /// Gets the text displayed in the cell: the number format applied to the value, or the value as a string.
    /// Rendered with the workbook culture.
    /// </summary>
    public string? GetDisplayText() => FormatDisplayText(Culture);

    /// <summary>
    /// Formats the display text with the specified culture. The XLSX writer measures autofit widths
    /// with the invariant culture so saved files are host-independent.
    /// </summary>
    internal string? FormatDisplayText(CultureInfo culture)
    {
        return NumberFormat.Apply(format?.NumberFormat, Value, ValueType, culture) ?? FormatValue(culture);
    }

    internal Format? GetEffectiveFormat()
    {
        var effectiveFormat = format;

        var conditionalFormat = Worksheet.ConditionalFormats.Calculate(this);

        if (conditionalFormat is not null)
        {
            effectiveFormat = format?.Merge(conditionalFormat) ?? conditionalFormat;
        }

        return effectiveFormat;
    }

    private void OnFormatChanged()
    {
        extras?.Changed?.Invoke(this);
    }

    /// <summary>
    /// Gets or sets the hyperlink associated with this cell.
    /// </summary>
    public Hyperlink? Hyperlink
    {
        get => extras?.Hyperlink;
        set
        {
            if (value is null && extras is null)
            {
                return;
            }
            Rare.Hyperlink = value;
        }
    }

    private object? value;

    private CellDataType type = CellDataType.Empty;

    /// <summary>
    /// Gets the current value and its type as a CellData object.
    /// </summary>
    public CellData Data
    {
        get => value is null ? CellData.Empty : new CellData(value, type);

        internal set
        {
            this.value = value.Value;
            type = value.Type;
        }
    }


    /// <summary>
    /// Gets or sets the value of the cell.
    /// </summary>
    public object? Value
    {
        get => value;
        set
        {
            if (Equals(this.value, value) && !QuotePrefix)
            {
                return;
            }

            CellData.Infer(value, Culture, out var inferred, out var inferredType);

            this.value = inferred;
            type = inferredType;
            QuotePrefix = false;

            Worksheet.OnCellValueChanged(this);
        }
    }

    private CultureInfo Culture => Worksheet.Culture;

    /// <summary>
    /// Sets the cell value with invariant type inference - file content is canonical and must parse
    /// the same on every host.
    /// </summary>
    internal void SetValueInvariant(string? value)
    {
        Data = new CellData(value);
        QuotePrefix = false;

        Worksheet.OnCellValueChanged(this);
    }

    /// <summary>
    /// Stores the value as literal text and sets <see cref="QuotePrefix"/> - the apostrophe protocol
    /// of <see cref="SetValue"/> without the apostrophe.
    /// </summary>
    internal void SetText(string value)
    {
        Formula = null;
        Data = CellData.FromString(value);
        QuotePrefix = true;

        Worksheet.OnCellValueChanged(this);
    }

    /// <summary>
    /// Gets the value of the cell as a string, or the formula if it exists.
    /// </summary>
    public string? GetValue()
    {
        if (!string.IsNullOrEmpty(Formula))
        {
            return FormulaLocalizer.ToLocalized(Formula, Culture);
        }

        var text = GetValueAsString();
        return QuotePrefix && text is not null ? "'" + text : text;
    }

    /// <summary>
    /// Gets the value of the cell as a string, formatted with the workbook culture so that the
    /// text round-trips through <see cref="SetValue"/> under the same culture.
    /// </summary>
    public string? GetValueAsString() => FormatValue(Culture);

    private string? FormatValue(CultureInfo culture) => FormatValue(Value, culture);

    internal static string? FormatValue(object? value, CultureInfo culture)
    {
        return value switch
        {
            null => null,
            CellError error => error.ToString(),
            string str => str,
            IFormattable formattable => formattable.ToString(null, culture),
            _ => value.ToString()
        };
    }

    /// <summary>
    /// Sets the value of the cell based on a string input.
    /// A leading apostrophe escapes the value — the apostrophe is stripped,
    /// the remainder is stored as text, and <see cref="QuotePrefix"/> is set.
    /// Otherwise, a string starting with '=' is treated as a formula.
    /// </summary>
    public void SetValue(string? value)
    {
        if (value is not null && value.StartsWith('\''))
        {
            SetText(value[1..]);
        }
        else if (value?.StartsWith('=') == true && value != "=")
        {
            Formula = FormulaLocalizer.ToInvariant(value, Culture);
        }
        else
        {
            Formula = null;
            Value = string.IsNullOrEmpty(value) ? null : value;
        }
    }

    internal void OnChanged()
    {
        extras?.Changed?.Invoke(this);
    }

    internal event Action<Cell>? Changed
    {
        add => Rare.Changed += value;
        remove
        {
            if (extras is not null)
            {
                extras.Changed -= value;
            }
        }
    }

    /// <summary>
    /// Gets the type of value contained in the cell.
    /// </summary>
    public CellDataType ValueType => type;

    internal FormulaSyntaxTree? FormulaSyntaxTree
    {
        get => extras?.FormulaSyntaxTree;
        private set
        {
            if (value is null && extras is null)
            {
                return;
            }
            Rare.FormulaSyntaxTree = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether the cell value was entered with
    /// a leading apostrophe and should be treated as literal text even if it
    /// looks like a formula. Mirrors Excel's <c>quotePrefix</c> cell flag.
    /// </summary>
    public bool QuotePrefix { get; set; }

    private string? formula
    {
        get => extras?.Formula;
        set
        {
            if (value is null && extras is null)
            {
                return;
            }
            Rare.Formula = value;
        }
    }

    /// <summary>
    /// Gets or sets the formula of the cell.
    /// </summary>
    public string? Formula
    {
        get => formula;
        set
        {
            if (formula == value)
            {
                return;
            }
            formula = value;
            FormulaSyntaxTree = value is not null ? FormulaParser.Parse(value) : null;
            if (value is not null)
            {
                QuotePrefix = false;
            }
            Worksheet.OnCellFormulaChanged(this);
        }
    }

    /// <summary>
    /// Gets a value indicating whether this cell has no meaningful content (no value, formula, format, or hyperlink).
    /// </summary>
    public bool IsEmpty => value is null && extras?.Formula is null && extras?.Format is null &&
        extras?.Hyperlink is null && !QuotePrefix;

    /// <summary>
    /// Gets the address of the cell.
    /// </summary>

    public CellRef Address { get; internal set; }

    /// <summary>
    /// Validates the cell's value against the sheet's validation rules.
    /// </summary>
    public void Validate()
    {
        ValidationErrors = Worksheet.Validation.Validate(this);
    }

    /// <summary>
    /// Gets a value indicating whether the cell has validation errors.
    /// </summary>
    public bool HasValidationErrors => ValidationErrors.Count > 0;

    /// <summary>
    /// Gets the validation errors for the cell.
    /// </summary>
    public IReadOnlyList<string> ValidationErrors
    {
        get => extras?.ValidationErrors ?? [];
        private set
        {
            if (value.Count == 0 && extras is null)
            {
                return;
            }
            Rare.ValidationErrors = value;
        }
    }

    internal void ClearValidationErrors()
    {
        if (extras is not null)
        {
            extras.ValidationErrors = null;
        }
    }

    internal Cell(Worksheet sheet, CellRef address)
    {
        Address = address;
        Worksheet = sheet;
    }

    // Adopts what a slot was holding before anything asked for a cell here. The value and type are
    // taken as they are: re-inferring them would rebuild "0123" as a number.
    internal Cell(Worksheet sheet, CellRef address, object? value, CellDataType type, bool quotePrefix)
    {
        Address = address;
        Worksheet = sheet;
        this.value = value;
        this.type = type;
        QuotePrefix = quotePrefix;
    }

    // What a cell holds when nothing but its value has been asked for, so the writer can read a
    // cell that was never materialised.
    internal object? StoredValue => value;

    internal CellDataType StoredType => type;
}