using UnityEngine;
using System.Collections.Generic;

public class ActorManager : MonoBehaviour
{
    const float IdleThoughtStartJitterMin = 0.5f;
    const float IdleThoughtStartJitterMax = 4f;
    const string RoleWizardId = "lieferant";
    const string RoleCrafterId = "chefin";

    [Header("Limits")]
    public int maxSlots = 3;
    public List<ActorInstance> actors = new();

    [Header("Refs")]
    public GameCalendar calendar;
    public ResourceStore store;
    public GlobalInventoryService globalInventoryService;
    public PlayerProgress playerProgress;
    public TreeState skillTree;

    Inventory PlayerInventory => globalInventoryService != null ? globalInventoryService.playerInventory : null;

    [Header("Resources")]
    public ResourceDefinition goldResource;
    public ResourceDefinition spiritResource;
    [Header("Actor Progression")]
    public int xpPerTask = 2;
    [Tooltip("XP gained per hire (join).")] public int xpPerHire = 0;

    [Header("Randomness")]
    public int seed = 1234;
    System.Random rng;

    [Header("Log Strings")]
    public string standingByText = "Standing by.";

    void EnsureRng()
    {
        if (rng == null)
            rng = new System.Random(seed);
    }

    void Awake() { rng = new System.Random(seed); }
    void OnEnable()
    {
        if (playerProgress == null) playerProgress = FindObjectOfType<PlayerProgress>();
        if (calendar) calendar.OnDayChanged += OnDay;
        foreach (var a in actors)
        {
            a?.EnsureBackpackCapacity();
            a?.EnsureEquipmentSlots();
        }
    }
    void OnDisable() { if (calendar) calendar.OnDayChanged -= OnDay; }
    void OnDestroy()
    {
        foreach (var a in actors)
            a?.Dispose();
    }

    void Update() => TickIdleThoughts();

    public bool TryAddActor(ActorDefinition def)
    {
        if (actors.Count >= maxSlots) return false;
        if (def != null && def.hireGoldCost > 0 && goldResource != null && store != null)
        {
            if (!store.TrySpend(goldResource, def.hireGoldCost))
            {
                Debug.Log($"[ActorManager] Not enough gold to hire {def.displayName} (cost {def.hireGoldCost}).");
                return false;
            }
        }
        actors.Add(new ActorInstance(def));
        return true;
    }

    public void AssignForage(ActorInstance a, ForageArea area, bool repeat = false)
    {
        if (a == null || area == null || !(a.Role && a.Role.canForage)) return;
        if (a.state != ActorState.Idle) return;
        a.taskType = "forage"; a.taskAsset = area; a.repeatTask = repeat;
        a.state = ActorState.Traveling;
        a.remainingDays = area.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
        a.lastHaulSummary = "nothing";
        a.AddStatement(Stamp($"Heading to {area.displayName}."));
    }

    public void AssignSell(ActorInstance a, SellRoute route, bool repeat = false)
    {
        if (a == null || route == null || !(a.Role && a.Role.canSell)) return;
        if (a.state != ActorState.Idle) return;
        a.taskType = "sell"; a.taskAsset = route; a.repeatTask = repeat;
        a.state = ActorState.Traveling;
        a.remainingDays = route.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
        a.AddStatement(Stamp($"Traveling along {route.displayName} to sell goods."));
    }

    public void AssignResearch(ActorInstance a, ResearchDomain domain, bool repeat = false)
    {
        if (a == null || domain == null || !(a.Role && a.Role.canResearch)) return;
        if (a.state != ActorState.Idle) return;
        a.taskType = "research"; a.taskAsset = domain; a.repeatTask = repeat;
        a.state = ActorState.Working;
        a.remainingDays = domain.days / (a.Role.efficiency * GetResearchSpeedMultiplier(a));
        a.AddStatement(Stamp($"Studying {domain.displayName}."));
    }

    public void AssignCraft(ActorInstance a, CraftPlan plan, bool repeat = true)
    {
        if (a == null || plan == null || !(a.Role && a.Role.canCraft)) return;
        if (a.state != ActorState.Idle) return;
        a.taskType = "craft"; a.taskAsset = plan; a.repeatTask = repeat;
        a.state = ActorState.Working;
        a.remainingDays = plan.daysPerCraft / (a.Role.efficiency * GetCraftSpeedMultiplier(a));
        a.AddStatement(Stamp($"Working on {plan.displayName}."));
    }

    public void AssignTavernVisit(ActorInstance a, TavernVisit visit, bool repeat = false)
    {
        if (a == null || visit == null || !(a.Role && a.Role.canVisitTavern)) return;
        if (a.state != ActorState.Idle) return;
        a.taskType = "tavern"; a.taskAsset = visit; a.repeatTask = repeat;
        a.state = ActorState.Working;
        a.remainingDays = visit.visitDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
        a.AddStatement(Stamp(visit.msgHeadingToTavern));
    }

