namespace BlazorA2UI.Agent.Basic;

/// <summary>
/// The text styles of <see cref="Text"/>.
/// </summary>
public enum TextVariant
{
    /// <summary>
    /// <c>caption</c>.
    /// </summary>
    Caption,

    /// <summary>
    /// <c>body</c>.
    /// </summary>
    Body,
}

/// <summary>
/// How an <see cref="Image"/> fills its box.
/// </summary>
public enum ImageFit
{
    /// <summary>
    /// <c>contain</c>.
    /// </summary>
    Contain,

    /// <summary>
    /// <c>cover</c>.
    /// </summary>
    Cover,

    /// <summary>
    /// <c>fill</c>.
    /// </summary>
    Fill,

    /// <summary>
    /// <c>none</c>.
    /// </summary>
    None,

    /// <summary>
    /// <c>scaleDown</c>.
    /// </summary>
    ScaleDown,
}

/// <summary>
/// The size presets of <see cref="Image"/>.
/// </summary>
public enum ImageVariant
{
    /// <summary>
    /// <c>icon</c>.
    /// </summary>
    Icon,

    /// <summary>
    /// <c>avatar</c>.
    /// </summary>
    Avatar,

    /// <summary>
    /// <c>smallFeature</c>.
    /// </summary>
    SmallFeature,

    /// <summary>
    /// <c>mediumFeature</c>.
    /// </summary>
    MediumFeature,

    /// <summary>
    /// <c>largeFeature</c>.
    /// </summary>
    LargeFeature,

    /// <summary>
    /// <c>header</c>.
    /// </summary>
    Header,
}

/// <summary>
/// How a <see cref="Row"/> or <see cref="Column"/> spaces its children along its main axis.
/// </summary>
public enum Justify
{
    /// <summary>
    /// <c>start</c>.
    /// </summary>
    Start,

    /// <summary>
    /// <c>center</c>.
    /// </summary>
    Center,

    /// <summary>
    /// <c>end</c>.
    /// </summary>
    End,

    /// <summary>
    /// <c>spaceBetween</c>.
    /// </summary>
    SpaceBetween,

    /// <summary>
    /// <c>spaceAround</c>.
    /// </summary>
    SpaceAround,

    /// <summary>
    /// <c>spaceEvenly</c>.
    /// </summary>
    SpaceEvenly,

    /// <summary>
    /// <c>stretch</c>.
    /// </summary>
    Stretch,
}

/// <summary>
/// How a <see cref="Row"/>, <see cref="Column"/> or <see cref="List"/> aligns its children across its main axis.
/// </summary>
public enum Alignment
{
    /// <summary>
    /// <c>start</c>.
    /// </summary>
    Start,

    /// <summary>
    /// <c>center</c>.
    /// </summary>
    Center,

    /// <summary>
    /// <c>end</c>.
    /// </summary>
    End,

    /// <summary>
    /// <c>stretch</c>.
    /// </summary>
    Stretch,
}

/// <summary>
/// The scroll direction of a <see cref="List"/>.
/// </summary>
public enum ListDirection
{
    /// <summary>
    /// <c>vertical</c>.
    /// </summary>
    Vertical,

    /// <summary>
    /// <c>horizontal</c>.
    /// </summary>
    Horizontal,
}

/// <summary>
/// The direction of a <see cref="Divider"/>.
/// </summary>
public enum DividerAxis
{
    /// <summary>
    /// <c>horizontal</c>.
    /// </summary>
    Horizontal,

    /// <summary>
    /// <c>vertical</c>.
    /// </summary>
    Vertical,
}

/// <summary>
/// The styles of <see cref="Button"/>.
/// </summary>
public enum ButtonVariant
{
    /// <summary>
    /// <c>default</c>.
    /// </summary>
    Default,

    /// <summary>
    /// <c>primary</c>.
    /// </summary>
    Primary,

    /// <summary>
    /// <c>borderless</c>.
    /// </summary>
    Borderless,
}

/// <summary>
/// The input types of <see cref="TextField"/>.
/// </summary>
public enum TextFieldVariant
{
    /// <summary>
    /// <c>shortText</c>.
    /// </summary>
    ShortText,

    /// <summary>
    /// <c>longText</c>.
    /// </summary>
    LongText,

    /// <summary>
    /// <c>number</c>.
    /// </summary>
    Number,

    /// <summary>
    /// <c>obscured</c>.
    /// </summary>
    Obscured,
}

/// <summary>
/// Whether a <see cref="ChoicePicker"/> allows one value or several.
/// </summary>
public enum ChoicePickerVariant
{
    /// <summary>
    /// <c>mutuallyExclusive</c>.
    /// </summary>
    MutuallyExclusive,

    /// <summary>
    /// <c>multipleSelection</c>.
    /// </summary>
    MultipleSelection,
}

/// <summary>
/// How a <see cref="ChoicePicker"/> draws its options.
/// </summary>
public enum ChoiceDisplayStyle
{
    /// <summary>
    /// <c>checkbox</c>.
    /// </summary>
    Checkbox,

    /// <summary>
    /// <c>chips</c>.
    /// </summary>
    Chips,
}
