using System.Diagnostics.CodeAnalysis;
using Content.Shared.StatusIcon;
using JetBrains.Annotations;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.RichText;
using Robust.Shared.Prototypes;
using Robust.Shared.Utility;

namespace Content.Client._Starlight.Language;

/// <summary>
/// Displays Starlight's language icons in chat using Frontier's rich text API.
/// </summary>
[UsedImplicitly]
public sealed class LanguageIconTag : IMarkupTag
{
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IEntitySystemManager _systems = default!;

    public string Name => "languageicon";

    public bool TryGetControl(MarkupNode node, [NotNullWhen(true)] out Control? control)
    {
        control = null;
        if (!node.Attributes.TryGetValue("id", out var id) || !id.TryGetString(out var value) ||
            !_prototypes.TryIndex<JobIconPrototype>(value, out var prototype))
            return false;

        control = new TextureRect
        {
            Texture = _systems.GetEntitySystem<SpriteSystem>().Frame0(prototype.Icon),
            SetWidth = 16,
            SetHeight = 16,
            Stretch = TextureRect.StretchMode.Scale,
        };
        return true;
    }
}