    void OnDay()
    {
        EnsureRng();
        // First: consume spirit cost for each actor
        foreach (var a in actors)
        {
            if (a == null) continue;
            int cost = a.def != null ? a.def.spiritPerDay : 0;
            if (cost > 0 && store != null && spiritResource != null)
            {
                bool paid = store.TrySpend(spiritResource, cost);
                if (!paid)
                {
                    a.state = ActorState.Paused;
                    a.AddStatement(Stamp($"Paused: not enough spirit ({cost}/day needed)."));
                    continue; // skip decrement/processing for this actor
                }
            }
        }

        foreach (var a in actors)
        {
            if (a == null) continue;
            if (a.state == ActorState.Idle || a.state == ActorState.Paused) continue;

            a.remainingDays -= 1f;
            if (a.remainingDays > 0f) continue;

            switch (a.taskType)
            {
            case "forage": TickForage(a); break;
            case "sell": TickSell(a); break;
            case "research": TickResearch(a); break;
            case "craft": TickCraft(a); break;
            case "tavern":
                if (a.state == ActorState.Returning) TickTavernReturn(a);
                else TickTavern(a);
                break;
        }
    }
    }

    void TickForage(ActorInstance a)
    {
        EnsureRng();
        var area = a.taskAsset as ForageArea;
        if (area == null) { a.state = ActorState.Idle; return; }

        if (a.state == ActorState.Traveling)
        {
            a.state = ActorState.Working;
            a.remainingDays = area.workDays / (a.Role.efficiency * GetForageSpeedMultiplier(a));
            a.AddStatement(Stamp($"Started foraging in {area.displayName}."));
            return;
        }

        if (a.state == ActorState.Working)
        {
            float seasonMul = 1f;
            int si = calendar ? calendar.SeasonIndex : 0;
            if (area.seasonYieldMul != null && si < area.seasonYieldMul.Length)
                seasonMul = area.seasonYieldMul[si];

            bool foundSomething = false;
            var lootEntries = new System.Text.StringBuilder();
            if (area.drops != null)
            {
                foreach (var d in area.drops)
                {
                    if (!d.item || d.max <= 0) continue;
                    if (!IsItemTierAllowed(a, d.item)) continue;
                    float probability = d.dropChance <= 0f ? 1f : Mathf.Clamp01(d.dropChance * (1f + GetForageLuckBonus(a)));
                    if (rng.NextDouble() > probability) continue;
                    int amount = area.RollAmount(rng, seasonMul, d);
                    if (amount > 0)
                    {
                        amount = ApplyForageSkill(amount);
                        TryBackpackAdd(a, d.item, amount);
                        if (foundSomething) lootEntries.Append(", ");
                        lootEntries.Append($"{amount}x {d.item.DisplayName}");
                        foundSomething = true;
                    }
                }
            }
            a.lastHaulSummary = foundSomething ? lootEntries.ToString() : "nothing";
            a.AddStatement(Stamp(foundSomething
                ? $"Collected {a.lastHaulSummary} in {area.displayName}."
                : $"Found nothing in {area.displayName}."));
            ApplyRoleHappiness(a, "forage", foundSomething);
            GrantActorXP(a, xpPerTask);
            MaybeAddThought(a);
            playerProgress?.GrantXP(5);

            if (area.roundTrip)
            {
                a.state = ActorState.Returning;
                a.remainingDays = area.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
                a.AddStatement(Stamp(foundSomething
                    ? $"Heading back from {area.displayName} with {a.lastHaulSummary}."
                : $"Heading back from {area.displayName} empty-handed."));
            }
            else FinishOrRepeat(a);
            return;
        }

        if (a.state == ActorState.Returning)
        {
            DumpBackpackToInventory(a);
            a.AddStatement(Stamp($"Delivered {a.lastHaulSummary} to base."));
            FinishOrRepeat(a);
        }
    }

    void TickSell(ActorInstance a)
    {
        var route = a.taskAsset as SellRoute;
        if (route == null) { a.state = ActorState.Idle; return; }

        if (a.state == ActorState.Traveling)
        {
            a.state = ActorState.Working;
            a.remainingDays = route.marketDays / a.Role.efficiency;
            a.AddStatement(Stamp($"Arrived at {route.displayName} market."));
            return;
        }

        if (a.state == ActorState.Working)
        {
            int goldEarned = 0;
            var slots = a.backpack.Slots;
            for (int i = 0; i < slots.Count; i++)
            {
                var st = slots[i];
                if (st == null || st.IsEmpty) continue;
                int price = route.GetPrice(st.Item);
                goldEarned += price * st.Amount;
                st.Clear();
            }
            a.backpack.RaiseChanged();

            a.lastHaulSummary = goldEarned > 0 ? $"{goldEarned} gold" : "no gold";
            a.AddStatement(Stamp(goldEarned > 0
                ? $"Sold goods for {goldEarned} gold on {route.displayName}."
                : $"Nothing to sell on {route.displayName}."));
            GrantActorXP(a, xpPerTask);
            MaybeAddThought(a);
            playerProgress?.GrantXP(3);

            a.state = ActorState.Returning;
            a.remainingDays = route.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
            a.AddStatement(Stamp($"Heading back from {route.displayName} with {a.lastHaulSummary}."));
        }
        else if (a.state == ActorState.Returning)
        {
            if (goldResource && store && a.lastHaulSummary != "no gold")
            {
                if (int.TryParse(a.lastHaulSummary.Split(' ')[0], out int amount) && amount > 0)
                    store.Add(goldResource, amount);
            }
            a.AddStatement(Stamp("Delivered earnings to base."));
            FinishOrRepeat(a);
        }
    }

