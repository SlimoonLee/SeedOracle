using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.Fonts;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using SeedOracle.Forecasting;

namespace SeedOracle.UI;

internal sealed partial class RoutePlanPanelControl
{
    private static void AddCombatRewardControls(
        VBoxContainer choiceBox, CombatRewardDetails rewards,
        RoutePlanChoice.Combat combatChoice, Func<RoutePlanChoice.Combat> currentCombat,
        Action<RoutePlanChoice.Combat> update, Player? planningPlayer, bool chinese)
    {
        var player = planningPlayer;
        var groups = rewards.CardRewardGroups;
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese ? $"卡牌奖励 {groupIndex + 1}"
                    : $"Card reward {groupIndex + 1}",
                15, new Color(0.68f, 0.78f, 0.8f)));
            for (var stepIndex = 0; stepIndex < group.Steps.Count; stepIndex++)
            {
                var step = group.Steps[stepIndex];
                var select = new OptionButton
                {
                    MouseFilter = MouseFilterEnum.Stop,
                    FocusMode = FocusModeEnum.None,
                    FitToLongestItem = false,
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                    TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
                };
                select.AddThemeFontSizeOverride("font_size", 17);
                select.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                select.AddItem(chinese ? "未选择" : "Unselected");
                foreach (var card in step.Cards)
                    select.AddItem(chinese ? $"拿 {card.Name}" : $"Take {card.Name}");
                foreach (var alternative in step.Alternatives)
                    select.AddItem(alternative.Title);
                select.Select(step.SelectedIndex + 1);
                select.TooltipText = select.GetItemText(select.Selected);
                var capturedGroup = groupIndex;
                var capturedStep = stepIndex;
                select.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
                {
                    var selected = (int)index - 1;
                    var current = currentCombat();
                    var picks = current.ChoiceForGroup(capturedGroup).Steps.Take(capturedStep).ToList();
                    if (selected >= 0 && selected < step.Cards.Count)
                        picks.Add(new CardRewardPick(selected, step.Cards[selected].Id));
                    else if (selected >= step.Cards.Count)
                        picks.Add(new CardRewardPick(AlternativeId:
                            step.Alternatives[selected - step.Cards.Count].OptionId));
                    update(current.WithCardRewardChoice(new RoutePlanChoice.CardReward(capturedGroup, picks)));
                });
                choiceBox.AddChild(select);
            }
        }

        if (rewards.Relics.Count > 0)
        {
            AddTakeToggle(
                choiceBox,
                chinese ? "拾取战斗遗物" : "Take combat relic",
                combatChoice.TakeRelic,
                take => update(currentCombat() with { TakeRelic = take }));
        }

        var rewardPotions = rewards.Potions;
        if (rewardPotions.Count > 0 && player is not null)
        {
            var potionChoice = combatChoice.PotionChoice;
            var hasExplicitPotionChoice = potionChoice is not null;
            var currentPotions = ResolvePlanningPotions(planningPlayer);
            var potionMax = planningPlayer?.MaxPotionCount
                                                        ?? player.MaxPotionCount;
            var freeSlots = potionMax - currentPotions.Count;
            var currentPotionText = currentPotions.Count == 0
                ? (chinese ? "无" : "none")
                : string.Join("、", currentPotions.Select(potion => potion.Name));
            choiceBox.AddChild(PreCombatPanelStyles.CreateLabel(
                chinese
                    ? $"领取奖励前药水：{currentPotionText}（{currentPotions.Count}/{potionMax}）"
                    : $"Potions before rewards: {currentPotionText} ({currentPotions.Count}/{potionMax})",
                15,
                new Color(0.68f, 0.78f, 0.8f)));
            var potionToggles = new List<(int Index, CheckButton Button)>();
            for (var potionIndex = 0; potionIndex < rewardPotions.Count; potionIndex++)
            {
                var taken = potionIndex;
                var button = new CheckButton
                {
                    Text = $"{(chinese ? "拾取" : "Take")} {rewardPotions[potionIndex].Name}",
                    ButtonPressed = !hasExplicitPotionChoice
                        ? potionIndex < freeSlots
                        : potionChoice?.TakenPotions.Contains(potionIndex) == true,
                    MouseFilter = MouseFilterEnum.Stop,
                    FocusMode = FocusModeEnum.None
                };
                button.AddThemeFontSizeOverride("font_size", 17);
                button.ApplyLocaleFontSubstitution(FontType.Regular, "font");
                button.Toggled += _ => update(currentCombat() with
                {
                    PotionChoice = new RoutePlanChoice.Potion(
                        potionToggles.Where(pair => pair.Button.ButtonPressed)
                            .Select(pair => pair.Index)
                            .ToArray(),
                        potionChoice?.DiscardPotion)
                });
                potionToggles.Add((taken, button));
                choiceBox.AddChild(button);
            }

            var discardPicker = new OptionButton
            {
                MouseFilter = MouseFilterEnum.Stop,
                FocusMode = FocusModeEnum.None
            };
            discardPicker.AddThemeFontSizeOverride("font_size", 17);
            discardPicker.ApplyLocaleFontSubstitution(FontType.Regular, "font");
            discardPicker.AddItem(chinese ? "满槽时丢弃…" : "Discard when full…");
            foreach (var potion in currentPotions)
                discardPicker.AddItem(potion.Name);
            var discardIndex = hasExplicitPotionChoice && potionChoice?.DiscardPotion is { } discardId
                ? 1 + currentPotions.ToList().FindIndex(potion =>
                    SameModelId(potion.Id, discardId) || SamePotionEntry(potion.Id, discardId))
                : 0;
            discardPicker.Select(discardIndex < 0 ? 0 : discardIndex);
            discardPicker.ItemSelected += (OptionButton.ItemSelectedEventHandler)(index =>
            {
                ModelId? discard = index <= 0 || (int)index - 1 >= currentPotions.Count
                    ? null
                    : currentPotions[(int)index - 1].Id;
                update(currentCombat() with
                {
                    PotionChoice = new RoutePlanChoice.Potion(
                        potionToggles.Where(pair => pair.Button.ButtonPressed)
                            .Select(pair => pair.Index)
                            .ToArray(),
                        discard)
                });
            });
            choiceBox.AddChild(discardPicker);
        }
    }
}
