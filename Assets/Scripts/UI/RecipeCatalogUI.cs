using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Read-only recipe browser that reuses an existing CraftingGridUI.
/// It creates a transient CraftingGrid at runtime and fills it with
/// the selected recipe's pattern. Provides Prev/Next navigation and
/// displays the output item and amount.
/// </summary>
public class RecipeCatalogUI : MonoBehaviour
{
    [Header("Source Recipes")]
    [SerializeField] private List<ShapedRecipe> recipes = new();

    // If a RecipeCatalogService exists, we prefer its runtime list and subscribe to changes.
    private RecipeCatalogService svc;

    [Header("Grid UI (reused)")]
    [SerializeField] private CraftingGridUI craftingGridUI;
    // If you want to reuse an existing CraftingGrid in the scene, leave this null.

    [Header("Output UI")]
    [SerializeField] private Image outputIcon;
    [SerializeField] private TextMeshProUGUI outputCountTMP;
    [SerializeField] private TextMeshProUGUI recipeIndexLabel;
    [Header("Strings")]
    [SerializeField] private string msgNoRecipes = "No recipes known yet.";

    [Header("Navigation")]
    [SerializeField] private Button prevButton;
    [SerializeField] private Button nextButton;

    private CraftingGrid runtimeGrid;
    private int currentIndex = 0;

    void Awake()
    {
        if (craftingGridUI == null) Debug.LogError("RecipeCatalogUI: craftingGridUI is not assigned.", this);

        if (prevButton) prevButton.onClick.AddListener(Prev);
        if (nextButton) nextButton.onClick.AddListener(Next);
        svc = RecipeCatalogService.Instance ?? FindObjectOfType<RecipeCatalogService>();
        if (svc != null) svc.OnRecipeAdded += OnServiceRecipeAdded;
    }

    void OnEnable()
    {
        EnsureRuntimeGrid();
        // Re-check service in case it was created after Awake (actors can create it at runtime)
        var found = RecipeCatalogService.Instance ?? FindObjectOfType<RecipeCatalogService>();
        if (found != svc)
        {
            if (svc != null) svc.OnRecipeAdded -= OnServiceRecipeAdded;
            svc = found;
            if (svc != null) svc.OnRecipeAdded += OnServiceRecipeAdded;
        }

        // If service present, populate our runtime list from it
        if (svc != null)
        {
            recipes.Clear();
            var all = svc.GetAll();
            if (all != null)
            {
                foreach (var r in all) if (r != null) recipes.Add(r);
            }
        }

        currentIndex = 0;
        ShowRecipe(currentIndex);
        UpdateNav();
    }

    void OnDisable()
    {
        if (craftingGridUI != null) craftingGridUI.grid = null;
        if (runtimeGrid != null) Destroy(runtimeGrid.gameObject);
        runtimeGrid = null;
        if (svc != null) svc.OnRecipeAdded -= OnServiceRecipeAdded;
    }

    void EnsureRuntimeGrid()
    {
        if (craftingGridUI == null) return;
        if (runtimeGrid != null) return;

        var go = new GameObject("_RecipeCatalogGrid", typeof(RectTransform));
        go.hideFlags = HideFlags.DontSaveInBuild | HideFlags.DontSaveInEditor;
        go.transform.SetParent(transform, false);
        runtimeGrid = go.AddComponent<CraftingGrid>();

        // assign the transient grid to the visible UI so it instantiates slots
        craftingGridUI.grid = runtimeGrid;
        // force a build/refresh
        craftingGridUI.Build();

        // Make the instantiated slot visuals non-interactive (read-only)
        var slots = go.GetComponentsInChildren<ItemSlotUI>(true);
        if (slots == null || slots.Length == 0)
        {
            // CraftingGridUI instantiates slots under its configured slotParent;
            // find them there instead.
            var parent = craftingGridUI.slotParent;
            if (parent != null)
                slots = parent.GetComponentsInChildren<ItemSlotUI>(true);
        }

        if (slots != null)
        {
            foreach (var s in slots)
            {
                // Make them hoverable for tooltips but locked for interaction/drag
                s.SetLocked(true);
                var cg = s.GetComponent<CanvasGroup>();
                if (cg == null) cg = s.gameObject.AddComponent<CanvasGroup>();
                cg.blocksRaycasts = true;

                if (s.GetComponent<SlotHoverTooltip>() == null)
                    s.gameObject.AddComponent<SlotHoverTooltip>();

                var juice = s.GetComponent<ButtonJuice>();
                if (juice != null) Destroy(juice);
            }
        }
    }

