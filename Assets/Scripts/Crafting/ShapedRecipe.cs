using UnityEngine;
using System;

[System.Serializable]
public struct Ingredient
{
    public Item item;
    [Min(1)] public int amount;
}

[CreateAssetMenu(menuName = "Crafting/Shaped Recipe (4x4)")]
public class ShapedRecipe : ScriptableObject
{
    public const int PatternSize = 16;

    [Header("Metadata")]
    [Tooltip("Optional display name shown in UIs. If empty, the asset name will be used.")]
    public string displayName;
    [Min(0)]
    [Tooltip("Progression tier required/unlocked for this recipe.")]
    public int tier = 0;
    [Tooltip("Muss 16 Elemente (4x4) enthalten. Leere Zellen = kein Item verlangt.")]
    public Ingredient[] pattern = new Ingredient[PatternSize];

    [Header("Output")]
    public Item outputItem;
    [Min(1)] public int outputAmount = 1;

    void OnValidate()
    {
        if (pattern == null)
        {
            pattern = new Ingredient[PatternSize];
        }
        else if (pattern.Length != PatternSize)
        {
            Array.Resize(ref pattern, PatternSize);
        }

        // Normalize potentially broken legacy data:
        // - item set with amount <= 0 should still require at least 1.
        // - empty item should always have amount 0.
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i].item == null)
            {
                pattern[i].amount = 0;
            }
            else if (pattern[i].amount <= 0)
            {
                pattern[i].amount = 1;
            }
        }
    }
}