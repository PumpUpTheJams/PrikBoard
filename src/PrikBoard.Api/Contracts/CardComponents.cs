using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>
/// One component of a card's content, on the card's own canvas (shown when the card is expanded). Coordinates are card
/// pixels from the top-left of that canvas, multiples of the 20 px grid.
/// </summary>
/// <param name="Id">Component identifier, unique within the card.</param>
/// <param name="Kind">Text, Checklist or Links. Only the matching content field is set; the others are null.</param>
/// <param name="X">Left edge on the card's canvas.</param>
/// <param name="Y">Top edge on the card's canvas.</param>
/// <param name="Width">Width in card pixels.</param>
/// <param name="Height">Height in card pixels.</param>
/// <param name="Title">Optional heading, e.g. "Acceptance criteria".</param>
/// <param name="Text">The text of a Text component, in Markdown (GitHub flavor; raw HTML is shown as text).</param>
/// <param name="Items">The items of a Checklist component, in order.</param>
/// <param name="Links">The links of a Links component, in order.</param>
public record CardComponentDetail(
    Guid Id,
    CardComponentKind Kind,
    int X,
    int Y,
    int Width,
    int Height,
    string? Title,
    string? Text,
    IReadOnlyList<ChecklistItem>? Items,
    IReadOnlyList<CardLink>? Links)
{
    public static CardComponentDetail From(CardComponent component) =>
        new(component.Id, component.Kind, component.X, component.Y, component.Width, component.Height, component.Title,
            component.Kind == CardComponentKind.Text ? component.Text ?? "" : null,
            component.Kind == CardComponentKind.Checklist ? component.Items.Select(c => new ChecklistItem(c.Text, c.Done)).ToList() : null,
            component.Kind == CardComponentKind.Links ? component.Links.Select(l => new CardLink(l.Title, l.Url)).ToList() : null);
}

/// <summary>
/// A new component on a card's canvas. Give only the content that fits its kind: text for Text, items for Checklist,
/// links for Links. Without X and Y it goes in the first free spot; without a size it gets the default for its kind.
/// </summary>
/// <param name="Kind">Text, Checklist or Links.</param>
/// <param name="Title">Optional heading, e.g. "Acceptance criteria".</param>
/// <param name="Text">The text of a Text component, in Markdown (GitHub flavor).</param>
/// <param name="Items">The items of a Checklist component.</param>
/// <param name="Links">The links of a Links component.</param>
/// <param name="X">Left edge on the card's canvas; snapped to the 20 px grid. Give it together with Y.</param>
/// <param name="Y">Top edge on the card's canvas; snapped to the 20 px grid. Give it together with X.</param>
/// <param name="Width">Width in card pixels; snapped to the grid, at least 160.</param>
/// <param name="Height">Height in card pixels; snapped to the grid, at least 80.</param>
/// <param name="Id">Only for putting back a component that was removed (addCardComponent): the id it had. Leave it out otherwise; createCard ignores it.</param>
public record NewCardComponent(
    [property: Required] CardComponentKind? Kind,
    [property: StringLength(CardComponent.MaxTitleLength)] string? Title = null,
    [property: StringLength(CardComponent.MaxTextLength)] string? Text = null,
    [property: MaxLength(CardComponent.MaxItems)] List<ChecklistItem>? Items = null,
    [property: MaxLength(CardComponent.MaxLinks)] List<CardLink>? Links = null,
    [property: Range(0, Layout.CardCanvasMax)] int? X = null,
    [property: Range(0, Layout.CardCanvasMax)] int? Y = null,
    [property: Range(1, Layout.MaxComponentSize)] int? Width = null,
    [property: Range(1, Layout.MaxComponentSize)] int? Height = null,
    Guid? Id = null);

/// <summary>
/// Partially updates a component: its content, position and/or size. Omitted (null) fields stay unchanged. A list that is
/// given replaces the component's whole list. To change a little without sending it all back, use appendText, addItems,
/// checkItems and uncheckItems, which apply after the other fields. Only content that fits the component's kind is accepted.
/// </summary>
/// <param name="Title">New heading; an empty string clears it.</param>
/// <param name="Text">New text of a Text component, in Markdown; an empty string clears it.</param>
/// <param name="Items">Replacement items of a Checklist component.</param>
/// <param name="Links">Replacement links of a Links component.</param>
/// <param name="X">New left edge on the card's canvas; snapped to the 20 px grid.</param>
/// <param name="Y">New top edge on the card's canvas; snapped to the 20 px grid.</param>
/// <param name="Width">New width; snapped to the grid, at least 160.</param>
/// <param name="Height">New height; snapped to the grid, at least 80.</param>
/// <param name="AppendText">Text to add at the end of a Text component, as a new paragraph; the text already there stays.</param>
/// <param name="AddItems">Items to add at the end of a Checklist component.</param>
/// <param name="CheckItems">The texts of checklist items to tick off (whole text, ignoring case); the other items stay as they are.</param>
/// <param name="UncheckItems">The texts of checklist items to untick, like checkItems.</param>
public record UpdateCardComponentRequest(
    [property: StringLength(CardComponent.MaxTitleLength)] string? Title = null,
    [property: StringLength(CardComponent.MaxTextLength)] string? Text = null,
    [property: MaxLength(CardComponent.MaxItems)] List<ChecklistItem>? Items = null,
    [property: MaxLength(CardComponent.MaxLinks)] List<CardLink>? Links = null,
    [property: Range(0, Layout.CardCanvasMax)] int? X = null,
    [property: Range(0, Layout.CardCanvasMax)] int? Y = null,
    [property: Range(1, Layout.MaxComponentSize)] int? Width = null,
    [property: Range(1, Layout.MaxComponentSize)] int? Height = null,
    [property: StringLength(CardComponent.MaxTextLength)] string? AppendText = null,
    [property: MaxLength(CardComponent.MaxItems)] List<ChecklistItem>? AddItems = null,
    [property: MaxLength(CardComponent.MaxItems)] List<string>? CheckItems = null,
    [property: MaxLength(CardComponent.MaxItems)] List<string>? UncheckItems = null);