    void TickResearch(ActorInstance a)
    {
        EnsureRng();
        var domain = a.taskAsset as ResearchDomain;
        if (domain == null) { a.state = ActorState.Idle; return; }

        float researchLuck = GetResearchLuckBonus(a);
        bool gotRecipe = rng.NextDouble() < Mathf.Clamp01(domain.recipeChance * (1f + researchLuck));
        if (gotRecipe && domain.possibleRecipes != null && domain.possibleRecipes.Length > 0)
        {
            var eligible = new System.Collections.Generic.List<ShapedRecipe>();
            foreach (var r in domain.possibleRecipes)
            {
                if (r != null && IsRecipeTierAllowed(a, r))
                    eligible.Add(r);
            }

            if (eligible.Count > 0)
            {
                var r = eligible[rng.Next(eligible.Count)];
                if (r)
                {
                    UnlockRecipe(r);
                    a.AddStatement(Stamp($"Discovered recipe {r.name}."));
                }
            }
            else
            {
                gotRecipe = false;
            }
        }

        bool gotSpiritGen = rng.NextDouble() < Mathf.Clamp01(domain.spiritGenChance * (1f + researchLuck));
        if (gotSpiritGen && domain.possibleSpiritGenerators != null && domain.possibleSpiritGenerators.Length > 0)
        {
            var p = domain.possibleSpiritGenerators[rng.Next(domain.possibleSpiritGenerators.Length)];
            if (p)
            {
                UnlockProducer(p);
                GiveProducerToActor(a, p);
                a.AddStatement(Stamp($"Uncovered spirit producer {p.displayName}"));
            }
        }

        if (!gotRecipe && !gotSpiritGen)
            a.AddStatement(Stamp($"Research in {domain.displayName} yielded no discoveries."));

        GrantActorXP(a, xpPerTask);
        ApplyRoleHappiness(a, "research", true);
        MaybeAddThought(a);
        playerProgress?.GrantXP(6);
        FinishOrRepeat(a);
    }

    void TickCraft(ActorInstance a)
    {
        var plan = a.taskAsset as CraftPlan;
        if (plan == null) { a.state = ActorState.Idle; return; }

        if (plan.spiritPerCraft > 0)
        {
            if (!spiritResource || !store || store.Get(spiritResource) < plan.spiritPerCraft)
            {
                a.state = ActorState.Paused;
                ToastSystem.Warning($"{a.def.displayName} paused", "Not enough Spirit to craft.");
                a.AddStatement(Stamp($"Can't craft {plan.displayName}: not enough spirit."));
                return;
            }
            store.Add(spiritResource, -plan.spiritPerCraft);
        }

        bool ok = TryAutocraftOnce(a, plan);
        if (ok)
        {
            playerProgress?.GrantXP(2);
            a.AddStatement(Stamp($"Finished crafting step for {plan.displayName}."));
            GrantActorXP(a, xpPerTask);
            ApplyRoleHappiness(a, "craft", true);
            MaybeAddThought(a);
        }

        a.state = ActorState.Working;
        a.remainingDays = plan.daysPerCraft / (a.Role.efficiency * GetCraftSpeedMultiplier(a));

        if (!ok)
        {
            a.state = ActorState.Paused;
            ToastSystem.Info($"{a.def.displayName}", "Waiting for resources.");
            a.AddStatement(Stamp($"Can't craft {plan.displayName}: missing materials."));
        }
    }

        void FinishOrRepeat(ActorInstance a)
        {
        if (a.repeatTask)
        {
            switch (a.taskType)
            {
                case "forage":
                    var area = (ForageArea)a.taskAsset;
                    a.state = ActorState.Traveling;
                    a.remainingDays = area.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
                    a.AddStatement(Stamp($"Looping forage route {area.displayName}."));
                    break;
                case "sell":
                    var route = (SellRoute)a.taskAsset;
                    a.state = ActorState.Traveling;
                    a.remainingDays = route.travelDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
                    a.AddStatement(Stamp($"Heading out again on {route.displayName}."));
                    break;
                case "research":
                    var rd = (ResearchDomain)a.taskAsset;
                    a.state = ActorState.Working;
                    a.remainingDays = rd.days / (a.Role.efficiency * GetResearchSpeedMultiplier(a));
                    a.AddStatement(Stamp($"Continuing research in {rd.displayName}."));
                    break;
                case "craft":
                    var cp = (CraftPlan)a.taskAsset;
                    a.state = ActorState.Working;
                    a.remainingDays = cp.daysPerCraft / (a.Role.efficiency * GetCraftSpeedMultiplier(a));
                    a.AddStatement(Stamp($"Continuing {cp.displayName}."));
                    break;
                case "tavern":
                    var tv = (TavernVisit)a.taskAsset;
                    a.state = ActorState.Working;
                    a.remainingDays = tv.visitDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
                    a.AddStatement(Stamp(tv.msgHeadingToTavern));
                    break;
            }
        }
        else
        {
            a.state = ActorState.Idle;
            a.taskType = null;
            a.taskAsset = null;
            a.remainingDays = 0f;
            a.AddStatement(Stamp(standingByText));
        }
    }