    public void Prev()
    {
        if (recipes == null || recipes.Count == 0) return;
        currentIndex = (currentIndex - 1 + recipes.Count) % recipes.Count;
        ShowRecipe(currentIndex);
        UpdateNav();
    }

    public void Next()
    {
        if (recipes == null || recipes.Count == 0) return;
        currentIndex = (currentIndex + 1) % recipes.Count;
        ShowRecipe(currentIndex);
        UpdateNav();
    }

    public void ShowRecipe(int index)
    {
        if (craftingGridUI == null) return;
        if (recipes == null || recipes.Count == 0)
        {
            ClearGrid();
            UpdateOutput(null, 0);
            if (recipeIndexLabel != null) recipeIndexLabel.text = msgNoRecipes;
            return;
        }
        index = Mathf.Clamp(index, 0, recipes.Count - 1);
        currentIndex = index;

        var r = recipes[index];
        if (r == null)
        {
            ClearGrid();
            UpdateOutput(null, 0);
            UpdateIndexLabel();
            return;
        }

        EnsureRuntimeGrid();

        // Fill the transient grid's cells according to the recipe pattern
        for (int i = 0; i < CraftingGrid.Width * CraftingGrid.Height; i++)
        {
            var cell = runtimeGrid.GetCell(i);
            var ing = (i >= 0 && i < r.pattern.Length) ? r.pattern[i] : default;
            if (ing.item != null)
            {
                cell.Item = ing.item;
                cell.Amount = Mathf.Max(1, ing.amount);
            }
            else
            {
                cell.Clear();
            }
        }

        runtimeGrid.RaiseChanged();

        UpdateOutput(r.outputItem, r.outputAmount);

        // Show the recipe's display name (fall back to asset name)
        if (recipeIndexLabel != null)
        {
            var title = (r != null && !string.IsNullOrEmpty(r.displayName)) ? r.displayName : (r != null ? r.name : "");
            recipeIndexLabel.text = title;
        }
    }

    void ClearGrid()
    {
        if (runtimeGrid == null) return;
        runtimeGrid.Clear();
        runtimeGrid.RaiseChanged();
    }

    void UpdateOutput(Item item, int amount)
    {
        if (outputIcon != null)
        {
            outputIcon.sprite = item ? item.Icon : null;
            outputIcon.enabled = item != null && outputIcon.sprite != null;
        }
        if (outputCountTMP != null)
            outputCountTMP.text = (item != null && amount >= 1) ? amount.ToString() : "";
    }

    void UpdateIndexLabel()
    {
        // Deprecated: label now shows recipe display name. Keep method to avoid call sites.
        if (recipeIndexLabel != null)
        {
            if (recipes != null && recipes.Count > 0 && currentIndex >= 0 && currentIndex < recipes.Count)
            {
                var r = recipes[currentIndex];
                recipeIndexLabel.text = (r != null && !string.IsNullOrEmpty(r.displayName)) ? r.displayName : (r != null ? r.name : "");
            }
            else recipeIndexLabel.text = "";
        }
    }

    void OnServiceRecipeAdded(ShapedRecipe r)
    {
        if (r == null) return;
        if (recipes == null) recipes = new List<ShapedRecipe>();
        if (!recipes.Contains(r)) recipes.Add(r);
        currentIndex = recipes.IndexOf(r);
        ShowRecipe(currentIndex);
        UpdateNav();
    }

    void UpdateNav()
    {
        int count = recipes != null ? recipes.Count : 0;
        bool enableNav = count > 1;
        if (prevButton)
        {
            prevButton.interactable = enableNav;
            var cg = prevButton.GetComponent<CanvasGroup>();
            if (cg) cg.alpha = enableNav ? 1f : 0.5f;
        }
        if (nextButton)
        {
            nextButton.interactable = enableNav;
            var cg = nextButton.GetComponent<CanvasGroup>();
            if (cg) cg.alpha = enableNav ? 1f : 0.5f;
        }
    }
}
