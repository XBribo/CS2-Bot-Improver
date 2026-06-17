using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;

namespace ProOpeningReplay;

public sealed partial class ProOpeningReplayPlugin
{
    private static EquipmentValues EstimateCurrentEquipment(CCSPlayerController player)
    {
        var primaryValue = 0;
        var utilityValue = 0;
        var weaponValue = 0;
        foreach (var itemName in GetCurrentInventory(player))
        {
            var price = ItemPrice(itemName);
            weaponValue += price;
            if (PrimaryWeapons.Contains(itemName))
            {
                primaryValue += price;
            }
            else if (UtilityItems.Contains(itemName))
            {
                utilityValue += price;
            }
        }

        var pawn = player.PlayerPawn.Value;
        var armorValue = 0;
        if (pawn != null && pawn.IsValid)
        {
            var hasHelmet = false;
            if (pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero)
            {
                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                hasHelmet = itemServices.HasHelmet;
                if (player.Team == CsTeam.CounterTerrorist && itemServices.HasDefuser)
                {
                    weaponValue += ItemPrice("item_defuser");
                }
            }

            if (pawn.ArmorValue > 0)
            {
                armorValue = hasHelmet ? ItemPrice("item_assaultsuit") : ItemPrice("item_kevlar");
            }
        }

        return new EquipmentValues(weaponValue + armorValue, primaryValue, utilityValue, armorValue);
    }

    private static EquipmentValues EstimateCurrentBudgetEquipment(CCSPlayerController player)
    {
        var primaryValue = 0;
        var utilityValue = 0;
        var weaponValue = 0;
        foreach (var itemName in GetCurrentInventory(player))
        {
            var price = BudgetItemValue(itemName);
            weaponValue += price;
            if (PrimaryWeapons.Contains(itemName))
            {
                primaryValue += price;
            }
            else if (UtilityItems.Contains(itemName))
            {
                utilityValue += price;
            }
        }

        var pawn = player.PlayerPawn.Value;
        var armorValue = 0;
        if (pawn != null && pawn.IsValid)
        {
            var hasHelmet = false;
            if (pawn.ItemServices != null && pawn.ItemServices.Handle != IntPtr.Zero)
            {
                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                hasHelmet = itemServices.HasHelmet;
                if (player.Team == CsTeam.CounterTerrorist && itemServices.HasDefuser)
                {
                    weaponValue += BudgetItemValue("item_defuser");
                }
            }

            if (pawn.ArmorValue > 0)
            {
                armorValue = hasHelmet ? BudgetItemValue("item_assaultsuit") : BudgetItemValue("item_kevlar");
            }
        }

        return new EquipmentValues(weaponValue + armorValue, primaryValue, utilityValue, armorValue);
    }

    public static int ReplayLoadoutValue(ReplayPlayer replayPlayer)
    {
        var value = BuildReplayLoadoutItems(replayPlayer).Sum(pair => BudgetItemValue(pair.Key) * pair.Value);
        if (replayPlayer.ArmorValue > 0)
        {
            value += replayPlayer.HasHelmet ? BudgetItemValue("item_assaultsuit") : BudgetItemValue("item_kevlar");
        }
        if (replayPlayer.HasDefuser)
        {
            value += BudgetItemValue("item_defuser");
        }
        return value;
    }

    public static bool ReplayUsesPrimaryWeapon(ReplayPlayer replayPlayer)
    {
        if (BuildReplayLoadoutItems(replayPlayer).Keys.Any(itemName => PrimaryWeapons.Contains(itemName)))
        {
            return true;
        }

        if (replayPlayer.InventoryDefIndexes.Any(defIndex => PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(defIndex))))
        {
            return true;
        }