    void TickTavern(ActorInstance a)
    {
        EnsureRng();
        var visit = a.taskAsset as TavernVisit;
        if (visit == null) { a.state = ActorState.Idle; return; }

        if (a.state == ActorState.Returning)
        {
            TickTavernReturn(a);
            return;
        }

        if (goldResource != null && store != null && visit.goldCost > 0)
        {
            if (!store.TrySpend(goldResource, visit.goldCost))
            {
                a.state = ActorState.Paused;
                string msg = string.Format(visit.msgNotEnoughGold, visit.goldCost);
                ToastSystem.Info(a.def?.displayName ?? "Actor", msg);
                a.AddStatement(Stamp(msg));
                return;
            }
        }

        float tavernLuck = GetTavernLuckBonus(a);
        bool anyDiscovery = false;
        if (visit.forageLeads != null && rng.NextDouble() < Mathf.Clamp01(visit.chanceForageLead * (1f + tavernLuck)))
            anyDiscovery |= TryUnlockArea(a, visit.forageLeads, visit.msgFoundForage);
        if (visit.sellLeads != null && rng.NextDouble() < Mathf.Clamp01(visit.chanceSellLead * (1f + tavernLuck)))
            anyDiscovery |= TryUnlockRoute(a, visit.sellLeads, visit.msgFoundSell);
        if (visit.researchLeads != null && rng.NextDouble() < Mathf.Clamp01(visit.chanceResearchLead * (1f + tavernLuck)))
            anyDiscovery |= TryUnlockDomain(a, visit.researchLeads, visit.msgFoundResearch);

        if (visit.actorOffers != null && visit.actorOffers.Count > 0 && rng.NextDouble() < Mathf.Clamp01(visit.chanceNewActorOffer * (1f + tavernLuck)))
        {
            var offer = PickRandom(visit.actorOffers);
            ShowActorOffer(offer, visit);
        }

        if (!anyDiscovery)
            a.AddStatement(Stamp(visit.msgNoDiscovery));
        GrantActorXP(a, xpPerTask);
        ApplyRoleHappiness(a, "tavern", true);
        MaybeAddThought(a);

        a.state = ActorState.Returning;
        a.remainingDays = visit.visitDays / (a.Role.efficiency * GetTravelSpeedMultiplier(a));
        a.AddStatement(Stamp($"Heading back from {visit.displayName}."));
    }

    void TickTavernReturn(ActorInstance a)
    {
        if (a == null) return;
        FinishOrRepeat(a);
    }

    bool TryAutocraftOnce(ActorInstance a, CraftPlan plan)
    {
        var recipe = plan.recipe;
        if (recipe == null) return false;

        if (!IsRecipeTierAllowed(a, recipe))
        {
            a.AddStatement(Stamp($"Cannot craft {recipe.name}: requires level {RequiredLevelForTier(recipe.tier)} (current {a?.level})."));
            return false;
        }

        if (recipe.outputItem != null && !IsItemTierAllowed(a, recipe.outputItem))
        {
            a.AddStatement(Stamp($"Cannot craft {recipe.name}: output {recipe.outputItem.DisplayName} requires level {RequiredLevelForTier(recipe.outputItem.tier)} (current {a?.level})."));
            return false;
        }

        var catalog = RecipeCatalogService.Instance ?? FindObjectOfType<RecipeCatalogService>();
        if (catalog != null && !catalog.HasRecipe(recipe))
        {
            a.AddStatement(Stamp($"Cannot craft {recipe.name}: recipe is not known yet."));
            return false;
        }

        if (!ConsumeInputs(a, recipe, plan.takeInputsFromBackpackFirst)) return false;
        ProduceOutputs(a, recipe, plan.putOutputsIntoBackpack);
        return true;
    }

    bool ConsumeInputs(ActorInstance a, ShapedRecipe recipe, bool fromBackpackFirst)
    {
        var needs = BuildRecipeNeeds(recipe);
        if (needs.Count == 0) return false;

        foreach (var kv in needs)
        {
            if (!IsItemTierAllowed(a, kv.Key))
            {
                a.AddStatement(Stamp($"Cannot use {kv.Key.DisplayName}: requires level {RequiredLevelForTier(kv.Key.tier)} (current {a?.level})."));
                return false;
            }

            int total = 0;
            var globalInv = PlayerInventory;
            if (fromBackpackFirst)
            {
                if (a.backpack != null) total += a.backpack.GetTotalAmount(kv.Key);
                if (globalInv != null) total += globalInv.GetTotalAmount(kv.Key);
            }
            else
            {
                if (globalInv != null) total += globalInv.GetTotalAmount(kv.Key);
                if (a.backpack != null) total += a.backpack.GetTotalAmount(kv.Key);
            }
            if (total < kv.Value) return false;
        }

        foreach (var kv in needs)
        {
            int remaining = kv.Value;
            var globalInv = PlayerInventory;
            if (fromBackpackFirst)
            {
                if (a.backpack != null) remaining -= a.backpack.RemoveUpTo(kv.Key, remaining);
                if (remaining > 0 && globalInv != null) remaining -= globalInv.RemoveUpTo(kv.Key, remaining);
            }
            else
            {
                if (globalInv != null) remaining -= globalInv.RemoveUpTo(kv.Key, remaining);
                if (remaining > 0 && a.backpack != null) remaining -= a.backpack.RemoveUpTo(kv.Key, remaining);
            }
            if (remaining > 0) return false;
        }
        return true;
    }

