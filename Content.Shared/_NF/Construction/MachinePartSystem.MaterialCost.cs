// SPDX-FileCopyrightText: 2026 OfflcerSam
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq;
using Content.Shared.Materials;
using Robust.Shared.Prototypes;

namespace Content.Shared.Construction;

public sealed partial class MachinePartSystem
{
    /// <summary>Uses the crafting cost of a stock part, falling back to its composition.</summary>
    private void AddPartMaterialCost(EntProtoId partPrototypeId, int partCount, int coefficient,
        Dictionary<string, int> materialCosts)
    {
        if (_lathe.TryGetRecipesFromEntity(partPrototypeId, out var recipes) && recipes.Count > 0)
        {
            var partRecipe = recipes.MinBy(recipe => recipe.Materials.Values.Sum())!;
            foreach (var (materialId, materialAmount) in partRecipe.Materials)
            {
                materialCosts.TryAdd(materialId, 0);
                materialCosts[materialId] += materialAmount * partCount * coefficient;
            }
        }
        else if (_prototype.TryIndex(partPrototypeId, out var partPrototype) &&
                 partPrototype.TryGetComponent<PhysicalCompositionComponent>(out var composition, Factory))
        {
            foreach (var (materialId, materialAmount) in composition.MaterialComposition)
            {
                materialCosts.TryAdd(materialId, 0);
                materialCosts[materialId] += materialAmount * partCount * coefficient;
            }
        }
    }
}