        if (PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(replayPlayer.FirstWeaponDefIndex)))
        {
            return true;
        }

        return replayPlayer.PreloadWeaponDefIndexes.Any(defIndex => PrimaryWeaponDefIndexes.Contains(NormalizeWeaponDefIndex(defIndex)));
    }

    private static int BudgetItemValue(string itemName)
    {
        var normalized = NormalizeLoadoutItem(itemName);
        return IsFreeDefaultPistol(normalized) ? 0 : ItemPrice(normalized);
    }

    private static bool IsBudgetWeapon(string itemName)
    {
        var normalized = NormalizeLoadoutItem(itemName);
        return normalized.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("knife", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            && !normalized.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFreeDefaultPistol(string itemName)
    {
        return itemName.Equals("weapon_glock", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_hkp2000", StringComparison.OrdinalIgnoreCase)
            || itemName.Equals("weapon_usp_silencer", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int> BuildReplayLoadoutItems(ReplayPlayer replayPlayer)
    {
        var targetItems = CountItems(replayPlayer.Inventory
            .Select(NormalizeLoadoutItem)
            .Where(IsReplayLoadoutItem));
        MergeReplayLoadoutDefs(targetItems, replayPlayer.InventoryDefIndexes);
        MergeReplayLoadoutDefs(targetItems, [replayPlayer.FirstWeaponDefIndex]);

        if (!targetItems.Keys.Any(itemName => SecondaryWeapons.Contains(itemName)))
        {
            var defaultPistol = DefaultPistolForTeam((CsTeam)replayPlayer.TeamNum);
            if (defaultPistol != null)
            {
                targetItems[defaultPistol] = Math.Max(1, targetItems.GetValueOrDefault(defaultPistol));
            }
        }

        return targetItems;
    }

    private static void MergeReplayLoadoutDefs(Dictionary<string, int> targetItems, IEnumerable<int> defIndexes)
    {
        var defCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var defIndex in defIndexes)
        {
            if (!TryGetWeaponClassByDefIndex(defIndex, out var className))
            {
                continue;
            }

            var itemName = NormalizeLoadoutItem(className);
            if (!IsReplayLoadoutItem(itemName))
            {
                continue;
            }

            defCounts[itemName] = defCounts.GetValueOrDefault(itemName) + 1;
        }

        foreach (var (itemName, count) in defCounts)
        {
            targetItems[itemName] = Math.Max(targetItems.GetValueOrDefault(itemName), count);
        }
    }

    private void SwitchToReplayLoadoutStartWeapon(CCSPlayerController player, ReplayPlayer replayPlayer)
    {
        var defIndex = ChooseLoadoutStartWeaponDef(replayPlayer);
        if (defIndex < 0 || player.Slot < 0)
        {
            return;
        }

        if (_nativeReplayAvailable && BotController.SwitchBotWeapon(player.Slot, NativeWeaponDefIndex(defIndex)))
        {
            return;
        }

        if (player.UserId != null && TryGetWeaponClassByDefIndex(defIndex, out var className))
        {
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {className}");
        }
    }

    private void SwitchToBestGunForHandoff(CCSPlayerController player)
    {
        if (player.Slot < 0)
        {
            return;
        }

        var pawn = player.PlayerPawn.Value;
        var weaponServices = pawn?.WeaponServices;
        if (pawn == null || !pawn.IsValid || weaponServices == null)
        {
            return;
        }

        var active = weaponServices.ActiveWeapon.Value;
        if (active != null && active.IsValid
            && GetReplayWeaponSlot(active.DesignerName) is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary)
        {
            return;
        }

        CBasePlayerWeapon? primary = null;
        CBasePlayerWeapon? secondary = null;
        foreach (var handle in weaponServices.MyWeapons)
        {
            var weapon = handle.Value;
            if (weapon == null || !weapon.IsValid)
            {
                continue;
            }

            switch (GetReplayWeaponSlot(weapon.DesignerName))
            {
                case ReplayWeaponSlot.Primary when primary == null:
                    primary = weapon;
                    break;
                case ReplayWeaponSlot.Secondary when secondary == null:
                    secondary = weapon;
                    break;
            }
        }

        var target = primary ?? secondary;
        if (target == null)
        {
            return;
        }

        var defIndex = WeaponDefIndex(target.DesignerName);
        if (_nativeReplayAvailable && BotController.SwitchBotWeapon(player.Slot, NativeWeaponDefIndex(defIndex)))
        {
            return;
        }

        weaponServices.ActiveWeapon.Raw = target.EntityHandle.Raw;
        Utilities.SetStateChanged(pawn, "CBasePlayerPawn", "m_pWeaponServices");

        if (player.UserId != null)
        {
            NativeAPI.IssueClientCommand(player.UserId.Value, $"use {target.DesignerName}");
        }
    }

    private static int ChooseLoadoutStartWeaponDef(ReplayPlayer replayPlayer)
    {
        var fallback = -1;
        foreach (var defIndex in ReplayPlayerWeaponDefs(replayPlayer))
        {
            var normalized = NormalizeWeaponDefIndex(defIndex);
            if (!IsKnownWeaponDefIndex(normalized))
            {
                continue;
            }

            if (!TryGetWeaponClassByDefIndex(normalized, out var className))
            {
                continue;
            }

            var slot = GetReplayWeaponSlot(className);
            if (slot is ReplayWeaponSlot.Primary or ReplayWeaponSlot.Secondary)
            {
                return normalized;
            }

            if (fallback < 0 && slot is ReplayWeaponSlot.Utility or ReplayWeaponSlot.Taser)
            {
                fallback = normalized;
            }
        }

        return fallback;
    }

    private static bool IsReplayLoadoutItem(string itemName)
    {
        return IsGiveableItem(itemName)
            && !itemName.Contains("knife", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_bayonet", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
            && !itemName.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase)
            && ItemPrices.ContainsKey(itemName);
    }

    private static string NormalizeLoadoutItem(string itemName)
    {
        return itemName switch
        {
            "weapon_decoy_grenade" => "weapon_decoy",
            "weapon_c4_explosive" => "weapon_c4",
            _ => itemName
        };
    }

    private static List<string> GetCurrentInventory(CCSPlayerController player)
    {
        var pawn = player.PlayerPawn.Value;
        if (pawn?.WeaponServices == null)
        {
            return [];
        }

        return pawn.WeaponServices.MyWeapons
            .Select(handle => handle.Value)
            .Where(weapon => weapon != null && weapon.IsValid)
            .Select(weapon => weapon!.DesignerName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Where(name => !name.Equals("weapon_knife", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_knife_t", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_c4", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("weapon_c4_explosive", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static Dictionary<string, int> CountItems(IEnumerable<string> itemNames)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var itemName in itemNames.Where(IsGiveableItem))
        {
            counts[itemName] = counts.GetValueOrDefault(itemName) + 1;
        }

        return counts;
    }

    private static string? BestPrimary(IEnumerable<string> itemNames)
    {
        return itemNames
            .Where(itemName => PrimaryWeapons.Contains(itemName))
            .OrderByDescending(ItemPrice)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? BestSecondary(IEnumerable<string> itemNames)
    {
        return itemNames
            .Where(itemName => SecondaryWeapons.Contains(itemName))
            .OrderByDescending(ItemPrice)
            .ThenBy(itemName => itemName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string? CurrentDefaultPistol(CsTeam team, IEnumerable<string> itemNames)
    {
        return itemNames.FirstOrDefault(itemName => IsDefaultPistolForTeam(team, itemName));
    }

    private static string? DefaultPistolForTeam(CsTeam team)
    {
        return team switch
        {
            CsTeam.Terrorist => "weapon_glock",
            CsTeam.CounterTerrorist => "weapon_hkp2000",
            _ => null
        };
    }

    private static bool IsDefaultPistolForTeam(CsTeam team, string itemName)
    {
        return team switch
        {
            CsTeam.Terrorist => itemName.Equals("weapon_glock", StringComparison.OrdinalIgnoreCase),
            CsTeam.CounterTerrorist => itemName.Equals("weapon_hkp2000", StringComparison.OrdinalIgnoreCase)
                || itemName.Equals("weapon_usp_silencer", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool CanPreservePrimary(string currentPrimary, string targetPrimary)
    {
        if (currentPrimary.Equals(targetPrimary, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (targetPrimary.Equals("weapon_awp", StringComparison.OrdinalIgnoreCase) && !currentPrimary.Equals("weapon_awp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (RifleLikeWeapons.Contains(currentPrimary) && RifleLikeWeapons.Contains(targetPrimary))
        {
            return true;
        }

        return ItemPrice(currentPrimary) >= ItemPrice(targetPrimary);
    }

    private static int ItemPrice(string itemName)
    {
        return ItemPrices.GetValueOrDefault(itemName);
    }

    public static int RoundMoneyDown(int money)
    {
        return Math.Max(0, money / 50 * 50);
    }

}