    void ProduceOutputs(ActorInstance a, ShapedRecipe recipe, bool intoBackpack)
    {
        if (!recipe || !recipe.outputItem) return;
        int remaining = recipe.outputAmount;

        var globalInv = PlayerInventory;
        if (intoBackpack && a.backpack != null)
        {
            remaining = a.backpack.Add(recipe.outputItem, remaining);
        }

        if (remaining > 0 && globalInv != null)
        {
            remaining = globalInv.Add(recipe.outputItem, remaining);
        }

        if (remaining > 0)
        {
            ToastSystem.Warning("Inventory full", $"{recipe.outputItem.DisplayName} overflowed.");
        }
    }

    public void DumpBackpackToInventory(ActorInstance a)
    {
        var globalInv = PlayerInventory;
        if (a?.backpack == null || globalInv == null) return;
        var slots = a.backpack.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            var st = slots[i];
            if (st == null || st.IsEmpty) continue;
            int leftover = globalInv.Add(st.Item, st.Amount);
            if (leftover <= 0) st.Clear();
            else st.Amount = leftover;
        }
        a.backpack.RaiseChanged();
    }

    void TryBackpackAdd(ActorInstance a, Item item, int amount)
    {
        if (a?.backpack == null || !item || amount <= 0) return;
        int leftover = a.backpack.Add(item, amount);
        var globalInv = PlayerInventory;
        if (leftover > 0 && globalInv != null)
        {
            leftover = globalInv.Add(item, leftover);
        }
        if (leftover > 0)
        {
            ToastSystem.Warning("Backpack overflow", $"{item.DisplayName} dropped on ground.");
        }
    }

    Dictionary<Item, int> BuildRecipeNeeds(ShapedRecipe recipe)
    {
        var needs = new Dictionary<Item, int>();
        if (!recipe || recipe.pattern == null) return needs;
        foreach (var ing in recipe.pattern)
        {
            if (ing.item == null || ing.amount <= 0) continue;
            if (needs.ContainsKey(ing.item)) needs[ing.item] += ing.amount;
            else needs[ing.item] = ing.amount;
        }
        return needs;
    }

    int RequiredLevelForTier(int tier) => Mathf.Max(1, tier);

    bool IsItemTierAllowed(ActorInstance a, Item item)
    {
        if (a == null || item == null) return false;
        return a.level >= RequiredLevelForTier(item.tier);
    }

    bool IsRecipeTierAllowed(ActorInstance a, ShapedRecipe recipe)
    {
        if (a == null || recipe == null) return false;
        return a.level >= RequiredLevelForTier(recipe.tier);
    }

    bool TryUnlockArea(ActorInstance a, List<ForageArea> pool, string messageFormat)
    {
        var target = PickNew(pool, GetAllForageAreas());
        if (target == null) return false;
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.forageAreas == null) continue;
            if (!ui.forageAreas.Contains(target)) ui.forageAreas.Add(target);
        }
        a.AddStatement(Stamp(string.Format(messageFormat, target.displayName)));
        ToastSystem.Success("New forage area", target.displayName);
        return true;
    }

    bool TryUnlockRoute(ActorInstance a, List<SellRoute> pool, string messageFormat)
    {
        var target = PickNew(pool, GetAllSellRoutes());
        if (target == null) return false;
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.sellRoutes == null) continue;
            if (!ui.sellRoutes.Contains(target)) ui.sellRoutes.Add(target);
        }
        a.AddStatement(Stamp(string.Format(messageFormat, target.displayName)));
        ToastSystem.Success("New sell route", target.displayName);
        return true;
    }

    bool TryUnlockDomain(ActorInstance a, List<ResearchDomain> pool, string messageFormat)
    {
        var target = PickNew(pool, GetAllResearchDomains());
        if (target == null) return false;
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.researchDomains == null) continue;
            if (!ui.researchDomains.Contains(target)) ui.researchDomains.Add(target);
        }
        a.AddStatement(Stamp(string.Format(messageFormat, target.displayName)));
        ToastSystem.Success("New research domain", target.displayName);
        return true;
    }

    T PickNew<T>(List<T> pool, List<T> already) where T : ScriptableObject
    {
        if (pool == null || pool.Count == 0) return null;
        var candidates = new List<T>();
        foreach (var p in pool)
        {
            if (p == null) continue;
            if (already != null && already.Contains(p)) continue;
            candidates.Add(p);
        }
        if (candidates.Count == 0) return null;
        int idx = rng.Next(0, candidates.Count);
        return candidates[idx];
    }

    List<ForageArea> GetAllForageAreas()
    {
        var list = new List<ForageArea>();
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.forageAreas == null) continue;
            list.AddRange(ui.forageAreas);
        }
        return list;
    }

    List<SellRoute> GetAllSellRoutes()
    {
        var list = new List<SellRoute>();
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.sellRoutes == null) continue;
            list.AddRange(ui.sellRoutes);
        }
        return list;
    }

    List<ResearchDomain> GetAllResearchDomains()
    {
        var list = new List<ResearchDomain>();
        foreach (var ui in FindObjectsOfType<ActorsPanelUI>())
        {
            if (ui == null || ui.researchDomains == null) continue;
            list.AddRange(ui.researchDomains);
        }
        return list;
    }

    void ShowActorOffer(ActorDefinition def, TavernVisit tavern)
    {
        if (def == null || tavern == null) return;
        int cost = def.hireGoldCost;
        int availableGold = (goldResource != null && store != null) ? store.Get(goldResource) : 0;
        bool canAfford = cost <= 0 || availableGold >= cost;
        string costLine = cost > 0
            ? string.Format(tavern.msgActorHireCost, cost, availableGold)
            : tavern.msgActorHireCostFree;
        string body = $"{string.Format(tavern.msgActorOfferBody, def.displayName)}\n{costLine}";
        OptionPopup.ShowWithBody(tavern.msgActorOfferTitle,
            body,
            new List<string> { tavern.msgActorOfferAccept, tavern.msgActorOfferDecline },
            s => s,
            idx =>
            {
                if (idx == 0)
                {
                    bool added = TryAddActor(def);
                    if (added)
                    {
                        ToastSystem.Success(tavern.msgActorOfferTitle, string.Format(tavern.msgActorHired, def.displayName));
                        RefreshAllPanels();
                    }
                    else
                    {
                        ToastSystem.Warning(tavern.msgActorOfferTitle, tavern.msgRosterFull);
                    }
                }
                else
                {
                    ToastSystem.Info(tavern.msgActorOfferTitle, tavern.msgActorDeclined);
                }
            },
            idx => idx != 0 || canAfford);
    }

    void RefreshAllPanels()
    {
        var panels = FindObjectsOfType<ActorsPanelUI>(true);
        foreach (var p in panels)
        {
            if (p != null && p.isActiveAndEnabled)
                p.Rebuild();
        }
    }

    T PickRandom<T>(List<T> list)
    {
        if (list == null || list.Count == 0) return default;
        int idx = rng.Next(0, list.Count);
        return list[idx];
    }

    void GrantActorXP(ActorInstance a, int amount)
    {
        if (a == null || amount <= 0) return;
        bool leveled;
        bool gained = a.GainXP(amount, out leveled);
        if (gained && leveled)
        {
            a.AddStatement(Stamp($"{a.def?.displayName ?? "Actor"} reached level {a.level}!"));
        }
    }

    void ApplyRoleHappiness(ActorInstance a, string task, bool success)
    {
        if (a == null || a.Role == null || !success) return;
        string roleId = a.Role.id;
        float gain = 0f;

        if (roleId == RoleWizardId)
        {
            if (task == "research" || task == "forage") gain = 1f;
        }
        else if (roleId == RoleCrafterId)
        {
            if (task == "craft") gain = 1f;
            else if (task == "tavern") gain = 0.75f;
        }

        if (gain > 0f) a.AddHappinessProgress(gain);
    }

    void MaybeAddThought(ActorInstance a)
    {
        if (a == null || a.def == null || rng == null) return;
        string thought = null;
        string task = a.taskType;

        float clumsyChance = Mathf.Clamp01(a.def.clumsiness);
        float happyChance = a.EffectiveHappiness;

        // Clumsy thought
        if (clumsyChance > 0f && a.def.clumsyThoughts != null && a.def.clumsyThoughts.Length > 0)
        {
            if (rng.NextDouble() < clumsyChance)
                thought = PickRandom(new List<string>(PickTaskSpecific(a.def.clumsyThoughts,
                    task,
                    a.def.clumsyIdleThoughts,
                    a.def.clumsyTravelThoughts,
                    a.def.clumsyForageThoughts,
                    a.def.clumsySellThoughts,
                    a.def.clumsyResearchThoughts,
                    a.def.clumsyCraftThoughts,
                    a.def.clumsyTavernThoughts)));
        }

        // Happy thought (only if none chosen yet)
        if (thought == null && happyChance > 0f && a.def.happinessThoughts != null && a.def.happinessThoughts.Length > 0)
        {
            if (rng.NextDouble() < happyChance)
                thought = PickRandom(new List<string>(PickTaskSpecific(a.def.happinessThoughts,
                    task,
                    a.def.happyIdleThoughts,
                    a.def.happyTravelThoughts,
                    a.def.happyForageThoughts,
                    a.def.happySellThoughts,
                    a.def.happyResearchThoughts,
                    a.def.happyCraftThoughts,
                    a.def.happyTavernThoughts)));
        }

        if (!string.IsNullOrWhiteSpace(thought))
            a.AddThought(Stamp(thought));
    }

    void TickIdleThoughts()
    {
        EnsureRng();
        float now = Time.time;
        foreach (var a in actors)
        {
            if (a == null) continue;

            if (a.state != ActorState.Idle)
            {
                a.nextIdleThoughtTime = -1f;
                continue;
            }

            if (a.ThoughtActive) continue;

            if (a.nextIdleThoughtTime <= 0f)
            {
                GetIdleThoughtRange(a, out float minInterval, out float maxInterval);
                float jitter = Mathf.Lerp(minInterval, maxInterval, (float)rng.NextDouble());
                a.nextIdleThoughtTime = now + jitter;
            }

            if (now < a.nextIdleThoughtTime) continue;

            string thought = PickIdleThought(a);
            if (!string.IsNullOrWhiteSpace(thought))
                a.AddThought(Stamp(thought));

            GetIdleThoughtRange(a, out float min, out float max);
            float interval = Mathf.Lerp(min, max, (float)rng.NextDouble());
            a.nextIdleThoughtTime = now + Mathf.Max(1f, interval);
        }
    }

    string PickIdleThought(ActorInstance a)
    {
        if (a == null || a.def == null) return null;
        var happy = a.def.happyIdleThoughts;
        var clumsy = a.def.clumsyIdleThoughts;
        bool hasHappy = happy != null && happy.Length > 0;
        bool hasClumsy = clumsy != null && clumsy.Length > 0;
        if (!hasHappy && !hasClumsy) return null;

        float happyWeight = hasHappy ? a.EffectiveHappiness : 0f;
        float clumsyWeight = hasClumsy ? Mathf.Clamp01(a.def.clumsiness) : 0f;
        float total = happyWeight + clumsyWeight;
        if (total <= 0f)
        {
            happyWeight = hasHappy ? 1f : 0f;
            clumsyWeight = hasClumsy ? 1f : 0f;
            total = happyWeight + clumsyWeight;
        }

        bool chooseHappy = hasHappy && (!hasClumsy || rng.NextDouble() < (happyWeight / total));
        var pool = chooseHappy ? happy : clumsy;
        return pool != null && pool.Length > 0 ? PickRandom(new List<string>(pool)) : null;
    }

    void GetIdleThoughtRange(ActorInstance a, out float min, out float max)
    {
        if (a?.def != null)
        {
            min = Mathf.Max(1f, a.def.idleThoughtIntervalMinSeconds);
            max = Mathf.Max(min, a.def.idleThoughtIntervalMaxSeconds);
        }
        else
        {
            min = 17f;
            max = 35f;
        }

        if (max < min) max = min;
    }

    IEnumerable<string> PickTaskSpecific(
        string[] fallback,
        string task,
        string[] idle,
        string[] travel,
        string[] forage,
        string[] sell,
        string[] research,
        string[] craft,
        string[] tavern)
    {
        string[] pick = null;
        switch (task)
        {
            case null:
            case "":
            case "idle": pick = idle; break;
            case "forage": pick = forage; break;
            case "sell": pick = sell; break;
            case "research": pick = research; break;
            case "craft": pick = craft; break;
            case "tavern": pick = tavern; break;
            case "travel": pick = travel; break;
        }

        if (pick != null && pick.Length > 0) return pick;
        return fallback;
    }

    void UnlockRecipe(ShapedRecipe r)
    {
        if (!r) return;
        Debug.Log($"ActorManager.UnlockRecipe called for: {r.name}");
        var svc = RecipeCatalogService.Instance ?? FindObjectOfType<RecipeCatalogService>();
        if (svc == null)
        {
            Debug.Log("ActorManager.UnlockRecipe: no RecipeCatalogService found, creating transient one.");
            var go = new GameObject("RecipeCatalogService");
            svc = go.AddComponent<RecipeCatalogService>();
        }
        bool added = false;
        if (svc != null)
        {
            Debug.Log($"ActorManager.UnlockRecipe: calling AddRecipe({r.name}) on service {svc.GetInstanceID()}");
            added = svc.AddRecipe(r);
            Debug.Log($"ActorManager.UnlockRecipe: AddRecipe returned {added}");
        }
        if (added)
        {
            ToastSystem.Success("New recipe discovered", r.name);
        }
        else
        {
            // already known (or no service present) => just log
            Debug.Log($"Recipe already known or no service: {r.name}");
            try
            {
                // Dump service contents and crafting manager lists to help diagnosis
                if (svc != null) svc.DebugDumpKnownRecipes();

                var mgrs = FindObjectsOfType<CraftingManager>();
                Debug.Log($"ActorManager.UnlockRecipe: found {mgrs.Length} CraftingManager(s) in scene");
                foreach (var m in mgrs)
                {
                    if (m == null) continue;
                    var list = m.Recipes;
                    Debug.Log($" - CraftingManager (id={m.GetInstanceID()}): { (list == null ? 0 : list.Count) } recipes");
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var rr = list[i];
                            if (rr == null) { Debug.Log("   * <null>"); continue; }
                            string line = $"   * {i}: {rr.name} (id={rr.GetInstanceID()})";
#if UNITY_EDITOR
                            try { line += " path=" + UnityEditor.AssetDatabase.GetAssetPath(rr); } catch { }
#endif
                            Debug.Log(line);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("ActorManager.UnlockRecipe: diagnostic dump failed: " + ex);
            }
        }
    }

    void UnlockProducer(ProducerDefinition p)
    {
        ToastSystem.Success("Spirit generator found", p.displayName);
    }

    void GiveProducerToActor(ActorInstance a, ProducerDefinition p)
    {
        if (a == null || p == null) return;

        // Try to find an existing Item asset that references this ProducerDefinition.
        Item foundItem = null;

        // First try Resources (runtime-friendly). If your project stores Item assets in Resources, they'll be found here.
        try
        {
            var items = Resources.LoadAll<Item>("");
            if (items != null && items.Length > 0)
            {
                foreach (var it in items)
                {
                    if (it != null && it.producer == p)
                    {
                        foundItem = it;
                        break;
                    }
                }
            }
        }
        catch { /* ignore resource load errors */ }

        // If not found, try to see if any startItems in GlobalInventoryService match (common pattern)
        if (foundItem == null)
        {
            var gInvSvc = FindObjectOfType<GlobalInventoryService>();
            if (gInvSvc != null && gInvSvc.startItems != null)
            {
                foreach (var it in gInvSvc.startItems)
                {
                    if (it != null && it.producer == p)
                    {
                        foundItem = it;
                        break;
                    }
                }
            }
        }

        // If still not found, create a transient Item instance that mirrors the producer
        bool createdTransient = false;
        if (foundItem == null)
        {
            var temp = ScriptableObject.CreateInstance<Item>();
            temp.Id = p.id ?? ("prod_" + p.displayName?.Replace(" ", "_")?.ToLowerInvariant());
            temp.DisplayName = p.displayName ?? "Spirit Producer";
            temp.Icon = p.icon;
            temp.MaxStack = 1;
            temp.producer = p;
            foundItem = temp;
            createdTransient = true;

#if UNITY_EDITOR
            // Create a persistent asset under Assets/Generated/Producers so designers can tweak later
            try
            {
                var folder = "Assets/Generated/Producers";
                if (!System.IO.Directory.Exists(folder))
                {
                    System.IO.Directory.CreateDirectory(folder);
                    UnityEditor.AssetDatabase.Refresh();
                }
                string safeName = (temp.Id ?? "producer").Replace(" ", "_");
                string path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(System.IO.Path.Combine(folder, safeName + ".asset"));
                UnityEditor.AssetDatabase.CreateAsset(temp, path);
                UnityEditor.AssetDatabase.SaveAssets();
                UnityEditor.AssetDatabase.Refresh();
                Debug.Log($"Created producer Item asset at {path}");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to create persistent Item asset for producer: {ex}");
            }
#endif
        }

        // Add to actor backpack
        if (a.backpack == null) a.EnsureBackpackCapacity();
        if (a.backpack != null && foundItem != null)
        {
            a.backpack.Add(foundItem, 1);
            a.AddStatement(Stamp($"Placed {foundItem.DisplayName} into backpack."));
        }

        // Do NOT add discovered producer directly to player inventory.
        // Let the actor carry it in their backpack and drop/deliver it through normal workflows.
    }

    // --- Skills ---
    int ApplyForageSkill(int baseAmount)
    {
        float chance = 0f, multiplier = 1f;
        if (playerProgress != null)
            playerProgress.GetForageBonuses(out chance, out multiplier);

        if (chance <= 0f || multiplier <= 1f) return baseAmount;
        return (rng.NextDouble() < chance) ? Mathf.RoundToInt(baseAmount * multiplier) : baseAmount;
    }

    float GetTravelSpeedMultiplier(ActorInstance a)
    {
        if (a == null) return 1f;
        var b = a.GetEquipmentTotals();
        return Mathf.Max(0.1f, 1f + b.travelSpeed);
    }

    float GetCraftSpeedMultiplier(ActorInstance a)
    {
        if (a == null) return 1f;
        var b = a.GetEquipmentTotals();
        return Mathf.Max(0.1f, 1f + b.craftingSpeed);
    }

    float GetResearchSpeedMultiplier(ActorInstance a)
    {
        if (a == null) return 1f;
        var b = a.GetEquipmentTotals();
        return Mathf.Max(0.1f, 1f + b.researchSpeed);
    }

    float GetForageSpeedMultiplier(ActorInstance a)
    {
        if (a == null) return 1f;
        var b = a.GetEquipmentTotals();
        return Mathf.Max(0.1f, 1f + b.forageSpeed);
    }

    float GetForageLuckBonus(ActorInstance a)
    {
        if (a == null) return 0f;
        return Mathf.Max(0f, a.GetEquipmentTotals().forageLuck);
    }

    float GetTavernLuckBonus(ActorInstance a)
    {
        if (a == null) return 0f;
        return Mathf.Max(0f, a.GetEquipmentTotals().tavernLuck);
    }

    float GetResearchLuckBonus(ActorInstance a)
    {
        if (a == null) return 0f;
        return Mathf.Max(0f, a.GetEquipmentTotals().researchLuck);
    }

    string Stamp(string message)
    {
        if (calendar == null) return message;
        string season = calendar.CurrentSeasonName;
        int day = calendar.DayInSeason;
        int year = calendar.Year;
        return $"[Day {day} {season} Y{year}] {message}";
    }
}
